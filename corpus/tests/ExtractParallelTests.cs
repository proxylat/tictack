namespace Corpus;

// Prefetch + digest-cache pins for --extract: parallel fetch/decode must
// return byte-identical volume bytes at any worker count, the digest-keyed
// cache must dedup + stay under its MB cap, and holes must prefetch as no-ops.
public sealed class ExtractParallelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public ExtractParallelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_extractpar_" + Guid.NewGuid().ToString("N"));
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SnapshotVolumeReader.DecodeDopOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SnapshotManifest ImageBytes(byte[] data, string name)
    {
        var path = Path.Combine(_dir, name + ".img");
        File.WriteAllBytes(path, data);
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using var source = new FileBlockSource(path);
        return store.Image(source, name);
    }

    private static SnapshotManifest LoadManifest(string storeRoot, int generation)
    {
        var manifestsDir = Path.Combine(storeRoot, "manifests");
        foreach (var (gen, name) in ManifestStore.List(manifestsDir))
        {
            if (gen == generation)
                return ManifestStore.Load(Path.Combine(manifestsDir, $"{gen:D6}-{name}.json"), ChunkId.EnsureKey(storeRoot));
        }
        throw new InvalidDataException($"generation {generation} not found");
    }

    private static byte[] ReadAll(SnapshotVolumeReader vol)
    {
        var out_ = new byte[vol.Length];
        var pos = 0L;
        while (pos < vol.Length)
        {
            var take = (int)Math.Min(4 * 1024 * 1024, vol.Length - pos);
            vol.Read(pos, out_, (int)pos, take);
            pos += take;
        }
        return out_;
    }

    private static byte[] Compressible(int mib, int seed)
    {
        var rnd = new Random(seed);
        var words = "the quick brown fox jumps over the lazy dog 0123456789 "u8.ToArray();
        var data = new byte[mib * 1024 * 1024];
        for (var i = 0; i < data.Length; i++)
            data[i] = words[(i + rnd.Next(8)) % words.Length];
        return data;
    }

    [Fact]
    public void Prefetch_MatchesSerialRead_Zstd()
    {
        var data = Compressible(3, 7);
        var m = ImageBytes(data, "txt");
        using var serial = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
        var expect = ReadAll(serial);
        Assert.Equal(data, expect);

        using var prefetched = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
        prefetched.PrefetchRange(0, prefetched.Length);
        Assert.Equal(data, ReadAll(prefetched));
        Assert.Equal(m.Chunks.Where(c => !c.IsHole).Select(c => c.DigestHex).Distinct(StringComparer.Ordinal).Count(),
            prefetched.CachedChunkCount);
    }

    [Fact]
    public void Prefetch_ThreadCount_ByteIdentical()
    {
        var data = Compressible(3, 7);
        var m = ImageBytes(data, "txt");
        byte[][] outs = new byte[2][];
        foreach (var (slot, dop) in new[] { (0, 1), (1, 16) })
        {
            SnapshotVolumeReader.DecodeDopOverride = dop;
            using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
            vol.PrefetchRange(0, vol.Length);
            outs[slot] = ReadAll(vol);
        }
        Assert.Equal(data, outs[0]);
        Assert.Equal(outs[0], outs[1]);
    }

    [Fact]
    public void Prefetch_RawCodec_MatchesSerial()
    {
        var data = new byte[3 * 1024 * 1024];
        new Random(1234).NextBytes(data);
        var m = ImageBytes(data, "rnd");
        using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
        vol.PrefetchRange(0, vol.Length);
        Assert.Equal(data, ReadAll(vol));
    }

    [Fact]
    public void Cache_DedupesIdenticalChunks()
    {
        var block = Compressible(1, 7);
        var data = new byte[4 * block.Length];
        for (var i = 0; i < 4; i++)
            Buffer.BlockCopy(block, 0, data, i * block.Length, block.Length);
        var m = ImageBytes(data, "dup");
        var distinct = m.Chunks.Where(c => !c.IsHole).Select(c => c.DigestHex).Distinct(StringComparer.Ordinal).Count();
        using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
        vol.PrefetchRange(0, vol.Length);
        Assert.Equal(data, ReadAll(vol));
        Assert.Equal(distinct, vol.CachedChunkCount); // content-addressed: one entry per digest
    }

    [Fact]
    public void Cache_StaysUnderMbCap()
    {
        var data = new byte[70 * 1024 * 1024];
        new Random(99).NextBytes(data);
        var m = ImageBytes(data, "big");
        using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, LoadManifest(_storeRoot, m.Generation));
        vol.PrefetchRange(0, vol.Length);
        Assert.True(vol.CachedBytes <= 64L * 1024 * 1024, $"cached {vol.CachedBytes} bytes exceeds 64 MiB cap");
        Assert.Equal(data, ReadAll(vol)); // eviction stays correct: misses re-fetch
    }

    [Fact]
    public void Prefetch_HolesAreNoOp()
    {
        const int Mib = 1024 * 1024;
        var m = ManifestStore.Build(1, "holes", 3L * Mib, 1, 3, "key-1", "",
            new List<ManifestEntry> { ManifestEntry.Hole(Mib), ManifestEntry.Hole(Mib), ManifestEntry.Hole(Mib) });
        using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, m);
        vol.PrefetchRange(0, vol.Length);
        Assert.Equal(0, vol.CachedChunkCount);
        Assert.Equal(new byte[3 * Mib], ReadAll(vol));
    }
}
