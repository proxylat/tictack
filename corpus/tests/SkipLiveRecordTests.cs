namespace Corpus;

// §9 design B: the RECORD pass (bitmap liveness classify + SkipRecordDigest)
// reads MFT bytes through the LIVE volume handle; groups, dirty-expansion,
// and bulk streaming stay on the frozen shadow. Shadow-view bytes smear
// intermittently on allocated records (B6K FRNs 32/33/34/36, v10 FRN 38),
// so a record-through-shadow pass can never skip deterministically — the
// live view is stable (B6H/B6K), so live-vs-live converges. Grid mirrors
// SkipUnchangedTests: chunkMib 1, bpc 4096; sys runs LCN 0-7 (chunk 0),
// user file LCN 512-767 (chunk 2), junk/diff LCN 900-915 (chunk 3).
public sealed class SkipLiveRecordTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public SkipLiveRecordTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_live_" + Guid.NewGuid());
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

    private string SeedVolume(string name, params int[] seeds)
    {
        var vol = Path.Combine(_dir, name);
        using var f = new FileStream(vol, FileMode.Create, FileAccess.Write);
        foreach (var seed in seeds)
        {
            var c = RandomChunk(seed);
            f.Write(c, 0, c.Length);
        }
        return vol;
    }

    private static Dictionary<long, byte[]> SysUserRecords()
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

    // Dual-view volume: IMftVolume members serve the SHADOW view (bulk
    // truth, group hashing, plan expansion), LiveFactory supplies a fresh
    // LIVE view per record pass (record hashing + bitmap + advisory mask).
    private static FakeMftVolume DualVol(IBlockSource inner,
        Dictionary<long, byte[]> shadowRecs, Dictionary<long, byte[]> liveRecs,
        byte[]? bitmap, long recordCount = 20)
    {
        var v = new FakeMftVolume(records: shadowRecs,
            mftRuns: new (long, long)[] { (0, 100) }, inner: inner, recordCount: recordCount);
        v.MftBitmap = bitmap;
        v.LiveFactory = () =>
        {
            var live = new FakeMftVolume(records: liveRecs,
                mftRuns: new (long, long)[] { (0, 100) }, recordCount: recordCount);
            live.MftBitmap = bitmap;
            return live;
        };
        return v;
    }

    private static void MutateBytes(string path, long offset, int count)
    {
        var buf = new byte[count];
        new Random(9182).NextBytes(buf);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        f.Seek(offset, SeekOrigin.Begin);
        f.Write(buf, 0, buf.Length);
    }

    private static byte[] TouchLsn(byte[] record)
    {
        // LSN-only re-stamp: flips the raw group digest (plan vetoes) but is
        // masked in the record digest (record gate passes). USA untouched
        // (zero → no USA mask), tails untouched, parse identical.
        var stamped = (byte[])record.Clone();
        for (var i = 0x08; i < 0x10; i++)
            stamped[i] ^= 0xFF;
        return stamped;
    }

    private static bool HasLine(TestLogger log, string fragment) =>
        log.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    private int ManifestCount() =>
        Directory.Exists(Path.Combine(_storeRoot, "manifests"))
            ? Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length : 0;

    // Old-manifest simulation (pre-live-view): the record gate must refuse
    // on the version drift and the backstop decides alone, then self-heal.
    private void PinRecordVersion(int version)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var key = ChunkId.EnsureKey(_storeRoot);
        var path = Directory.GetFiles(manDir, "000001-*.json")[0];
        var m = ManifestStore.Load(path, key);
        m.MftRecordDigestVersion = version;
        ManifestStore.Save(manDir, m, key);
    }


    // Fixture-against-old-path proof: the shadow view of an allocated user
    // record flaps between opens (unmasked slack byte — the v10 FRN 38
    // shape), while the live view is stable. The single-view (old) record
    // lists diverge on the smear; the dual-view (design B) record list
    // follows the live view, and groups stay shadow.
    [Fact]
    public void ShadowSmear_FlapsShadowView_StableLiveView()
    {
        var shadowA = SysUserRecords();
        shadowA[16][1000] = 0xAA;
        var shadowB = SysUserRecords();
        shadowB[16][1000] = 0xBB;
        var liveRecs = SysUserRecords(); // slack 0x00 on both runs
        using var a = new FakeMftVolume(records: shadowA, recordCount: 20, length: 4L * Mib);
        using var b = new FakeMftVolume(records: shadowB, recordCount: 20, length: 4L * Mib);
        using var live = new FakeMftVolume(records: liveRecs, recordCount: 20, length: 4L * Mib);

        var ra = MftDigest.ComputeGroupsAndRecords(a, Mib).Records;
        var rb = MftDigest.ComputeGroupsAndRecords(b, Mib).Records;
        Assert.NotEqual(ra[16], rb[16]); // old path vetoes: the smear is comparison-visible

        var rl = MftDigest.ComputeGroupsAndRecords(live, Mib).Records;
        Assert.Equal(rl, MftDigest.ComputeGroupsAndRecords(live, Mib).Records); // live stable

        var dual = MftDigest.ComputeGroupsAndRecords(a, live, Mib);
        Assert.Equal(rl, dual.Records); // records follow the live view
        Assert.Equal(MftDigest.ComputeGroups(a, Mib), dual.Groups); // groups stay shadow
    }

    // The b6n shape through the full gate: shadow smears on the allocated
    // user record AND on unallocated FILE-looking lie slots (B6L shape:
    // bitmap clears them on both views), live stable throughout. The plan
    // gate vetoes (groups flap → chunk 2+3 dirty), the live record gate
    // passes, and the skip fires pre-stream with zero bulk reads.
    [Fact]
    public void ShadowSmearLiveStable_SkipsPreStream()
    {
        var vol = SeedVolume("livesmear.img", 701, 702, 703, 704);
        var log = new TestLogger();
        using var store = NewStore(log);
        var liveRecs = SysUserRecords();
        var shadow1 = SysUserRecords();
        shadow1[16][1000] = 0xAA;
        for (var r = 17L; r < 24; r++)
            shadow1[r] = ShadowLie(1000 + (int)r);
        using (var v1 = DualVol(new FileBlockSource(vol), shadow1, liveRecs, AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow2 = SysUserRecords();
        shadow2[16][1000] = 0xBB;
        for (var r = 17L; r < 24; r++)
            shadow2[r] = ShadowLie(2000 + (int)r); // same lie, different bytes
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = DualVol(counting, shadow2, liveRecs, AllocBitmap(), recordCount: 24))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "mft-bitmap: ok 3 bytes for 24 records"));
        Assert.True(HasLine(log, "skip-plan:")); // group flap vetoed the plan gate → the record gate decided
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.True(HasLine(log, "empty:7"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // No over-skip on the live path: the shadow shows only a masked LSN
    // re-stamp (plan still vetoes on the group flip), but the LIVE record
    // carries a real content change — the record gate must name FRN 16 and
    // the gen must record it. (Old shadow-path code logs 0 changed here.)
    [Fact]
    public void LiveViewFidelity_MaskedShadowRealLiveChange_WritesGen()
    {
        var vol = SeedVolume("livefidel.img", 711, 712, 713, 714);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), SysUserRecords(), null))
            store.ImageFast(v1, "gen1");

        var shadow = SysUserRecords();
        shadow[16] = TouchLsn(shadow[16]); // masked-only: invisible to the record gate
        var live = SysUserRecords();
        live[16][1000] ^= 0xFF; // real content change, parse-identical slack
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the user file's clusters
        using (var v2 = DualVol(new FileBlockSource(vol), shadow, live, null))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        var outPath = Path.Combine(_dir, "livefidel-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // Version self-heal pin: a v3 (shadow-view) manifest refuses the record
    // gate once (no skip-records line — the version drift refuses before
    // comparing) and the backstop writes a v4 gen; the next run then skips
    // pre-stream on live-vs-live digests.
    [Fact]
    public void VersionBumpV3_RefusesOnceThenSelfHeals()
    {
        var vol = SeedVolume("livever.img", 721, 722, 723, 724);
        using (var store = NewStore())
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), SysUserRecords(), null))
            store.ImageFast(v1, "gen1");
        PinRecordVersion(3);

        var log2 = new TestLogger();
        using (var store = NewStore(log2))
        {
            var shadow = SysUserRecords();
            shadow[16][1000] ^= 0xFF;
            var live = SysUserRecords();
            live[16][1000] ^= 0xFF;
            MutateBytes(vol, 2L * Mib + 1000, 100);
            using var v2 = DualVol(new FileBlockSource(vol), shadow, live, null);
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
            Assert.Equal(MftDigest.SkipRecordVersion, m.MftRecordDigestVersion);
        }
        Assert.Equal(2, ManifestCount());
        Assert.DoesNotContain(log2.Messages, m => m.Contains("skip-records:"));

        var log3 = new TestLogger();
        using (var store = NewStore(log3))
        {
            // Idle pair except a masked shadow re-stamp (group flap forces
            // the plan gate open so the healed record gate decides).
            var shadow = SysUserRecords();
            shadow[16][1000] ^= 0xFF;
            shadow[16] = TouchLsn(shadow[16]);
            var live = SysUserRecords();
            live[16][1000] ^= 0xFF;
            using var inner = new FileBlockSource(vol);
            using var counting = new CountingBlockSource(inner, Mib);
            using var v3 = DualVol(counting, shadow, live, null);
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v3, "gen3", skipIfUnchanged: true));
            Assert.Equal(2, ex.Generation);
            Assert.Empty(counting.ReadChunks);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log3, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log3.Messages, m => m.Contains("skip-compare:"));
        Assert.True(NewStore().Verify(2));
    }

    // Live-fetch unavailable (no live provider — the Linux / test-seam
    // shape: TryOpenLiveMft returns null): the record pass falls back to
    // the shadow view and the image still completes — fail-safe, never
    // throws. Idle pair skips pre-stream on the empty plan gate with zero
    // bulk reads and no record-gate line.
    [Fact]
    public void NoLiveProvider_FallsBackToShadow_SkipsPreStream()
    {
        var vol = SeedVolume("nolive.img", 731, 732, 733, 734);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = ShadowOnlyVol(new FileBlockSource(vol), SysUserRecords(), AllocBitmap()))
            store.ImageFast(v1, "gen1");

        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = ShadowOnlyVol(counting, SysUserRecords(), AllocBitmap()))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // Bitmap source pin (design B choice): liveness classifies the SAME
    // view whose bytes are hashed, so the bitmap is read from the LIVE
    // handle (recordsVol), never the shadow. Divergent-view proof: a slot
    // the shadow bitmap frees but the live bitmap holds hashes live; a
    // slot the live bitmap frees is empty despite FILE-live bytes.
    [Fact]
    public void BitmapComesFromLiveView_NotShadow()
    {
        using var shadow = new FakeMftVolume(records: SysUserRecords(), recordCount: 20);
        shadow.MftBitmap = new byte[] { 0xFF, 0xFF, 0x00 }; // shadow: slot 16 free
        using var live = new FakeMftVolume(records: SysUserRecords(), recordCount: 20);
        live.MftBitmap = AllocBitmap(); // live: slot 16 allocated
        var dual = MftDigest.ComputeGroupsAndRecords(shadow, live, Mib);
        Assert.Equal(64, dual.Records[16].Length); // live bitmap wins: hashed, not empty

        using var shadow2 = new FakeMftVolume(records: SysUserRecords(), recordCount: 20);
        shadow2.MftBitmap = AllocBitmap();
        using var live2 = new FakeMftVolume(records: SysUserRecords(), recordCount: 20);
        live2.MftBitmap = new byte[] { 0xFF, 0xFF, 0x00 }; // live: slot 16 free
        var dual2 = MftDigest.ComputeGroupsAndRecords(shadow2, live2, Mib);
        Assert.Equal(MftDigest.EmptyRecordSentinel, dual2.Records[16]); // live bitmap wins: empty despite FILE-live bytes
    }

    // B6P/B6Q race net: the plan-pass live sweep catches one slot
    // mid-rewrite (one unmasked content byte torn, parse-identical), the
    // settle re-read sees parent bytes. The gate must skip pre-stream with
    // zero bulk reads. LiveFactory call 1 = gen1 baseline (stable), call 2
    // = gen2 plan pass (torn), calls 3+ = gen2 mask + retry (stable).
    [Fact]
    public void TornLiveReadThenStable_SkipsViaRetry()
    {
        var vol = SeedVolume("tornretry.img", 701, 702, 703, 704);
        var log = new TestLogger();
        using var store = NewStore(log);
        var stable = SysUserRecords();
        var torn = SysUserRecords();
        torn[16][1000] ^= 0xFF; // unmasked content tear, parse-identical
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), stable, AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow2 = SysUserRecords();
        shadow2[16][1000] = 0xBB; // group flap: plan gate vetoes, record gate decides
        var calls = 0;
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = DualVol(counting, shadow2, stable, AllocBitmap(), recordCount: 24);
        // First live open (plan pass) serves the torn bytes, every later
        // open (mask + retry) serves stable bytes.
        v2.LiveFactory = () => NewLive(++calls == 1 ? torn : stable);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-plan:")); // plan gate vetoed on the group flap
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(HasLine(log, "skip-records-retry: re-read [16] -> still-changed []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));

        FakeMftVolume NewLive(Dictionary<long, byte[]> recs)
        {
            var l = new FakeMftVolume(records: recs,
                mftRuns: new (long, long)[] { (0, 100) }, recordCount: 24);
            l.MftBitmap = AllocBitmap();
            return l;
        }
    }

    // Persistent dirt (real content change, identical across sweep and
    // retry) must still veto and write the gen: the retry converges to
    // skip ONLY on byte-stable re-reads.
    [Fact]
    public void PersistentLiveChange_StillWritesGen()
    {
        var vol = SeedVolume("persistretry.img", 711, 712, 713, 714);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), SysUserRecords(), AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow = SysUserRecords();
        shadow[16] = TouchLsn(shadow[16]); // masked-only: plan vetoes, record gate must still see the live change
        var live = SysUserRecords();
        live[16][1000] ^= 0xFF; // real content change, stable across sweep + retry
        MutateBytes(vol, 2L * Mib + 1000, 100); // bulk carries the change: the written gen has new chunks
        using (var v2 = DualVol(new FileBlockSource(vol), shadow, live, AllocBitmap(), recordCount: 24))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(HasLine(log, "skip-records-retry: re-read [16] -> still-changed [16]"));
        var outPath = Path.Combine(_dir, "persistretry-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // HARD-CONSTRAINT pin, FRN-38 shape (in-use DIRECTORY with a resident
    // index): a persistent INDEX-byte change must still veto after the
    // retry — the race net masks nothing new, so index dirt can never skip.
    // FRN 16 stands in for 38 here (both non-sys user slots, same gate rule).
    [Fact]
    public void DirIndexChange_PersistsThroughRetry_WritesGen()
    {
        var vol = SeedVolume("dirindexretry.img", 721, 722, 723, 724);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), DirLive(DirIndexRecord(0x70)), AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow = SysUserRecords();
        shadow[16][1000] = 0xCC; // group flap: plan gate vetoes, record gate decides
        MutateBytes(vol, 2L * Mib + 2000, 100); // bulk carries the change: the written gen has new chunks
        using (var v2 = DualVol(new FileBlockSource(vol), shadow, DirLive(DirIndexRecord(0x71)), AllocBitmap(), recordCount: 24))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(HasLine(log, "skip-records-retry: re-read [16] -> still-changed [16]"));
        Assert.True(store.Verify(2));
    }

    // FRN-38 shape: in-use DIRECTORY (flags 3) carrying a resident 0x90
    // index attribute; flip selects one index-content byte. The 0x80 data
    // runs keep the record parseable for plan expansion; the 0x90 bytes are
    // hashed but never parsed — exactly the sensitivity the retry preserves.
    private static Dictionary<long, byte[]> DirLive(byte[] dir16)
    {
        var recs = SysUserRecords();
        recs[16] = dir16;
        return recs;
    }

    private static byte[] DirIndexRecord(byte flip)
    {
        var idx = new byte[48];
        BitConverter.GetBytes(0x90u).CopyTo(idx, 0);
        BitConverter.GetBytes(48u).CopyTo(idx, 4);
        idx[8] = 0; // resident
        BitConverter.GetBytes(24u).CopyTo(idx, 16); // contentLen
        BitConverter.GetBytes((ushort)24).CopyTo(idx, 20); // contentOff
        for (var i = 24; i < 48; i++)
            idx[i] = (byte)(i + 1);
        idx[30] = flip; // one index-content byte: the persistent dirt
        return MftRecordBuilder.LiveFlags(1024, 7, 3,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))), idx);
    }

    // B6R fixture: serves torn bytes on the first GetRecordBytes per slot,
    // stable bytes on every later read — a live sweep catching a slot
    // mid-rewrite at capture time. Delegates everything else to the
    // wrapped records view; fresh wrapper per LiveFactory call.
    private sealed class TornFirstLiveVolume : IMftVolume
    {
        private readonly FakeMftVolume _stable;
        private readonly Dictionary<long, byte[]> _torn;
        private readonly HashSet<long> _served = new();

        public TornFirstLiveVolume(FakeMftVolume stable, Dictionary<long, byte[]> torn)
        {
            _stable = stable;
            _torn = torn;
        }

        public int BytesPerRecord => _stable.BytesPerRecord;
        public int BytesPerCluster => _stable.BytesPerCluster;
        public long RecordCount => _stable.RecordCount;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => _stable.MftRuns;
        public IReadOnlyList<MftEnumEntry> Enumerate() => _stable.Enumerate();
        public byte[]? GetMftAllocationBitmap() => _stable.GetMftAllocationBitmap();

        public byte[] GetRecordBytes(ulong fileRef)
        {
            var rec = (long)(fileRef & 0xFFFF_FFFF_FFFF);
            if (_torn.TryGetValue(rec, out var torn) && _served.Add(rec))
                return torn;
            return _stable.GetRecordBytes(fileRef);
        }

        public void Dispose()
        {
        }
    }

    private static TornFirstLiveVolume TornFirstLive(Dictionary<long, byte[]> stable,
        Dictionary<long, byte[]> torn, long recordCount = 24)
    {
        var s = new FakeMftVolume(records: stable,
            mftRuns: new (long, long)[] { (0, 100) }, recordCount: recordCount);
        s.MftBitmap = AllocBitmap();
        return new TornFirstLiveVolume(s, torn);
    }

    // Digest-level pin for the stabilization: a live view that smears the
    // first read per slot must still hash to the stable digests — a torn
    // parent capture must not poison the stored list.
    [Fact]
    public void TornFirstRead_ConvergesToStableDigests()
    {
        var stable = SysUserRecords();
        var torn = SysUserRecords();
        torn[16][1000] ^= 0xFF; // unmasked content tear, parse-identical
        using var groups = new FakeMftVolume(records: SysUserRecords(), recordCount: 20, length: 4L * Mib);
        using var tornFirst = TornFirstLive(stable, torn, 20);
        // Dual-view: the tear must hit the RECORDS pass first read (the
        // groups pass shares the handle and would consume it).
        var stabilized = MftDigest.ComputeGroupsAndRecords(groups, tornFirst, Mib).Records;
        using var clean = new FakeMftVolume(records: SysUserRecords(), recordCount: 20, length: 4L * Mib);
        clean.MftBitmap = AllocBitmap();
        Assert.Equal(MftDigest.ComputeGroupsAndRecords(clean, Mib).Records, stabilized);
    }

    // B6R end-to-end: gen1's baseline live view smears (torn first read per
    // slot — the parent-side tear the retry can never cure), gen2 is fully
    // stable with only a masked shadow re-stamp forcing the record gate to
    // decide. The skip must fire pre-stream with zero bulk reads and no
    // retry line — the stabilized baseline never recorded the tear.
    [Fact]
    public void TornParentCaptureThenStable_SkipsPreStream()
    {
        var vol = SeedVolume("tornparent.img", 701, 702, 703, 704);
        var log = new TestLogger();
        using var store = NewStore(log);
        var stable = SysUserRecords();
        var torn = SysUserRecords();
        torn[16][1000] ^= 0xFF; // unmasked content tear, parse-identical
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), stable, AllocBitmap(), recordCount: 24))
        {
            v1.LiveFactory = () => TornFirstLive(stable, torn);
            store.ImageFast(v1, "gen1");
        }

        var shadow2 = SysUserRecords();
        shadow2[16] = TouchLsn(shadow2[16]); // masked-only flap: plan gate vetoes, record gate decides
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = DualVol(counting, shadow2, SysUserRecords(), AllocBitmap(), recordCount: 24))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-records-retry:"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // Wrong-skip guard for the stabilization: the tear settles on bytes
    // that still differ from the parent (persistent change, not a torn
    // read) — the gate must veto and write the gen. The tear differs from
    // BOTH parent and final so the third read does the deciding.
    [Fact]
    public void TornThenPersistentChange_StillWritesGen()
    {
        var vol = SeedVolume("tornpersist.img", 711, 712, 713, 714);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), SysUserRecords(), AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow = SysUserRecords();
        shadow[16] = TouchLsn(shadow[16]); // masked-only: plan vetoes, record gate must still see the live change
        var changed = SysUserRecords();
        changed[16][1000] ^= 0xFF; // persistent content change, stable across re-reads
        var torn = SysUserRecords();
        torn[16][1000] ^= 0x11; // tear: differs from parent AND from final
        MutateBytes(vol, 2L * Mib + 1000, 100); // bulk carries the change: the written gen has new chunks
        using var v2 = DualVol(new FileBlockSource(vol), shadow, changed, AllocBitmap(), recordCount: 24);
        v2.LiveFactory = () => TornFirstLive(changed, torn);
        var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
        Assert.Equal(2, m.Generation);
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(HasLine(log, "skip-records-retry: re-read [16] -> still-changed [16]"));
        var outPath = Path.Combine(_dir, "tornpersist-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // Shadow-only volume: LiveFactory unset → TryOpenLiveMft returns null
    // → the record pass uses the shadow view, exactly like a non-Windows
    // or provider-less source. Never throws for the missing live handle.
    private static FakeMftVolume ShadowOnlyVol(IBlockSource inner,
        Dictionary<long, byte[]> shadowRecs, byte[]? bitmap, long recordCount = 20)
    {
        var v = new FakeMftVolume(records: shadowRecs,
            mftRuns: new (long, long)[] { (0, 100) }, inner: inner, recordCount: recordCount);
        v.MftBitmap = bitmap;
        return v;
    }

    // B6L shadow-lie shape: FILE magic + in-use flag on an UNALLOCATED
    // slot, payload unstable across views (seeded per run). The payload
    // lives in record slack (past the end marker), so the slot stays
    // structurally parseable for plan expansion — the lie is byte
    // instability, not structural corruption.
    private static byte[] ShadowLie(int seed)
    {
        var r = MftRecordBuilder.LiveFlags(1024, 7, 1,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 900))));
        var rnd = new Random(seed);
        for (var i = 200; i < 300; i++)
            r[i] = (byte)rnd.Next(256);
        return r;
    }

    private sealed class SharedReads
    {
        public int Count;
    }

    // B6S-shape live view: one global post-create transition window shared
    // across opens — the first `transitionReads` reads of the veto slot
    // serve transitional bytes, later reads serve parent bytes. Everything
    // else delegates to the stable view.
    private sealed class SettlingLiveVolume : IMftVolume
    {
        private readonly FakeMftVolume _stable;
        private readonly byte[] _transitional;
        private readonly long _slot;
        private readonly SharedReads _shared;
        private readonly int _transitionReads;

        public SettlingLiveVolume(FakeMftVolume stable, byte[] transitional, long slot, SharedReads shared, int transitionReads)
        {
            _stable = stable;
            _transitional = transitional;
            _slot = slot;
            _shared = shared;
            _transitionReads = transitionReads;
        }

        public int BytesPerRecord => _stable.BytesPerRecord;
        public int BytesPerCluster => _stable.BytesPerCluster;
        public long RecordCount => _stable.RecordCount;
        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => _stable.MftRuns;
        public IReadOnlyList<MftEnumEntry> Enumerate() => _stable.Enumerate();
        public byte[]? GetMftAllocationBitmap() => _stable.GetMftAllocationBitmap();

        public byte[] GetRecordBytes(ulong fileRef)
        {
            var rec = (long)(fileRef & 0xFFFF_FFFF_FFFF);
            if (rec == _slot && _shared.Count < _transitionReads)
            {
                _shared.Count++;
                return _transitional;
            }
            return _stable.GetRecordBytes(fileRef);
        }

        public void Dispose()
        {
        }
    }

    private static SettlingLiveVolume SettlingLive(Dictionary<long, byte[]> stable, Dictionary<long, byte[]> trans, long slot, SharedReads shared, int transitionReads)
    {
        var s = new FakeMftVolume(records: stable,
            mftRuns: new (long, long)[] { (0, 100) }, recordCount: 24);
        s.MftBitmap = AllocBitmap();
        return new SettlingLiveVolume(s, trans[slot], slot, shared, transitionReads);
    }

    // Settle-across-the-boundary pin (B6S shape): the live view of the veto
    // slot serves TRANSITIONAL bytes (unmasked, parse-identical — the
    // create-coupled FSCTL-view artifact quiescent raw probes never show)
    // for the first reads after the fresh-create epoch, then settles to
    // parent bytes. The sweep converges on the transition (µs-apart reads
    // agree, so the gate names the slot), the old single re-read would
    // still land inside it, but the settle loop outlasts it → pre-stream
    // skip with zero bulk reads. Bulk is untouched (consistent idle pair):
    // without the loop the gate streams it before the backstop skips, so
    // Empty(ReadChunks) discriminates.
    [Fact]
    public void SettlingVetoSlot_ConvergesAcrossAttempts_SkipsPreStream()
    {
        var vol = SeedVolume("settlingveto.img", 701, 702, 703, 704);
        var log = new TestLogger();
        using var store = NewStore(log);
        var stable = SysUserRecords();
        var trans = SysUserRecords();
        trans[16][1000] ^= 0xFF; // unmasked transitional byte, parse-identical
        using (var v1 = DualVol(new FileBlockSource(vol), SysUserRecords(), stable, AllocBitmap(), recordCount: 24))
            store.ImageFast(v1, "gen1");

        var shadow2 = SysUserRecords();
        shadow2[16] = TouchLsn(shadow2[16]); // masked-only flap: plan gate vetoes, record gate decides
        var shared = new SharedReads();
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = DualVol(counting, shadow2, stable, AllocBitmap(), recordCount: 24))
        {
            v2.LiveFactory = () => SettlingLive(stable, trans, 16, shared, transitionReads: 6);
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(HasLine(log, "skip-records-retry: re-read [16] -> still-changed []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }
}
