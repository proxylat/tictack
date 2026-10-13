using DiscUtils.Vhdx;

namespace Corpus;

public sealed class VhdxTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public VhdxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_vhdx_" + Guid.NewGuid());
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

    [Fact]
    public void ExportVhdx_RoundTripsBytesThroughRealContainer()
    {
        using var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger());
        using var source = new FileBlockSource(_volPath);
        var m = store.Image(source, "t1");

        var vhdxPath = Path.Combine(_dir, "t1.vhdx");
        store.ExportVhdx(1, vhdxPath);

        // Container proof, not just DiscUtils-readable: VHDX file signature.
        var magic = new byte[8];
        using (var f = new FileStream(vhdxPath, FileMode.Open, FileAccess.Read))
            Assert.Equal(8, f.Read(magic, 0, 8));
        Assert.Equal("vhdxfile"u8.ToArray(), magic);

        using var disk = new Disk(vhdxPath, FileAccess.Read);
        Assert.Equal(5L * Mib, disk.Capacity);
        Assert.Equal(512, disk.SectorSize);
        var back = new byte[m.VolumeLength];
        disk.Content.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "VHDX content ended early at " + read);
            read += n;
        }
        Assert.Equal(File.ReadAllBytes(_volPath), back);
    }

    [Fact]
    public void ExportVhdx_AllZeroVolume_StaysSparse()
    {
        var zeroVol = Path.Combine(_dir, "zero.img");
        using (var f = new FileStream(zeroVol, FileMode.Create, FileAccess.Write))
            f.SetLength(8L * Mib);
        using var store = new Store(_storeRoot, _dbPath, 4, 3, new TestLogger());
        using var source = new FileBlockSource(zeroVol);
        store.Image(source, "zero");

        var vhdxPath = Path.Combine(_dir, "zero.vhdx");
        store.ExportVhdx(1, vhdxPath);

        // 8 MiB of zeros must cost metadata only, not 8 MiB of blocks.
        Assert.True(new FileInfo(vhdxPath).Length < 8L * Mib,
            "VHDX length: " + new FileInfo(vhdxPath).Length);
    }

    [Fact]
    public void AlignedCapacity_RoundsUpToOneMibAndRejectsEmpty()
    {
        Assert.Equal(Mib, VhdxExporter.AlignedCapacity(1));
        Assert.Equal(5L * Mib, VhdxExporter.AlignedCapacity(5L * Mib));
        Assert.Equal(6L * Mib, VhdxExporter.AlignedCapacity(5L * Mib + 1));
        Assert.Throws<InvalidDataException>(() => VhdxExporter.AlignedCapacity(0));
    }
}
