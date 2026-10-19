namespace Corpus;

// Deep-verify on a real seeded store: FileBlockSource .img plays the volume
// (same stand-in the imaging loop already proves), ChunkMib 1 keeps the
// grid small and fast.
public sealed class DeepVerifyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public DeepVerifyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_deepverify_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Distinct random chunks: dedup can't collapse the grid, so every
    // manifest entry maps to its own pack bytes.
    private SnapshotManifest SeedChunks(int chunkCount)
    {
        var bytes = new byte[chunkCount * 1024 * 1024];
        new Random(4242).NextBytes(bytes);
        File.WriteAllBytes(_volPath, bytes);
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using var source = new FileBlockSource(_volPath);
        return store.Image(source, "seed");
    }

    [Fact]
    public void DeepVerify_PercentZero_NoopReturnsZeroChecked()
    {
        var manifest = SeedChunks(3);
        var log = new TestLogger();

        var summary = DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, 0, log);

        Assert.Equal(0, summary.Checked);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.Ok);
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("ERR:"));
    }

    [Fact]
    public void DeepVerify_Percent100_AllGreenOnSeededStore()
    {
        var manifest = SeedChunks(3);
        var log = new TestLogger();

        var summary = DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, 100, log);

        Assert.Equal(3, summary.Checked);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.Ok);
        Assert.Contains(log.Messages, m => m.Contains("3/3 chunks OK"));
    }

    [Fact]
    public void DeepVerify_InjectedCorruption_DetectedNamingChunk()
    {
        var manifest = SeedChunks(3);
        var pack = Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Order().First();
        using (var stream = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Seek(45, SeekOrigin.Begin);
            stream.WriteByte((byte)(stream.ReadByte() ^ 0xFF));
        }
        var log = new TestLogger();

        var summary = DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, 100, log);

        Assert.False(summary.Ok);
        Assert.Equal(1, summary.Failed);
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:") && m.Contains($"snapshot {manifest.Generation} chunk"));
    }

    [Fact]
    public void DeepVerify_Sampling_DeterministicAcrossRuns()
    {
        var manifest = SeedChunks(10);

        var first = DeepVerifyJob.SelectCandidates(manifest.Chunks, 30, manifest.Generation);
        var second = DeepVerifyJob.SelectCandidates(manifest.Chunks, 30, manifest.Generation);

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DeepVerify_PartialPercent_ChecksCeiledCount()
    {
        var manifest = SeedChunks(10);
        var log = new TestLogger();

        var summary = DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, 30, log);

        Assert.Equal(3, summary.Checked);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(10, summary.TotalChunks);
    }

    [Fact]
    public void DeepVerify_PercentOutOfRange_Throws()
    {
        var manifest = SeedChunks(2);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, 101, new TestLogger()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepVerifyJob.Run(_storeRoot, _dbPath, manifest, -1, new TestLogger()));
    }
}
