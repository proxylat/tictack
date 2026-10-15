namespace Corpus;

// Delta-transfer lane: two real stores in temp dirs, real chunk index,
// no mocks. Small volumes (1 MiB chunks) keep it fast.
public sealed class TransferTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _aRoot;
    private readonly string _aDb;
    private readonly string _bRoot;
    private readonly string _bDb;
    private readonly TestLogger _log = new();

    public TransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_xfer_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _aRoot = Path.Combine(_dir, "a");
        _aDb = Path.Combine(_dir, "a-state", "corpus-chunks.db");
        _bRoot = Path.Combine(_dir, "b");
        _bDb = Path.Combine(_dir, "b-state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        WriteChunks(_volPath, RandomChunk(1), RandomChunk(2), RandomChunk(3));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    private static FakeChangeSource Dirty(params long[] chunks) =>
        new((_, _, _) => new IncrementalPlan(new HashSet<long>(chunks), "fake:1"));

    [Fact]
    public void Sync_CopiesEverything_SecondRunNoop()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.Image(source, "gen1");

        var r = StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log);
        Assert.Equal(1, r.ManifestsCopied);
        Assert.True(r.PacksCopied >= 1);
        // Fresh dest gets the source identity (same key = same addresses).
        Assert.Equal(File.ReadAllBytes(Path.Combine(_aRoot, "id.key")), File.ReadAllBytes(Path.Combine(_bRoot, "id.key")));

        using (var b = new Store(_bRoot, _bDb, 1, 3, _log))
        {
            Assert.True(b.Verify(1));
            var outPath = Path.Combine(_dir, "out.img");
            b.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        }

        var r2 = StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log);
        Assert.Equal(new TransferResult(0, 0, 0, 0, 0), r2);
    }

    [Fact]
    public void Sync_MovesOnlyMissingPacks()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.Image(source, "gen1");
        StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log);

        var mutated = RandomChunk(999);
        using (var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(Mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.Image(source, "gen2", new ImageOptions(Incremental: true, ChangeSource: Dirty(1)));

        var srcPacks = Directory.GetFiles(Path.Combine(_aRoot, "packs"), "pack-*.pack");
        var r = StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log);
        Assert.Equal(1, r.ManifestsCopied);
        Assert.Equal(1, r.PacksCopied);
        Assert.True(r.PacksCopied < srcPacks.Length);
        Assert.Equal(Directory.GetFiles(Path.Combine(_aRoot, "packs")).Length,
            Directory.GetFiles(Path.Combine(_bRoot, "packs")).Length);

        using var b = new Store(_bRoot, _bDb, 1, 3, _log);
        Assert.True(b.Verify(2));
        var outPath = Path.Combine(_dir, "out2.img");
        b.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Copy_SingleSnapshot_WithAndWithoutChain()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        {
            using (var source = new FileBlockSource(_volPath))
                a.Image(source, "gen1");
            var mutated = RandomChunk(999);
            using (var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                f.Seek(Mib, SeekOrigin.Begin);
                f.Write(mutated, 0, mutated.Length);
            }
            using (var source = new FileBlockSource(_volPath))
                a.Image(source, "gen2", new ImageOptions(Incremental: true, ChangeSource: Dirty(1)));
        }

        var cRoot = Path.Combine(_dir, "c");
        var cDb = Path.Combine(_dir, "c-state", "corpus-chunks.db");
        var rc = StoreTransfer.Copy(_aRoot, _aDb, cRoot, cDb, new TransferOptions(Snapshot: 2, IncludeChain: true), _log);
        Assert.Equal(2, rc.ManifestsCopied);
        using (var c = new Store(cRoot, cDb, 1, 3, _log))
        {
            Assert.True(c.Verify(1));
            Assert.True(c.Verify(2));
        }

        // Child manifests are self-contained: gen2 alone still verifies.
        var dRoot = Path.Combine(_dir, "d");
        var dDb = Path.Combine(_dir, "d-state", "corpus-chunks.db");
        var rd = StoreTransfer.Copy(_aRoot, _aDb, dRoot, dDb, new TransferOptions(Snapshot: 2), _log);
        Assert.Equal(1, rd.ManifestsCopied);
        using (var d = new Store(dRoot, dDb, 1, 3, _log))
        {
            Assert.Single(d.ListSnapshots());
            Assert.True(d.Verify(2));
        }
    }

    [Fact]
    public void Copy_FastLanding_TransfersLooseChunks()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.ImageFast(source, "fast1");

        var r = StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log);
        Assert.Equal(1, r.ManifestsCopied);
        Assert.Equal(0, r.PacksCopied);
        Assert.Equal(3, r.LandingCopied);

        using var b = new Store(_bRoot, _bDb, 1, 3, _log);
        Assert.True(b.Verify(1));
        var outPath = Path.Combine(_dir, "out-fast.img");
        b.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Copy_KeyMismatch_FailsLoud()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.Image(source, "gen1");
        using (var b = new Store(_bRoot, _bDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            b.Image(source, "other");
        Assert.Throws<InvalidDataException>(() => StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, null, _log));
    }

    [Fact]
    public void Copy_SameRoot_Fails()
    {
        Assert.Throws<InvalidOperationException>(() => StoreTransfer.Copy(_aRoot, _aDb, _aRoot, _aDb, null, _log));
    }

    [Fact]
    public void Copy_UnknownSnapshot_Fails()
    {
        using (var a = new Store(_aRoot, _aDb, 1, 3, _log))
        using (var source = new FileBlockSource(_volPath))
            a.Image(source, "gen1");
        Assert.Throws<InvalidDataException>(() =>
            StoreTransfer.Copy(_aRoot, _aDb, _bRoot, _bDb, new TransferOptions(Snapshot: 99), _log));
    }
}
