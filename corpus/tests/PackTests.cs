namespace Corpus;

public sealed class PackTests : IDisposable
{
    private readonly string _dir;

    public PackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_pack_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Digest(byte fill)
    {
        var d = new byte[32];
        for (var i = 0; i < 32; i++) d[i] = fill;
        return d;
    }

    [Fact]
    public void Add_ThreeBlobs_ReadPayloadRoundTrips()
    {
        var packsDir = Path.Combine(_dir, "packs");
        using (var writer = new PackWriter(packsDir))
        {
            var refs = new List<(string Pack, long Offset, byte[] Payload)>();
            for (byte i = 0; i < 3; i++)
            {
                var payload = new byte[] { i, (byte)(i + 1), (byte)(i + 2), 1, 2, 3, 4, 5, 6, 7 };
                var (pack, offset) = writer.Add(Digest(i), payload, payload.Length + 10, i);
                refs.Add((pack, offset, payload));
            }
            writer.Complete();
            foreach (var (pack, offset, payload) in refs)
                Assert.Equal(payload, PackReader.ReadPayload(Path.Combine(packsDir, pack), offset, payload.Length, out _));
        }
    }

    [Fact]
    public void Add_SmallMax_RotatesToThreePacks()
    {
        var packsDir = Path.Combine(_dir, "packs");
        using (var writer = new PackWriter(packsDir, maxPackBytes: 100))
        {
            for (byte i = 0; i < 3; i++)
                writer.Add(Digest(i), new byte[10], 10, 0);
            writer.Complete();
        }
        Assert.Equal(3, Directory.GetFiles(packsDir, "*.pack").Length);
        Assert.Empty(Directory.GetFiles(packsDir, "*.tmp"));
    }

    [Fact]
    public void Complete_SealedPack_EndsWithMagic()
    {
        var packsDir = Path.Combine(_dir, "packs");
        string pack;
        using (var writer = new PackWriter(packsDir))
        {
            (pack, _) = writer.Add(Digest(1), new byte[] { 9, 8, 7 }, 3, 0);
            writer.Complete();
        }
        var bytes = File.ReadAllBytes(Path.Combine(packsDir, pack));
        Assert.Equal(new byte[] { (byte)'C', (byte)'P', (byte)'K', (byte)'1' }, bytes[^4..]);
    }

    [Fact]
    public void Complete_ZeroBlobs_CreatesNoFile()
    {
        var packsDir = Path.Combine(_dir, "packs");
        using (var writer = new PackWriter(packsDir))
        {
            writer.Complete();
        }
        Assert.Empty(Directory.GetFiles(packsDir));
    }

    [Fact]
    public void Dispose_SealsPack_PayloadReadableAfterUsing()
    {
        var packsDir = Path.Combine(_dir, "packs");
        string pack;
        long offset;
        var payload = new byte[] { 5, 4, 3, 2, 1 };
        using (var writer = new PackWriter(packsDir))
        {
            (pack, offset) = writer.Add(Digest(7), payload, payload.Length, 1);
        }
        Assert.Equal(payload, PackReader.ReadPayload(Path.Combine(packsDir, pack), offset, payload.Length, out _));
    }
}
