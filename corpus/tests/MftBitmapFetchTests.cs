namespace Corpus;

// §9 Wave-3g follow-up (B6M): the bitmap-liveness fix never fired on-box
// (`skip-records: ... dead:0 empty:0` = ClassifySkipRecord never returned
// empty = the bitmap fetch returned null on the real path). These tests
// pin the fetch point: ReadMftBitmap against realistic $BITMAP record
// shapes (resident + non-resident, including the oversize-dataSize
// truncation that killed the real path), and the single diagnostic log
// line for every outcome.
public sealed class MftBitmapFetchTests
{
    private const int Bpr = 1024;
    private const int Bpc = 4096;

    private static byte[] Record0(params byte[][] attrs) =>
        MftRecordBuilder.Live(Bpr, 7, attrs);

    private static byte[] ClusterWith(byte[] head)
    {
        var cluster = new byte[Bpc];
        new Random(1234).NextBytes(cluster);
        head.CopyTo(cluster, 0);
        return cluster;
    }

    private static bool HasLine(TestLogger log, string fragment) =>
        log.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void ResidentBitmap_ReturnsExactBytesWithoutClusterReads()
    {
        var bits = new byte[] { 0xFF, 0x01 };
        var record = Record0(MftRecordBuilder.BitmapResidentAttr(bits));
        var got = MftExtentReader.ReadMftBitmap(record,
            _ => throw new InvalidOperationException("resident bitmap must not read clusters"),
            Bpc, maxBytes: 32);
        Assert.Equal(bits, got);
    }

    [Fact]
    public void NonResidentBitmap_ExactSize_ReturnsBytes()
    {
        var bits = new byte[] { 0xFF, 0xFF, 0x01 };
        var record = Record0(MftRecordBuilder.BitmapNonResidentAttr(
            MftRecordBuilder.EncodeRuns((1, 100)), dataSize: 32));
        var got = MftExtentReader.ReadMftBitmap(record,
            lcn => lcn == 100 ? ClusterWith(bits) : throw new InvalidOperationException($"unexpected LCN {lcn}"),
            Bpc, maxBytes: 32);
        Assert.Equal(32, got!.Length);
        Assert.Equal(bits, got[..3]);
    }

    // B6M repro: cluster-sized $BITMAP stream on a small MFT (dataSize 4096
    // >> (RecordCount+7)/8 = 32). Old code threw InvalidDataException here
    // (→ normalized to null → silent legacy fallback, the fix never
    // fired); now the read caps at maxBytes and classifies covered slots.
    [Fact]
    public void NonResidentBitmap_OversizeDataSize_TruncatesInsteadOfThrowing()
    {
        var bits = new byte[] { 0xFF, 0xFF, 0x01 };
        var record = Record0(MftRecordBuilder.BitmapNonResidentAttr(
            MftRecordBuilder.EncodeRuns((1, 100)), dataSize: 4096));
        var got = MftExtentReader.ReadMftBitmap(record,
            lcn => lcn == 100 ? ClusterWith(bits) : throw new InvalidOperationException($"unexpected LCN {lcn}"),
            Bpc, maxBytes: 32);
        Assert.Equal(32, got!.Length);
        Assert.Equal(bits, got[..3]);
    }

    [Fact]
    public void NonResidentBitmap_NegativeDataSize_Throws()
    {
        var record = Record0(MftRecordBuilder.BitmapNonResidentAttr(
            MftRecordBuilder.EncodeRuns((1, 100)), dataSize: -1));
        Assert.Throws<InvalidDataException>(() =>
            MftExtentReader.ReadMftBitmap(record, _ => new byte[Bpc], Bpc, maxBytes: 32));
    }

