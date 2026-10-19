using System.Security.Cryptography;

namespace Corpus;

public sealed class ChunkerBuzhashTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;
    private readonly byte[] _key;

    public ChunkerBuzhashTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_bz_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        _key = new byte[32];
        new Random(99).NextBytes(_key);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Seeded(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static HashSet<string> Digests(IEnumerable<Chunk> chunks)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in chunks)
            set.Add(Convert.ToHexString(SHA256.HashData(c.Data)));
        return set;
    }

    [Fact]
    public void Fixed_OneByteInsert_RechunksEverythingAfter()
    {
        var a = Seeded(256 * 1024, 7);
        var b = new byte[a.Length + 1];
        Buffer.BlockCopy(a, 0, b, 0, 1000);
        b[1000] = 0xFF;
        Buffer.BlockCopy(a, 1000, b, 1001, a.Length - 1000);
        List<Chunk> ca, cb;
        using (var sa = new MemoryStream(a, false))
        using (var sb = new MemoryStream(b, false))
        {
            ca = Chunker.Read(sa, 4096).ToList();
            cb = Chunker.Read(sb, 4096).ToList();
        }
        Assert.Equal(64, ca.Count);
        Assert.Equal(65, cb.Count);
        // Fixed grid: same offsets, but every chunk at/after the insert
        // carries shifted bytes — zero shared content past the insert.
        Assert.Equal(ca.Select(c => c.Offset), cb.Take(64).Select(c => c.Offset));
        var shared = Digests(ca);
        shared.IntersectWith(Digests(cb));
        Assert.Empty(shared);
    }

    [Fact]
    public void BuzHash_Bounds_Contiguous_And_Deterministic()
    {
        var input = Seeded(2 * Mib, 11);
        const int avg = 256 * 1024, min = 64 * 1024, max = Mib;
        List<Chunk> first, second;
        using (var s = new MemoryStream(input, false))
            first = Chunker.ReadVariable(s, avg, min, max).ToList();
        using (var s = new MemoryStream(input, false))
            second = Chunker.ReadVariable(s, avg, min, max).ToList();
        Assert.True(first.Count > 1);
        var pos = 0L;
        var total = 0L;
        for (var i = 0; i < first.Count; i++)
        {
            var c = first[i];
            Assert.Equal(i, c.Index);
            Assert.Equal(pos, c.Offset);
            Assert.True(c.Data.Length >= 1 && c.Data.Length <= max);
            if (i < first.Count - 1)
                Assert.True(c.Data.Length >= min);
            pos += c.Data.Length;
            total += c.Data.Length;
        }
        Assert.Equal(input.Length, total);
        var concat = first.SelectMany(c => c.Data).ToArray();
        Assert.Equal(input, concat);
        // Average lands near target (half..double is a wide, stable band).
        var mean = (double)total / first.Count;
        Assert.InRange(mean, avg / 2.0, avg * 2.0);
        // Deterministic: same bytes → same boundaries, byte for byte.
        Assert.Equal(first.Select(c => c.Offset), second.Select(c => c.Offset));
        Assert.Equal(first.Select(c => c.Data.Length), second.Select(c => c.Data.Length));
        Assert.Equal(concat, second.SelectMany(c => c.Data).ToArray());
    }

    [Fact]
    public void BuzHash_OneByteInsert_PreservesDownstreamChunks()
    {
        var a = Seeded(256 * 1024, 7);
        var b = new byte[a.Length + 1];
        Buffer.BlockCopy(a, 0, b, 0, 1000);
        b[1000] = 0xFF;
        Buffer.BlockCopy(a, 1000, b, 1001, a.Length - 1000);
        const int avg = 16 * 1024, min = 4 * 1024, max = 64 * 1024;
        List<Chunk> ca, cb;
        using (var sa = new MemoryStream(a, false))
        using (var sb = new MemoryStream(b, false))
        {
            ca = Chunker.ReadVariable(sa, avg, min, max).ToList();
            cb = Chunker.ReadVariable(sb, avg, min, max).ToList();
        }
        var shared = Digests(ca);
        shared.IntersectWith(Digests(cb));
        // Only the chunks touching the insert (plus the shifted tail) can
        // differ; every other chunk is content-identical. Fixed shares 0.
        Assert.True(shared.Count >= ca.Count - 3, $"shared {shared.Count} of {ca.Count}");
    }

    [Fact]
    public void BuzHashBounds_MatchesPolicy()
    {
        Assert.Equal((Mib, 16 * Mib), Chunker.BuzHashBounds(4 * Mib));
        Assert.Equal((256 * 1024, 4 * Mib), Chunker.BuzHashBounds(Mib));
    }

    [Fact]
    public void BuzHash_Rejects_Bad_Sizes_Eagerly()
    {
        using var s = new MemoryStream(new byte[8]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Chunker.ReadVariable(s, 0, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Chunker.ReadVariable(s, 4096, 0, 8192));
        Assert.Throws<ArgumentOutOfRangeException>(() => Chunker.ReadVariable(s, 4096, 8192, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => Chunker.ReadVariable(s, 1024, 2048, 8192));
    }

    private void SeedVolume()
    {
        var random = Seeded(Mib, 42);
        using var f = new FileStream(_volPath, FileMode.Create, FileAccess.Write);
        f.Write(new byte[Mib], 0, Mib);
        f.Write(random, 0, Mib);
        f.Write(new byte[Mib], 0, Mib);
    }

    [Fact]
    public void BuzHash_Image_Export_Verify_RoundTrip()
    {
        SeedVolume();
        SnapshotManifest m;
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger(), chunker: "buzhash"))
        {
            using var source = new FileBlockSource(_volPath);
            m = store.Image(source, "bz1");
            Assert.Equal("buzhash", m.Chunker);
            Assert.Equal(3L * Mib, m.Chunks.Sum(e => e.Size));
            Assert.True(store.Verify(1));
            // Random-access reader path (cumulative offsets, not grid).
            using var vol = new SnapshotVolumeReader(_storeRoot, _dbPath, m);
            var expected = File.ReadAllBytes(_volPath);
            vol.PrefetchRange(0, expected.Length);
            var buf = new byte[expected.Length];
            vol.Read(0, buf, 0, buf.Length);
            Assert.Equal(expected, buf);
            var span = new byte[1000];
            vol.Read(100 * 1024 + 13, span, 0, span.Length);
            Assert.Equal(expected.AsSpan((100 * 1024) + 13, span.Length).ToArray(), span);
        }
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger(), chunker: "buzhash"))
        {
            var outPath = Path.Combine(_dir, "export.img");
            store.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
            Assert.True(store.Verify(1));
        }
    }

    [Fact]
    public void MixedRepo_Fixed_And_Buzhash_Read_Both_Ways()
    {
        SeedVolume();
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        {
            using var source = new FileBlockSource(_volPath);
            var m1 = store.Image(source, "gen1-fixed");
            Assert.Null(m1.Chunker);
            Assert.True(store.Verify(1));
        }
        // Old snapshot (no Chunker field on disk) reads under a buzhash
        // store; the next image falls back to full with its own shape.
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger(), chunker: "buzhash"))
        {
            Assert.True(store.Verify(1));
            var outPath = Path.Combine(_dir, "mixed1.img");
            store.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
            using var source = new FileBlockSource(_volPath);
            var m2 = store.Image(source, "gen2-buzhash");
            Assert.Equal(2, m2.Generation);
            Assert.Equal("buzhash", m2.Chunker);
        }
        // And the buzhash snapshot reads back under a fixed store.
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        {
            Assert.True(store.Verify(2));
            var outPath = Path.Combine(_dir, "mixed2.img");
            store.Export(2, outPath);
            Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void Manifest_Chunker_Omitted_When_Fixed()
    {
        var m = ManifestStore.Build(1, "n", 100, 4, 3, "key-1", "",
            new List<ManifestEntry> { new(new string('a', 64), 100) });
        var path = ManifestStore.Save(_dir, m, _key);
        Assert.DoesNotContain("Chunker", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(ManifestStore.Load(path, _key).Chunker);
        var mb = ManifestStore.Build(1, "n", 100, 4, 3, "key-1", "",
            new List<ManifestEntry> { new(new string('a', 64), 100) }, chunker: "buzhash");
        var pathb = ManifestStore.Save(Path.Combine(_dir, "sub"), mb, _key);
        Assert.Equal("buzhash", ManifestStore.Load(pathb, _key).Chunker);
    }
}
