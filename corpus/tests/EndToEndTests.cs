namespace Corpus;

public sealed class EndToEndTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public EndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_e2e_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        SeedVolume();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // 5 MiB, chunkMib 1: zeros | random | zeros | same random | text.
    // Chunks 0+2 dedup, 1+3 dedup → 3 unique of 5.
    private void SeedVolume()
    {
        var random = new byte[Mib];
        new Random(42).NextBytes(random);
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        using var f = new FileStream(_volPath, FileMode.Create, FileAccess.Write);
        f.Write(new byte[Mib], 0, Mib);
        f.Write(random, 0, Mib);
        f.Write(new byte[Mib], 0, Mib);
        f.Write(random, 0, Mib);
        f.Write(text, 0, Mib);
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private long IndexCount()
    {
        using var idx = new ChunkIndex(_dbPath);
        return idx.Count();
    }

    [Fact]
    public void Image_Export_RoundTripsBytesAndVerifies()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        var m = store.Image(source, "t1");
        Assert.Equal(1, m.Generation);
        Assert.Equal(5, m.Chunks.Count);
        Assert.Equal(5L * Mib, m.VolumeLength);

        var outPath = Path.Combine(_dir, "export.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void Image_SameVolumeTwice_SecondAddsNoChunks()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        store.Image(source, "first");
        Assert.Equal(3, IndexCount());
        var second = store.Image(source, "second");
        Assert.Equal(2, second.Generation);
        Assert.Equal("first", store.ListSnapshots()[0].Name);
        Assert.Equal(3, IndexCount());
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Verify_CorruptedPack_ReturnsFalse()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        store.Image(source, "t1");
        Assert.True(store.Verify(1));

        // Byte 44 sits in the first blob's payload (header is 41 bytes),
        // so the corruption lands in a Verify-read region, not the trailer.
        var pack = Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack")[0];
        using (var f = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(44, SeekOrigin.Begin);
            var b = (byte)f.ReadByte();
            f.Seek(44, SeekOrigin.Begin);
            f.WriteByte((byte)(b ^ 0xFF));
        }
        Assert.False(store.Verify(1));
    }
}