    [Fact]
    public void MissingBitmapAttr_ReturnsNull()
    {
        var record = Record0(MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, 200))));
        Assert.Null(MftExtentReader.ReadMftBitmap(record, _ => new byte[Bpc], Bpc, maxBytes: 32));
    }

    [Fact]
    public void NonZeroBaseVcn_ReturnsNull()
    {
        var attr = MftRecordBuilder.BitmapNonResidentAttr(
            MftRecordBuilder.EncodeRuns((1, 100)), dataSize: 32);
        BitConverter.GetBytes(1L).CopyTo(attr, 16); // lowest VCN != 0: unsupported shape
        Assert.Null(MftExtentReader.ReadMftBitmap(Record0(attr), _ => new byte[Bpc], Bpc, maxBytes: 32));
    }

    [Fact]
    public void OverridesBitmap_ProductionChainDelegates()
    {
        using var fake = new FakeMftVolume(recordCount: 20);
        Assert.True(MftDigest.OverridesBitmap(fake));
        using var vss = new VssBlockSource("SNAPSHOT", 512 * Bpc, Bpc, _ => fake);
        Assert.True(MftDigest.OverridesBitmap(vss)); // explicit impl counts as an override
    }

    [Fact]
    public void NoOverrideDecorator_LogsNoOverride()
    {
        using var inner = new FakeMftVolume(recordCount: 20);
        using var vol = new NoBitmapDecorator(inner);
        Assert.False(MftDigest.OverridesBitmap(vol));
        var log = new TestLogger();
        MftDigest.ComputeGroupsAndRecords(vol, Bpc, log);
        Assert.True(HasLine(log, "mft-bitmap: null (no-override: NoBitmapDecorator uses the interface default)"));
    }

    [Fact]
    public void OverriddenNull_LogsAbsent()
    {
        using var vol = new FakeMftVolume(recordCount: 20); // MftBitmap null by default
        var log = new TestLogger();
        MftDigest.ComputeGroupsAndRecords(vol, Bpc, log);
        Assert.True(HasLine(log, "mft-bitmap: null (absent: FakeMftVolume returned null)"));
    }

    [Fact]
    public void ThrowingBitmap_LogsReadThrow()
    {
        using var inner = new FakeMftVolume(recordCount: 20);
        using var vol = new ThrowingBitmapVolume(inner);
        var log = new TestLogger();
        MftDigest.ComputeGroupsAndRecords(vol, Bpc, log);
        Assert.True(HasLine(log, "mft-bitmap: null (read-throw:InvalidDataException)"));
    }

    [Fact]
    public void ShortBitmap_LogsShort()
    {
        using var vol = new FakeMftVolume(recordCount: 20);
        vol.MftBitmap = new byte[] { 0xFF }; // 8 bits for 20 records
        var log = new TestLogger();
        MftDigest.ComputeGroupsAndRecords(vol, Bpc, log);
        Assert.True(HasLine(log, "mft-bitmap: short 1 bytes for 20 records (tail slots unknown)"));
    }

    [Fact]
    public void OkBitmap_LogsOk()
    {
        using var vol = new FakeMftVolume(recordCount: 24);
        vol.MftBitmap = new byte[] { 0xFF, 0xFF, 0x01 };
        var log = new TestLogger();
        MftDigest.ComputeGroupsAndRecords(vol, Bpc, log);
        Assert.True(HasLine(log, "mft-bitmap: ok 3 bytes for 24 records"));
    }

    // A decorator that forwards everything but never overrides the bitmap:
    // the DIM default silently returns null (the in-repo shape this guards
    // against is MftVssVolumeTests.TrackingMftVolume before its fix).
    private sealed class NoBitmapDecorator : IMftVolume
    {
        private readonly IMftVolume _inner;

        public NoBitmapDecorator(IMftVolume inner) => _inner = inner;

        public int BytesPerRecord => _inner.BytesPerRecord;
        public int BytesPerCluster => _inner.BytesPerCluster;
        public long RecordCount => _inner.RecordCount;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => _inner.MftRuns;
        public IReadOnlyList<MftEnumEntry> Enumerate() => _inner.Enumerate();
        public byte[] GetRecordBytes(ulong fileRef) => _inner.GetRecordBytes(fileRef);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class ThrowingBitmapVolume : IMftVolume
    {
        private readonly IMftVolume _inner;

        public ThrowingBitmapVolume(IMftVolume inner) => _inner = inner;

        public int BytesPerRecord => _inner.BytesPerRecord;
        public int BytesPerCluster => _inner.BytesPerCluster;
        public long RecordCount => _inner.RecordCount;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => _inner.MftRuns;
        public IReadOnlyList<MftEnumEntry> Enumerate() => _inner.Enumerate();
        public byte[] GetRecordBytes(ulong fileRef) => _inner.GetRecordBytes(fileRef);

        public byte[]? GetMftAllocationBitmap() =>
            throw new InvalidDataException("snapshot bitmap unreadable");

        public void Dispose() => _inner.Dispose();
    }
}
