namespace Corpus;

// §9 Wave-3g: bitmap-based slot liveness. B6L proved the last veto is slots
// 57-64 reading FILE-live through the shadow while raw live bytes are zeros
// (B6H) — byte classification is unfixable by byte-tweaks, so the $MFT
// $BITMAP stream (bit N = record N allocated) is the liveness source.
// Grid mirrors SkipUnchangedTests: chunkMib 1, bpc 4096, sys LCN 0-7
// (chunk 0), user LCN 512-767 (chunk 2), junk/diff LCN 900-915 (chunk 3).
public sealed class SkipBitmapLivenessTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public SkipBitmapLivenessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_bmap_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private string SeedVolume(string name)
    {
        var vol = Path.Combine(_dir, name);
        using var f = new FileStream(vol, FileMode.Create, FileAccess.Write);
        foreach (var seed in new[] { 611, 612, 613, 614 })
        {
            var c = RandomChunk(seed);
            f.Write(c, 0, c.Length);
        }
        return vol;
    }

    private static Dictionary<long, byte[]> BaseRecords()
    {
        var records = new Dictionary<long, byte[]>();
        for (var r = 0L; r < 16; r++)
            records[r] = MftRecordBuilder.Live(1024, 7,
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 0))));
        records[16] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))));
        return records;
    }

    // Bits 0..16 set (sys + the user record), everything past clear.
    private static byte[] AllocBitmap() => new byte[] { 0xFF, 0xFF, 0x01 };

    private static FakeMftVolume MftVol(IBlockSource inner, Dictionary<long, byte[]> records,
        byte[]? bitmap, long recordCount = 24)
    {
        var v = new FakeMftVolume(records: records, mftRuns: new (long, long)[] { (0, 100) },
            inner: inner, recordCount: recordCount);
        v.MftBitmap = bitmap;
        return v;
    }

    // B6L shadow-lie shape: FILE magic + in-use flag on an UNALLOCATED slot,
    // payload unstable across views (seeded per run). The payload lives in
    // record slack (past the end marker), so the slot stays structurally
    // parseable for the plan expansion — the lie is byte instability, not
    // structural corruption.
    private static byte[] ShadowLieRecord(int seed)
    {
        var r = MftRecordBuilder.LiveFlags(1024, 7, 1,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 900))));
        var rnd = new Random(seed);
        for (var i = 200; i < 300; i++)
            r[i] = (byte)rnd.Next(256);
        return r;
    }

    private static void MutateBytes(string path, long offset, int count)
    {
        var buf = new byte[count];
        new Random(9182).NextBytes(buf);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(offset, SeekOrigin.Begin);
        f.Write(buf, 0, buf.Length);
    }

    private int ManifestCount() =>
        Directory.Exists(Path.Combine(_storeRoot, "manifests"))
            ? Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length : 0;

    private static bool HasLine(TestLogger log, string fragment) =>
        log.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    // B6L re-proof: unallocated slots read FILE-live through the shadow on
    // BOTH views (unstable payloads), but the bitmap clears them on both —
    // so the record gate sees 0 changed and skips with zero bulk reads,
    // even though the plan gate vetoes (junk extents union chunk 3).
    [Fact]
    public void ShadowLieUnallocatedSlots_SkipsPreStream()
    {
        var vol = SeedVolume("bmaplie.img");
        var log = new TestLogger();
        using var store = NewStore(log);
        var run1 = BaseRecords();
        for (var r = 17L; r < 24; r++)
            run1[r] = ShadowLieRecord(1000 + (int)r);
        using (var v1 = MftVol(new FileBlockSource(vol), run1, AllocBitmap()))
            store.ImageFast(v1, "gen1");

        var run2 = BaseRecords();
        for (var r = 17L; r < 24; r++)
            run2[r] = ShadowLieRecord(2000 + (int)r); // same lie, different bytes
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = MftVol(counting, run2, AllocBitmap()))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "mft-bitmap: ok 3 bytes for 24 records"));
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.True(HasLine(log, "empty:7"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // Unit pin under the integration: the record list itself carries the
    // bitmap verdict — bit-clear + FILE-live bytes → "empty"; bit-set +
    // live → hashed; bit-set + dead flags → "dead"; bitmap absent →
    // legacy byte classification (garbage non-FILE → "empty").
    [Fact]
    public void BitmapVerdict_LandsInRecordList()
    {
        var recs = BaseRecords();
        recs[17] = ShadowLieRecord(42);
        recs[18] = MftRecordBuilder.LiveFlags(1024, 7, 0,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 900))));
        var bitmap = new byte[] { 0xFF, 0xFF, 0x05 }; // bits 0..16 + 18; 17 clear
        using (var vol = MftVol(new FileBlockSource(SeedVolume("bmapunit.img")), recs, bitmap))
        {
            var records = MftDigest.ComputeGroupsAndRecords(vol, Mib).Records;
            Assert.Equal(MftDigest.EmptyRecordSentinel, records[17]); // allocated=false beats FILE magic
            Assert.Equal(MftDigest.DeadRecordSentinel, records[18]); // allocated + flags=0 → dead
            Assert.Equal(64, records[16].Length); // allocated + live → hashed
        }
        using (var vol = MftVol(new FileBlockSource(SeedVolume("bmapunit2.img")), recs, null))
        {
            var records = MftDigest.ComputeGroupsAndRecords(vol, Mib).Records;
            Assert.Equal(64, records[17].Length); // absent bitmap: legacy, FILE-live hashes
            Assert.Equal(MftDigest.DeadRecordSentinel, records[18]);
        }
        var garbage = BaseRecords();
        garbage[17] = new byte[1024];
        new Random(7).NextBytes(garbage[17]); // non-FILE magic
        using (var vol = MftVol(new FileBlockSource(SeedVolume("bmapunit3.img")), garbage, null))
        {
            var records = MftDigest.ComputeGroupsAndRecords(vol, Mib).Records;
            Assert.Equal(MftDigest.EmptyRecordSentinel, records[17]); // legacy empty claim preserved
        }
    }

    // Fail-safe: bitmap unreadable/absent → today's behavior, never a new
    // empty claim. FILE-looking slots on both sides hash live, flip, veto
    // the record gate (outside-sys names them, empty:0) and the mutated
    // chunk writes gen 2.
    [Fact]
    public void BitmapAbsent_FileLookingSlots_VetoAndWriteGen()
    {
        var vol = SeedVolume("bmapabsent.img");
        var log = new TestLogger();
        using var store = NewStore(log);
        var run1 = BaseRecords();
        for (var r = 17L; r < 20; r++)
            run1[r] = ShadowLieRecord(3000 + (int)r);
        using (var v1 = MftVol(new FileBlockSource(vol), run1, null))
            store.ImageFast(v1, "gen1");

        var changed = BaseRecords();
        for (var r = 17L; r < 20; r++)
            changed[r] = ShadowLieRecord(4000 + (int)r);
        MutateBytes(vol, 3L * Mib + 1000, 100); // chunk 3: the junk slots' extents
        using (var v2 = MftVol(new FileBlockSource(vol), changed, null))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "mft-bitmap: null (absent: FakeMftVolume returned null)"));
        Assert.True(HasLine(log, "skip-records: 3 changed of 20 records, outside-sys: [17,18,19]"));
        Assert.True(HasLine(log, "empty:4")); // only the genuinely-zero tail; no new empty claims
        Assert.True(store.Verify(2));
    }

    // Gate ordering (Store.cs ImageFast: plan-gate OR record-gate): an
    // LSN-only touch flips every raw group digest so the plan gate vetoes
    // (first-outside names chunk 2), yet the record gate passes and the
    // skip fires pre-stream with zero bulk reads. No plan-gate change.
    [Fact]
    public void RecordPassShortCircuitsPlanVeto_SkipsWithZeroBulkReads()
    {
        var vol = SeedVolume("bmapor.img");
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), TouchCounters(BaseRecords()), AllocBitmap()))
            store.ImageFast(v1, "gen1");

        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = MftVol(counting, TouchCounters(BaseRecords(), lsnBase: 9000), AllocBitmap()))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-plan:") && HasLine(log, "first-outside: [2]")); // plan gate vetoed
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []")); // record gate passed
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // No over-skip: with the bitmap present, an allocated user-record
    // change still refuses the record gate and writes the gen.
    [Fact]
    public void BitmapAllocatedUserChange_StillWritesGen()
    {
        var vol = SeedVolume("bmapuser.img");
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), BaseRecords(), AllocBitmap()))
            store.ImageFast(v1, "gen1");

        var changed = BaseRecords();
        changed[16][1023] ^= 0xFF;
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the user file's clusters
        using (var v2 = MftVol(new FileBlockSource(vol), changed, AllocBitmap()))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        var outPath = Path.Combine(_dir, "bmap-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // B6E miss-1 shape: every record rewrite bumps LSN + fixup USN with
    // zero content change (the on-box mount/VSS touch signature).
    private static Dictionary<long, byte[]> TouchCounters(Dictionary<long, byte[]> records, long lsnBase = 1000)
    {
        var touched = new Dictionary<long, byte[]>();
        var lsn = lsnBase;
        foreach (var (rec, bytes) in records)
        {
            var stamped = (byte[])bytes.Clone();
            if (stamped.Length >= 16)
                BitConverter.GetBytes(lsn++).CopyTo(stamped, 0x08);
            if (stamped.Length >= 48)
            {
                BitConverter.GetBytes((ushort)42).CopyTo(stamped, 0x04);
                BitConverter.GetBytes((ushort)3).CopyTo(stamped, 0x06);
                for (var w = 0; w < 3; w++)
                    BitConverter.GetBytes((ushort)(7 + rec)).CopyTo(stamped, 42 + 2 * w);
            }
            touched[rec] = stamped;
        }
        return touched;
    }
}
