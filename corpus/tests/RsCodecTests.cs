namespace Corpus;

public sealed class RsCodecTests : IDisposable
{
    private readonly string _dir;

    public RsCodecTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_rs_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Digest(byte fill)
    {
        var d = new byte[32];
        for (var i = 0; i < 32; i++) d[i] = fill;
        return d;
    }

    [Fact]
    public void ParityBytesFor_MapsPercentToParity()
    {
        Assert.Equal(0, RsCodec.ParityBytesFor(0));
        Assert.Equal(0, RsCodec.ParityBytesFor(-3));
        Assert.Equal(2, RsCodec.ParityBytesFor(1)); // min 2: 1 parity detects, cannot correct
        Assert.Equal(8, RsCodec.ParityBytesFor(6)); // 128+8 layout, ~6.25% overhead
        Assert.Equal(64, RsCodec.ParityBytesFor(50));
    }

    [Fact]
    public void RoundTrip_CleanData_NeedsNoRepair()
    {
        var data = RandomBytes(3000, 7);
        var parity = RsCodec.EncodeFile(data, 8);
        Assert.Equal(24 * 8, parity.Length); // ceil(3000/128) stripes
        var snapshot = (byte[])data.Clone();
        Assert.Equal(0, RsCodec.TryRepair(data, parity, 8));
        Assert.Equal(snapshot, data);
    }

    [Fact]
    public void SingleByteCorruption_Repaired()
    {
        var data = RandomBytes(3000, 11);
        var parity = RsCodec.EncodeFile(data, 8);
        var snapshot = (byte[])data.Clone();
        data[500] ^= 0xFF;
        Assert.Equal(1, RsCodec.TryRepair(data, parity, 8));
        Assert.Equal(snapshot, data);
    }

    [Fact]
    public void MultiByteCorruption_AcrossStripes_Repaired()
    {
        var data = RandomBytes(3000, 13);
        var parity = RsCodec.EncodeFile(data, 8);
        var snapshot = (byte[])data.Clone();
        data[0] ^= 0x01;
        data[200] ^= 0x02;
        data[1500] ^= 0x04;
        data[2900] ^= 0x08;
        Assert.True(RsCodec.TryRepair(data, parity, 8) > 0);
        Assert.Equal(snapshot, data);
    }

    [Fact]
    public void TooManyErrorsInOneStripe_FailsLoud()
    {
        var data = RandomBytes(3000, 17);
        var parity = RsCodec.EncodeFile(data, 8);
        for (var i = 0; i < 9; i++)
            data[i] ^= (byte)(i + 1); // 9 errors in stripe 0, P=8 corrects 4
        Assert.Equal(-1, RsCodec.TryRepair(data, parity, 8));
    }

    [Fact]
    public void OverheadZero_WritesNoSidecar()
    {
        var packsDir = Path.Combine(_dir, "packs");
        using (var writer = new PackWriter(packsDir))
        {
            writer.Add(Digest(1), new byte[] { 9, 8, 7 }, 3, 0);
            writer.Complete();
        }
        Assert.Empty(Directory.GetFiles(packsDir, "*.ecc"));
        Assert.Empty(Directory.GetFiles(packsDir, "*.tmp"));
    }

    [Fact]
    public void OldPackWithoutSidecar_StillReads()
    {
        var packsDir = Path.Combine(_dir, "packs");
        string pack;
        long offset;
        var payload = new byte[] { 5, 4, 3, 2, 1 };
        using (var writer = new PackWriter(packsDir))
        {
            (pack, offset) = writer.Add(Digest(7), payload, payload.Length, 1);
        }
        Assert.False(PackEcc.TryRepairPack(Path.Combine(packsDir, pack)));
        Assert.Equal(payload, PackReader.ReadPayload(Path.Combine(packsDir, pack), offset, payload.Length, out _));
    }

    [Fact]
    public void HeaderCorruption_ReaderRepairsViaSidecar()
    {
        var packsDir = Path.Combine(_dir, "packs");
        string pack;
        long offset;
        var payload = RandomBytes(200, 19);
        using (var writer = new PackWriter(packsDir, eccOverheadPercent: 6))
        {
            (pack, offset) = writer.Add(Digest(3), payload, payload.Length, 0);
        }
        var packPath = Path.Combine(packsDir, pack);
        Assert.Single(Directory.GetFiles(packsDir, "*.ecc"));
        using (var f = new FileStream(packPath, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(0, SeekOrigin.Begin);
            var b = (byte)f.ReadByte();
            f.Seek(0, SeekOrigin.Begin);
            f.WriteByte((byte)(b ^ 0xFF)); // stored-length header: read throws without ECC
        }
        Assert.Equal(payload, PackReader.ReadPayload(packPath, offset, payload.Length, out _));
    }

    [Fact]
    public void StoreVerify_CorruptThenRepaired_VerifiesGreen()
    {
        var storeRoot = Path.Combine(_dir, "store");
        var dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        var volPath = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(volPath, RandomBytes(64 * 1024, 23));
        using (var store = new Store(storeRoot, dbPath, 1, 3, new TestLogger(), eccOverheadPercent: 6))
        {
            using (var source = new FileBlockSource(volPath))
                store.Image(source, "t1");
            Assert.True(store.Verify(1));
            var packsDir = Path.Combine(storeRoot, "packs");
            Assert.Single(Directory.GetFiles(packsDir, "*.ecc"));
            var pack = Directory.GetFiles(packsDir, "*.pack")[0];
            using (var f = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite))
            {
                // Payload region (header is 41 bytes): decode or digest
                // fails, then the verify hook repairs and re-reads.
                f.Seek(41 + 5, SeekOrigin.Begin);
                var b = (byte)f.ReadByte();
                f.Seek(41 + 5, SeekOrigin.Begin);
                f.WriteByte((byte)(b ^ 0xFF));
            }
            Assert.True(store.Verify(1));
        }
    }

    [Fact]
    public void StoreVerify_UnrecoverableCorruption_StaysRed()
    {
        var storeRoot = Path.Combine(_dir, "store");
        var dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        var volPath = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(volPath, RandomBytes(64 * 1024, 29));
        using (var store = new Store(storeRoot, dbPath, 1, 3, new TestLogger(), eccOverheadPercent: 6))
        {
            using (var source = new FileBlockSource(volPath))
                store.Image(source, "t1");
            Assert.True(store.Verify(1));
            var pack = Directory.GetFiles(Path.Combine(storeRoot, "packs"), "*.pack")[0];
            using (var f = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite))
            {
                // 12 bytes inside stripe 0: P=8 corrects 4, so this is loud red.
                for (var i = 0; i < 12; i++)
                {
                    f.Seek(41 + i, SeekOrigin.Begin);
                    var b = (byte)f.ReadByte();
                    f.Seek(41 + i, SeekOrigin.Begin);
                    f.WriteByte((byte)(b ^ (0xFF - i)));
                }
            }
            Assert.False(store.Verify(1));
        }
    }
}
