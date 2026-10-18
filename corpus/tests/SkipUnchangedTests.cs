namespace Corpus;

// §12.8 skip-if-unchanged: exit-0-without-gen when the source matches the
// latest snapshot. Seeded-store style (no subprocess — Store is the surface
// the CLI maps to exit 0; StoredSkippedException + the exact log line are
// the contract).
public sealed class SkipUnchangedTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _volPath;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public SkipUnchangedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_skip_" + Guid.NewGuid());
        _volPath = Path.Combine(_dir, "vol.img");
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
        WriteChunks(_volPath, new byte[Mib], RandomChunk(42), new byte[Mib], RandomChunk(42), TextChunk());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private static FakeChangeSource EmptyPlan() =>
        new((_, _, _) => new IncrementalPlan(new HashSet<long>(), "fake:empty"));

    private static FakeChangeSource Dirty(params long[] chunks) =>
        new((_, _, _) => new IncrementalPlan(new HashSet<long>(chunks), "fake:1"));

    private int ManifestCount() =>
        Directory.Exists(Path.Combine(_storeRoot, "manifests"))
            ? Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length : 0;

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] TextChunk()
    {
        var text = new byte[Mib];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (var i = 0; i < text.Length; i++)
            text[i] = pattern[i % pattern.Length];
        return text;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    [Fact]
    public void Classic_EmptyPlan_SkipsBeforeBulkRead()
    {
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");

        using var counting = new CountingBlockSource(_volPath, Mib);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.Image(counting, "gen2",
                new ImageOptions(Incremental: true, ChangeSource: EmptyPlan()), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Equal("unchanged since gen 1, skipping", ex.Message);
        Assert.Empty(counting.ReadChunks); // no bulk read at all
        Assert.Equal(1, ManifestCount()); // gen unconsumed
        Assert.Contains(log.Messages, m => m.Contains("unchanged since gen 1, skipping"));
    }

    [Fact]
    public void Classic_NonEmptyPlan_ZeroNewChunks_WithholdsManifest()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");

        // Dirty plan, identical bytes: bulk read happens, but every chunk is
        // already indexed → manifest withheld, gen unconsumed.
        using var counting = new CountingBlockSource(_volPath, Mib);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.Image(counting, "gen2",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0, 1, 2, 3, 4)), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Equal(5, counting.ReadChunks.Count);
        Assert.Equal(1, ManifestCount());

        // Mutate → third run really runs, and reuses the freed gen number.
        var mutated = RandomChunk(999);
        using (var f = new FileStream(_volPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            f.Seek(2 * Mib, SeekOrigin.Begin);
            f.Write(mutated, 0, mutated.Length);
        }
        using var source2 = new FileBlockSource(_volPath);
        var child = store.Image(source2, "gen2",
            new ImageOptions(Incremental: true, ChangeSource: Dirty(2)), skipIfUnchanged: true);
        Assert.Equal(2, child.Generation);
        Assert.Equal(1, child.ParentGeneration);
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Classic_DefaultSource_SecondRunSkips()
    {
        // No ChangeSource: FileBlockSource yields a null plan (full fallback)
        // → read+hash path → zero new → skip. This is the wiring the CLI gets
        // for free (MftDigestChangeSource default).
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        using var source2 = new FileBlockSource(_volPath);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.Image(source2, "gen2", new ImageOptions(Incremental: true), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Equal(1, ManifestCount());
    }

    [Fact]
    public void Classic_PlainReimage_Skips()
    {
        // No --incremental either: full re-read, zero new → skip.
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        using var source2 = new FileBlockSource(_volPath);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.Image(source2, "gen2", new ImageOptions(), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Equal(1, ManifestCount());
    }

    [Fact]
    public void Classic_ForceFull_OverridesSkip()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        using var source2 = new FileBlockSource(_volPath);
        var m = store.Image(source2, "gen2",
            new ImageOptions(Incremental: true, ForceFull: true, ChangeSource: EmptyPlan()), skipIfUnchanged: true);
        Assert.Equal(2, m.Generation);
        Assert.Equal(0, m.ParentGeneration);
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void Classic_NoParent_NeverSkips()
    {
        // First image always runs, even with an empty plan + the flag.
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        var m = store.Image(source, "gen1",
            new ImageOptions(Incremental: true, ChangeSource: EmptyPlan()), skipIfUnchanged: true);
        Assert.Equal(1, m.Generation);
        Assert.Equal(0, m.ParentGeneration);
    }

    [Fact]
    public void Classic_SkipOff_UnchangedWritesGen()
    {
        // Flag absent = today's behavior, byte-identical (regression pin).
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.Image(source, "gen1");
        using var source2 = new FileBlockSource(_volPath);
        var m = store.Image(source2, "gen2",
            new ImageOptions(Incremental: true, ChangeSource: EmptyPlan()));
        Assert.Equal(2, m.Generation);
        Assert.Equal(1, m.ParentGeneration);
    }

    [Fact]
    public void Landing_EmptyPlan_SkipsBeforeBulkStream()
    {
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen1");

        using var counting = new CountingBlockSource(_volPath, Mib);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(counting, "gen2",
                new ImageOptions(ChangeSource: EmptyPlan()), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks); // MFT-digest plan only, no streaming
        Assert.Equal(1, ManifestCount());
        Assert.Contains(log.Messages, m => m.Contains("unchanged since gen 1, skipping"));
    }

    [Fact]
    public void Landing_NullPlan_ZeroNew_WithholdsAndCleans()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen1");
        Assert.True(store.Verify(1));
        store.OptimizeLanding(); // landing empty, bytes indexed

        // No plan computable (file source) → stream; every chunk index-hit →
        // skip. The just-landed duplicates are removed (all hexes stay
        // index-resident), gen unconsumed, gen 1 still verifies.
        using var source2 = new FileBlockSource(_volPath);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(source2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Equal(1, ManifestCount());
        Assert.Equal(0, store.PendingLandingCount());
        Assert.True(store.Verify(1));
        var outPath = Path.Combine(_dir, "rt.img");
        store.Export(1, outPath);
        Assert.Equal(File.ReadAllBytes(_volPath), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Landing_NoParent_NeverSkips()
    {
        using var store = NewStore();
        using var source = new FileBlockSource(_volPath);
        var m = store.ImageFast(source, "gen1", skipIfUnchanged: true);
        Assert.Equal(1, m.Generation);
    }

    [Fact]
    public void Landing_ForceFull_OverridesSkip()
    {
        using var store = NewStore();
        using (var source = new FileBlockSource(_volPath))
            store.ImageFast(source, "gen1");
        using var source2 = new FileBlockSource(_volPath);
        var m = store.ImageFast(source2, "gen2",
            new ImageOptions(ForceFull: true, ChangeSource: EmptyPlan()), skipIfUnchanged: true);
        Assert.Equal(2, m.Generation);
    }

    private static string NewGuid() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    private DiskCapturePlan BuildPlan(IBlockSource sys, IBlockSource data, string sysGuid, string dataGuid, IChangeSource? change)
    {
        var sysLen = sys.Length;
        var dataLen = data.Length;
        return new DiskCapturePlan("disk-guid-skip", 1L << 40, 512, new List<PartitionImageInput>
        {
            new(1, "sys", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", sysGuid, 2048, 2048 + sysLen / 512 - 1, 2048 * 512, sysLen,
                "ntfs", false, null, sys, change),
            new(2, "data", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", dataGuid, 10240, 10240 + dataLen / 512 - 1, 10240 * 512, dataLen,
                "ntfs", false, null, data, change),
        });
    }

    private string SeedVolume(string name, params byte[][] chunks)
    {
        var vol = Path.Combine(_dir, name);
        WriteChunks(vol, chunks);
        return vol;
    }

    [Fact]
    public void Disk_SkipsWhenEveryPlanEmpty()
    {
        var log = new TestLogger();
        var sysPath = SeedVolume("sys.img", RandomChunk(1), RandomChunk(2));
        var dataPath = SeedVolume("data.img", RandomChunk(3));
        var sysGuid = NewGuid();
        var dataGuid = NewGuid();
        using var store = NewStore(log);
        using (var sys1 = new FileBlockSource(sysPath))
        using (var data1 = new FileBlockSource(dataPath))
            store.ImageDisk(0, "disk0", new DiskImageOptions(), BuildPlan(sys1, data1, sysGuid, dataGuid, null));
        Assert.Equal(2, ManifestCount());

        using var sys2 = new FileBlockSource(sysPath);
        using var data2 = new FileBlockSource(dataPath);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true),
                BuildPlan(sys2, data2, sysGuid, dataGuid, EmptyPlan()), skipIfUnchanged: true));
        Assert.Equal(2, ex.Generation); // latest gen, not per-partition
        Assert.Equal("unchanged since gen 2, skipping", ex.Message);
        Assert.Equal(2, ManifestCount()); // nothing written
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "layouts"), "*.json")); // no new layout
        Assert.Contains(log.Messages, m => m.Contains("unchanged since gen 2, skipping"));
    }

    [Fact]
    public void Disk_RunsWhenAnyPartitionDirty()
    {
        var sysPath = SeedVolume("sys.img", RandomChunk(11), RandomChunk(12));
        var dataPath = SeedVolume("data.img", RandomChunk(13));
        var sysGuid = NewGuid();
        var dataGuid = NewGuid();
        using var store = NewStore();
        using (var sys1 = new FileBlockSource(sysPath))
        using (var data1 = new FileBlockSource(dataPath))
            store.ImageDisk(0, "disk0", new DiskImageOptions(), BuildPlan(sys1, data1, sysGuid, dataGuid, null));

        var sysKey = DiskImager.PartitionKey(0, 1, sysGuid);
        var mixed = new FakeChangeSource((parent, _, _) => parent.PartitionKey == sysKey
            ? new IncrementalPlan(new HashSet<long> { 0 }, "fake:2")
            : new IncrementalPlan(new HashSet<long>(), "fake:2"));
        using var sys2 = new FileBlockSource(sysPath);
        using var data2 = new FileBlockSource(dataPath);
        var layout = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true),
            BuildPlan(sys2, data2, sysGuid, dataGuid, mixed), skipIfUnchanged: true);
        Assert.Equal(4, layout.Generation);
        Assert.Equal(4, ManifestCount());
    }

    [Fact]
    public void Disk_NoParent_NeverSkips()
    {
        var sysPath = SeedVolume("sys.img", RandomChunk(21));
        var dataPath = SeedVolume("data.img", RandomChunk(22));
        using var store = NewStore();
        using var sys1 = new FileBlockSource(sysPath);
        using var data1 = new FileBlockSource(dataPath);
        var layout = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true),
            BuildPlan(sys1, data1, NewGuid(), NewGuid(), EmptyPlan()), skipIfUnchanged: true);
        Assert.Equal(2, layout.Generation);
    }

    [Fact]
    public void Disk_ForceFull_OverridesSkip()
    {
        var sysPath = SeedVolume("sys.img", RandomChunk(31));
        var dataPath = SeedVolume("data.img", RandomChunk(32));
        var sysGuid = NewGuid();
        var dataGuid = NewGuid();
        using var store = NewStore();
        using (var sys1 = new FileBlockSource(sysPath))
        using (var data1 = new FileBlockSource(dataPath))
            store.ImageDisk(0, "disk0", new DiskImageOptions(), BuildPlan(sys1, data1, sysGuid, dataGuid, null));
        using var sys2 = new FileBlockSource(sysPath);
        using var data2 = new FileBlockSource(dataPath);
        var layout = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true, ForceFull: true),
            BuildPlan(sys2, data2, sysGuid, dataGuid, EmptyPlan()), skipIfUnchanged: true);
        Assert.Equal(4, layout.Generation);
    }

    // §9 skip gate on live NTFS: FRN<16 sys extents ($MFT/$MFTMirr/
    // $LogFile zones) churn on every mount, so a gate counting them as
    // user changes never fires. Grid: chunkMib 1, bpc 4096 → chunk i =
    // LCNs [i*256,(i+1)*256). Sys runs parked at LCN 0-7 (chunk 0);
    // the user file at LCN 512-767 (chunk 2).
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

    private static FakeMftVolume MftVol(IBlockSource inner, Dictionary<long, byte[]> records,
        long recordCount = 20, List<MftEnumEntry>? entries = null)
    {
        var v = new FakeMftVolume(records: records, mftRuns: new (long, long)[] { (0, 100) },
            inner: inner, recordCount: recordCount);
        if (entries != null)
            v.Entries.AddRange(entries);
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

    [Fact]
    public void Landing_SysZoneChurn_Skips()
    {
        // MFT-zone-only churn (sys record + sys-zone volume bytes, the
        // on-box remount signature) yields skip via the PRE-STREAM record
        // gate: only FRN 0 changes, so no bulk read happens at all (the
        // post-stream manifest backstop below stays silent — no
        // skip-compare lines — proving the fast path fired).
        var vol = SeedVolume("mftvol.img", RandomChunk(71), RandomChunk(72), RandomChunk(73), RandomChunk(74));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var churned = SysUserRecords();
        churned[0][1023] ^= 0xFF; // record padding: flips the group digest, parses identically
        MutateBytes(vol, 1000, 100); // inside LCN 0-7 (chunk 0), outside every user extent
        using (var v2 = MftVol(new FileBlockSource(vol), churned))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Equal(1, ManifestCount());
        Assert.True(store.Verify(1));
        Assert.Contains(log.Messages, m => m.Contains("skip-records:") && m.Contains("outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
    }

    [Fact]
    public void Landing_UserExtentChange_WritesGen()
    {
        // A single user-extent write must still trip the gate: the
        // differing chunk is outside every sys run, so no skip.
        var vol = SeedVolume("mftvol2.img", RandomChunk(81), RandomChunk(82), RandomChunk(83), RandomChunk(84));
        using var store = NewStore();
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var changed = SysUserRecords();
        changed[16][1023] ^= 0xFF;
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the user file's clusters
        using (var v2 = MftVol(new FileBlockSource(vol), changed))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(store.Verify(2));
        var outPath = Path.Combine(_dir, "mft-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Landing_SysOnlyDirty_SkipsBeforeBulkStream()
    {
        // Pre-stream fast path: a plan dirty ONLY in sys chunks skips
        // with zero bulk reads (the seconds-path); user dirt forces
        // the streaming path even when the bytes turn out identical.
        var vol = SeedVolume("mftvol3.img", RandomChunk(91), RandomChunk(92), RandomChunk(93), RandomChunk(94));
        using var store = NewStore();
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, SysUserRecords());
        var sysOnly = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long> { 0 }, "fake:1"));
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", new ImageOptions(ChangeSource: sysOnly), skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());

        using var inner2 = new FileBlockSource(vol);
        using var counting2 = new CountingBlockSource(inner2, Mib);
        using var v3 = MftVol(counting2, SysUserRecords());
        var userDirty = new FakeChangeSource((_, _, _) => new IncrementalPlan(new HashSet<long> { 2 }, "fake:2"));
        var ex2 = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v3, "gen2b", new ImageOptions(ChangeSource: userDirty), skipIfUnchanged: true));
        Assert.Equal(1, ex2.Generation);
        Assert.NotEmpty(counting2.ReadChunks);
        Assert.Equal(1, ManifestCount());
    }

    // §9 gate-1 repair fixtures: advisory metadata ($TxfLog container FRN
    // 24, $UsnJrnl host FRN 27) with MFT-enumeration entries so MftSkipMask
    // resolves them from current geometry. Grid (chunkMib 1, bpc 4096):
    // chunk 0 = sys LCN 0-7 + TxF LCN 16-255; chunk 2 = user LCN 512-767;
    // chunk 3 head = journal LCN 768-895; chunk 1 = free (relocation target).
    private static Dictionary<long, byte[]> AdvisoryRecords((long Count, long Lcn)? journalRun = null)
    {
        var records = SysUserRecords();
        records[24] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((240, 16))));
        var jr = journalRun ?? (128L, 768L);
        records[27] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((jr.Count, jr.Lcn))));
        return records;
    }

    private static List<MftEnumEntry> AdvisoryEntries() => new()
    {
        new MftEnumEntry((7UL << 48) | 11, 5, "$Extend", 0x10),
        new MftEnumEntry((7UL << 48) | 30, (7UL << 48) | 11, "$RmMetadata", 0x10),
        new MftEnumEntry((7UL << 48) | 31, (7UL << 48) | 30, "$TxfLog", 0x10),
        new MftEnumEntry((7UL << 48) | 24, (7UL << 48) | 31, "TxfLogContainer00000000000000000001", 0x20),
        new MftEnumEntry((7UL << 48) | 27, (7UL << 48) | 11, "$UsnJrnl", 0x20),
    };

    // Old-manifest simulation (pre-record-digests): the record gate must
    // abstain and the streaming backstop decides alone.
    private void StripRecordDigests()
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var key = ChunkId.EnsureKey(_storeRoot);
        var path = Directory.GetFiles(manDir, "000001-*.json")[0];
        var m = ManifestStore.Load(path, key);
        m.MftRecordDigests = null;
        ManifestStore.Save(manDir, m, key);
    }

    private static bool HasLine(TestLogger log, string fragment) =>
        log.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void RecordLevel_SysPadFlip_SkipsBeforeBulkStream()
    {
        // Group-flip-only fixture: one sys record's padding byte flips the
        // single LSN-inclusive group digest, so the capture plan is polluted
        // (whole-volume dirty) — but the record-level decision sees only
        // FRN 3 changed and skips with zero bulk reads.
        var vol = SeedVolume("mftrec.img", RandomChunk(121), RandomChunk(122), RandomChunk(123), RandomChunk(124));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var flipped = SysUserRecords();
        flipped[3][1023] ^= 0xFF;
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, flipped);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-plan:") && HasLine(log, "first-outside: [2]"));
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: []"));
        Assert.False(HasLine(log, "skip-compare:"));
    }

    [Fact]
    public void RecordLevel_UserPadFlip_StreamsThenZeroNewSkips()
    {
        // Same group flip on a USER record must refuse the pre-stream gate
        // (a changed resident file would hide in the $MFT runs, so FRN
        // membership refuses without extent math) — streaming then finds
        // zero new chunks and skips via the classic backstop.
        var vol = SeedVolume("mftrec2.img", RandomChunk(131), RandomChunk(132), RandomChunk(133), RandomChunk(134));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var flipped = SysUserRecords();
        flipped[16][1023] ^= 0xFF;
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, flipped);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.NotEmpty(counting.ReadChunks); // refused pre-stream, bulk happened
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.False(HasLine(log, "skip-compare:")); // zero-new short-circuit, no masked compare
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void TxfLogChurn_IsSkipSafe_Skips()
    {
        // $TxfLog containers churn on every mount/VSS touch yet live outside
        // FRN 0..15 — the on-box chunk-0 killer. TxF is deprecated and its
        // bytes never restore anything (in-flight txns abort on crash), so
        // the mask names it explicitly instead of widening the FRN range.
        var vol = SeedVolume("mfttxf.img", RandomChunk(141), RandomChunk(142), RandomChunk(143), RandomChunk(144));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), AdvisoryRecords(), recordCount: 32, entries: AdvisoryEntries()))
            store.ImageFast(v1, "gen1");

        var churned = AdvisoryRecords();
        churned[24][1023] ^= 0xFF; // container record touched, extents identical
        MutateBytes(vol, 100L * 4096, 100); // LCN 100: inside the TxF range, chunk 0
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned, recordCount: 32, entries: AdvisoryEntries());
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "advisory txf 1/2 journal 1/1"));
        Assert.True(HasLine(log, "skip-records: 1 changed of 19 records, outside-sys: []"));
    }

    [Fact]
    public void TxfLogSharing_UserChange_RefusesViaBackstop()
    {
        // Masking is byte-precise, not chunk-blanket: TxF churn in chunk 0
        // plus a genuine user write in chunk 2 must write gen 2 even with an
        // old (record-digest-less) manifest forcing the backstop path. Pins
        // the gate-2 lines: chunk 0 masked-equal, chunk 2 no-overlap.
        var vol = SeedVolume("mfttxf2.img", RandomChunk(151), RandomChunk(152), RandomChunk(153), RandomChunk(154));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), AdvisoryRecords(), recordCount: 32, entries: AdvisoryEntries()))
            store.ImageFast(v1, "gen1");
        StripRecordDigests();

        MutateBytes(vol, 100L * 4096, 100); // TxF range, chunk 0 (masked)
        MutateBytes(vol, 2L * Mib + 1000, 100); // user file range, chunk 2 (not masked)
        var dirtied = AdvisoryRecords();
        dirtied[16][1023] ^= 0xFF; // user record touched: pollutes the plan past the pre-stream gate
        using var v2 = MftVol(new FileBlockSource(vol), dirtied, recordCount: 32, entries: AdvisoryEntries());
        var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
        Assert.Equal(2, m.Generation);
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-compare: chunk 0 overlap=Y reads=ok masked-equal=Y"));
        Assert.True(HasLine(log, "skip-compare: chunk 2 overlap=N"));
        var outPath = Path.Combine(_dir, "txf-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
    }

    [Fact]
    public void JournalStableChurn_SkipsViaBackstop()
    {
        // Same-$J-range journal churn with an old manifest: the backstop
        // masks the current-geometry $J bytes and skips post-stream,
        // cleaning the just-landed duplicates.
        var vol = SeedVolume("mftjrnl.img", RandomChunk(161), RandomChunk(162), RandomChunk(163), RandomChunk(164));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), AdvisoryRecords(), recordCount: 32, entries: AdvisoryEntries()))
            store.ImageFast(v1, "gen1");
        Assert.True(store.Verify(1));
        store.OptimizeLanding(); // fold gen1 into packs so the skip must clean just-landed dupes
        StripRecordDigests();

        var churned = AdvisoryRecords();
        churned[27][1023] ^= 0xFF;
        MutateBytes(vol, 800L * 4096, 100); // LCN 800: inside the $J range, chunk 3
        using (var v2 = MftVol(new FileBlockSource(vol), churned, recordCount: 32, entries: AdvisoryEntries()))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Equal(1, ManifestCount());
        Assert.Equal(0, store.PendingLandingCount());
        Assert.True(HasLine(log, "advisory txf 1/2 journal 1/1"));
        Assert.True(HasLine(log, "skip-compare: chunk 3 overlap=Y reads=ok masked-equal=Y"));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void JournalRelocation_WritesGen()
    {
        // $J moved (LCN 768→256): the abandoned bytes are NOT covered by the
        // current-geometry mask, so the backstop fails to skip — never
        // wrong-skips. (On a new manifest the record gate would also refuse
        // only if a user record moved with it; pure relocation is advisory.)
        var vol = SeedVolume("mftjrnl2.img", RandomChunk(171), RandomChunk(172), RandomChunk(173), RandomChunk(174));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), AdvisoryRecords(), recordCount: 32, entries: AdvisoryEntries()))
            store.ImageFast(v1, "gen1");
        StripRecordDigests();

        var moved = AdvisoryRecords((128L, 256L)); // $J now LCN 256-383, chunk 1
        MutateBytes(vol, 300L * 4096, 100); // new-range journal bytes
        MutateBytes(vol, 800L * 4096, 100); // old-range reuse churn (unmasked now)
        using (var v2 = MftVol(new FileBlockSource(vol), moved, recordCount: 32, entries: AdvisoryEntries()))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-compare: chunk 3 overlap=N"));
        var outPath = Path.Combine(_dir, "jrnl-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    [Fact]
    public void SysExtentDrop_NullNamesFrnAndRefuses()
    {
        // FRN 2 ($LogFile) goes dead: its extents drop SILENTLY from the mask
        // except for the sys-extents line naming "2:null" — and the $LogFile
        // byte churn it used to cover now refuses the skip.
        var vol = SeedVolume("mftdrop.img", RandomChunk(181), RandomChunk(182), RandomChunk(183), RandomChunk(184));
        var log = new TestLogger();
        var sys = SysUserRecords();
        sys[2] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 8)))); // unique LCN 8-15
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), sys))
            store.ImageFast(v1, "gen1");
        StripRecordDigests();

        var dead = SysUserRecords();
        dead[2] = new byte[1024]; // zeroed slot: digest flips, parses to null
        MutateBytes(vol, 10L * 4096, 100); // LCN 10: was $LogFile-only cover, chunk 0
        using (var v2 = MftVol(new FileBlockSource(vol), dead))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.True(HasLine(log, "dropped: [2:null]"));
        Assert.True(HasLine(log, "skip-compare: chunk 0 overlap=Y reads=ok masked-equal=N"));
    }

    [Fact]
    public void SysExtentException_RefusesTotal()
    {
        // FRN 2 live-but-corrupt (bad attribute offset → throws, not null):
        // the whole mask is unknown, so the gate stays strict. Distinct from
        // the null-drop above — the log names the exception type.
        var vol = SeedVolume("mftdrop2.img", RandomChunk(191), RandomChunk(192), RandomChunk(193), RandomChunk(194));
        var log = new TestLogger();
        var sys = SysUserRecords();
        sys[2] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 8))));
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), sys))
            store.ImageFast(v1, "gen1");
        StripRecordDigests();

        var bad = SysUserRecords();
        bad[2] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 8))));
        BitConverter.GetBytes((ushort)9999).CopyTo(bad[2], 0x14); // live + in-use, corrupt attr offset
        MutateBytes(vol, 10L * 4096, 100);
        using (var v2 = MftVol(new FileBlockSource(vol), bad))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.True(HasLine(log, "sys-extents: unresolved (frn 2: InvalidDataException)"));
    }

    // B6E miss-1 shape: a mount/VSS touch rewrites records (LSN+USN bump)
    // without changing a single content byte. Stamps every record's counters
    // (USA [42,48), parser-invisible) with a different base per run.
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

    [Fact]
    public void RecordLevel_MountTouchOnly_SkipsBeforeBulkStream()
    {
        // B6E re-proof in fakes: LSN+USN rewritten on EVERY record (sys and
        // user alike), zero volume-byte change. Groups flip (raw digests) so
        // the capture plan is whole-volume dirty — but the record gate sees
        // only counter churn and skips with zero bulk reads.
        var vol = SeedVolume("mfttouch.img", RandomChunk(201), RandomChunk(202), RandomChunk(203), RandomChunk(204));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), TouchCounters(SysUserRecords())))
            store.ImageFast(v1, "gen1");

        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, TouchCounters(SysUserRecords(), lsnBase: 9000));
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records:") && HasLine(log, "outside-sys: []"));
        Assert.False(HasLine(log, "skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // B6E miss-2 shapes: the live enumeration carries advisory rows whose
    // $Extend-chain parents are missing, so FullPath degrades and the pathed
    // norm misses a PRESENT row. Bare-$ resolution still masks it.
    private static List<MftEnumEntry> OrphanedJournalEntries() => new()
    {
        new MftEnumEntry((7UL << 48) | 27, (7UL << 48) | 11, "$UsnJrnl", 0x20), // $Extend row absent
    };

    private static List<MftEnumEntry> OrphanedTxfEntries() => new()
    {
        new MftEnumEntry((7UL << 48) | 31, (7UL << 48) | 999, "$TxfLog", 0x10), // $RmMetadata row absent
        new MftEnumEntry((7UL << 48) | 24, (7UL << 48) | 31, "TxfLogContainer00000000000000000001", 0x20),
    };

    [Fact]
    public void JournalChainBroken_StillMasked_Skips()
    {
        // The exact B6E live shape for journal: $UsnJrnl row present, $Extend
        // row absent. Pathed "$Extend\$UsnJrnl" misses; bare "$UsnJrnl" hits,
        // so journal churn skips pre-stream (would write gen 2 unmasked).
        var vol = SeedVolume("mftorphan.img", RandomChunk(211), RandomChunk(212), RandomChunk(213), RandomChunk(214));
        var log = new TestLogger();
        using var store = NewStore(log);
        var recs = SysUserRecords();
        recs[27] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((128, 768))));
        using (var v1 = MftVol(new FileBlockSource(vol), recs, recordCount: 32, entries: OrphanedJournalEntries()))
            store.ImageFast(v1, "gen1");

        var churned = SysUserRecords();
        churned[27] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((128, 768))));
        churned[27][1023] ^= 0xFF;
        MutateBytes(vol, 800L * 4096, 100); // LCN 800: inside the $J range, chunk 3
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned, recordCount: 32, entries: OrphanedJournalEntries());
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "txf 0/0 journal 1/1"));
        Assert.True(HasLine(log, "outside-sys: []"));
    }

    [Fact]
    public void TxfChainBroken_StillMasked_Skips()
    {
        // Same break one level deeper: $TxfLog dir + container rows present,
        // $RmMetadata/$Extend parents absent. Bare "$TxfLog" hits the dir and
        // the subtree covers the container — identical counts to the
        // complete-chain fixture above (txf 1/2: dir row has no record bytes).
        var vol = SeedVolume("mftorphan2.img", RandomChunk(221), RandomChunk(222), RandomChunk(223), RandomChunk(224));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), AdvisoryRecords(), recordCount: 32, entries: OrphanedTxfEntries()))
            store.ImageFast(v1, "gen1");

        var churned = AdvisoryRecords();
        churned[24][1023] ^= 0xFF;
        MutateBytes(vol, 100L * 4096, 100); // LCN 100: inside the TxF range, chunk 0
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned, recordCount: 32, entries: OrphanedTxfEntries());
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "advisory txf 1/2 journal 0/0"));
        Assert.True(HasLine(log, "outside-sys: []"));
    }

    [Fact]
    public void AdvisoryAbsent_Unmasked_WritesGen()
    {
        // Enumeration with NO advisory rows at all (TxF/USN paths genuinely
        // absent on this volume): journal churn is not maskable — the gate
        // writes the gen (never wrong-skips) and the counts line names 0/0.
        var vol = SeedVolume("mftnoadv.img", RandomChunk(231), RandomChunk(232), RandomChunk(233), RandomChunk(234));
        var log = new TestLogger();
        using var store = NewStore(log);
        var recs = SysUserRecords();
        recs[27] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((128, 768))));
        using (var v1 = MftVol(new FileBlockSource(vol), recs, recordCount: 32))
            store.ImageFast(v1, "gen1");

        var churned = SysUserRecords();
        churned[27] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((128, 768))));
        churned[27][1023] ^= 0xFF;
        MutateBytes(vol, 800L * 4096, 100);
        using (var v2 = MftVol(new FileBlockSource(vol), churned, recordCount: 32))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "advisory txf 0/0 journal 0/0"));
        Assert.True(HasLine(log, "outside-sys: [27]"));
        Assert.True(HasLine(log, "skip-compare: chunk 3 overlap=N"));
        var outPath = Path.Combine(_dir, "noadv-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // B6F re-proof in fakes: the 207 empty FRN slots are byte-static live
    // but read unstable through the VSS shadow (zeros vs garbage across
    // runs). The gate must exclude them, not compare them: run1 zeros vs
    // run2 garbage skips pre-stream (zero bulk reads, no skip-compare).
    [Fact]
    public void EmptySlotInstability_SkipsPreStream()
    {
        var vol = SeedVolume("mftempty.img", RandomChunk(241), RandomChunk(242), RandomChunk(243), RandomChunk(244));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var unstable = SysUserRecords();
        for (var r = 17L; r < 20; r++)
        {
            var g = new byte[1024];
            new Random(1000 + (int)r).NextBytes(g);
            g[0] = (byte)'G'; g[1] = (byte)'A'; g[2] = (byte)'R'; g[3] = (byte)'B'; // not FILE magic, like unstable shadow bytes
            unstable[r] = g;
        }
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = MftVol(counting, unstable))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.True(HasLine(log, "empty:3"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // B6H re-proof in fakes: rec55 on-box is FILE-magic but flags=0x0
    // (dead-stale yet hashed). A dead user record with churned bytes must
    // drop out of the changed set, not veto the skip. rec17 (live,
    // chunk-3 extents) forces the plan gate open so the record gate
    // decides: without it the dead slot contributes no planner dirty and
    // the plan gate would fire first.
    [Fact]
    public void DeadFlagRecord_SkipsPreStream()
    {
        var vol = SeedVolume("mftdead.img", RandomChunk(251), RandomChunk(252), RandomChunk(253), RandomChunk(254));
        var log = new TestLogger();
        using var store = NewStore(log);
        var live17 = SysUserRecords();
        live17[17] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 768))));
        using (var v1 = MftVol(new FileBlockSource(vol), live17))
            store.ImageFast(v1, "gen1");

        var died = SysUserRecords();
        died[17] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 768))));
        died[16] = MftRecordBuilder.LiveFlags(1024, 7, 0,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))));
        died[16][1023] ^= 0xFF; // stale dead bytes churn, like the on-box rec55
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using (var v2 = MftVol(counting, died))
        {
            var ex = Assert.Throws<StoredSkippedException>(() =>
                store.ImageFast(v2, "gen2", skipIfUnchanged: true));
            Assert.Equal(1, ex.Generation);
        }
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.True(HasLine(log, "dead:1"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    // No over-skipping: a LIVE user-record change still refuses the record
    // gate (outside-sys names the FRN) and writes the gen.
    [Fact]
    public void LiveUserRecordChange_WritesGen()
    {
        var vol = SeedVolume("mftlive.img", RandomChunk(261), RandomChunk(262), RandomChunk(263), RandomChunk(264));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords()))
            store.ImageFast(v1, "gen1");

        var changed = SysUserRecords();
        changed[16][1023] ^= 0xFF; // live user record, padding flip
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the user file's clusters
        using (var v2 = MftVol(new FileBlockSource(vol), changed))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [16]"));
        Assert.True(store.Verify(2));
    }

    // B6K re-proof fixtures: the VSS create/delete cycle moves LIVE bytes
    // on records the FRN<16 + TxF/journal mask never covered — $Tops (32),
    // $TxfLog.blf (33), orphan TxF containers (34, dir row absent so even
    // the subtree misses it), the SVI dir (40, FRNs vary on-box), and the
    // transient {guid} diff file itself (56, new name every cycle). Each
    // flips below and the pre-stream record gate must still skip with zero
    // bulk reads. Grid: chunkMib 1, bpc 4096; VSS runs parked at LCN
    // 240-255 (chunk 0) + diff runs at LCN 600-855 (chunks 2-3); rec16
    // (user, chunk 2) pollutes the group plan so the record gate decides.
    private static Dictionary<long, byte[]> VssMachineryRecords()
    {
        var records = SysUserRecords();
        records[32] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 240))));
        records[33] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 260))));
        records[34] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 270))));
        records[40] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 400))));
        records[56] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((8, 100))));
        return records;
    }

    private static List<MftEnumEntry> VssTopsEntries() => new()
    {
        new MftEnumEntry((7UL << 48) | 32, (7UL << 48) | 5, "$Tops", 0x20),
        new MftEnumEntry((7UL << 48) | 33, (7UL << 48) | 5, "$TxfLog.blf", 0x20),
        // Orphan container: the $TxfLog dir row itself is absent, so the
        // subtree never covers it — the TxfLogContainer prefix must.
        new MftEnumEntry((7UL << 48) | 34, (7UL << 48) | 5, "TxfLogContainer00000000000000000002", 0x20),
    };

    private static List<MftEnumEntry> VssSviEntries(string diffName) => new()
    {
        new MftEnumEntry((7UL << 48) | 40, (7UL << 48) | 5, "System Volume Information", 0x10),
        new MftEnumEntry((7UL << 48) | 56, (7UL << 48) | 40, diffName, 0x20),
    };

    [Fact]
    public void VssTxfIdentitiesChurn_SkipsPreStream()
    {
        // $Tops + $TxfLog.blf + orphan container flip (LSN/USN/content
        // re-stamps the live shadow carries): all three are VSS machinery,
        // none is user data — skip with zero bulk reads.
        var vol = SeedVolume("mftvss.img", RandomChunk(271), RandomChunk(272), RandomChunk(273), RandomChunk(274));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), VssMachineryRecords(), recordCount: 64, entries: VssTopsEntries()))
            store.ImageFast(v1, "gen1");

        var churned = VssMachineryRecords();
        churned[32][1023] ^= 0xFF;
        churned[33][1023] ^= 0xFF;
        churned[34][1023] ^= 0xFF;
        MutateBytes(vol, 240L * 4096, 100); // LCN 240: inside the TxF range, chunk 0
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned, recordCount: 64, entries: VssTopsEntries());
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 3 changed of 22 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void SviDirAndDiffFileChurn_SkipsPreStream()
    {
        // SVI dir (timestamps + LSN, B6K rec36) plus the transient {guid}
        // diff file itself (new name every snapshot, B6K rec56): both flip,
        // both are VSS machinery — skip with zero bulk reads.
        var vol = SeedVolume("mftsvi.img", RandomChunk(281), RandomChunk(282), RandomChunk(283), RandomChunk(284));
        var log = new TestLogger();
        const string diff = "{3808876b-c176-4d9f-9d4e-000000000001}";
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), VssMachineryRecords(), recordCount: 64, entries: VssSviEntries(diff)))
            store.ImageFast(v1, "gen1");

        var churned = VssMachineryRecords();
        churned[40][1023] ^= 0xFF;
        churned[56][1023] ^= 0xFF;
        MutateBytes(vol, 100L * 4096, 100); // LCN 100: inside the diff range, chunk 0
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned, recordCount: 64, entries: VssSviEntries(diff));
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 2 changed of 22 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void GuidNamedUserFile_StillVetoes()
    {
        // Discrimination, other direction: the SAME braced-{guid} name shape
        // under a non-SVI parent is a user file, not a diff file — its flip
        // must refuse the gate and write the gen. (The SVI dir itself rides
        // along unflipped and stays excluded.)
        var vol = SeedVolume("mftguid.img", RandomChunk(291), RandomChunk(292), RandomChunk(293), RandomChunk(294));
        var log = new TestLogger();
        const string userGuid = "{3808876b-c176-4d9f-9d4e-000000000002}";
        var entries = new List<MftEnumEntry>
        {
            new MftEnumEntry((7UL << 48) | 40, (7UL << 48) | 5, "System Volume Information", 0x10),
            new MftEnumEntry((7UL << 48) | 60, (7UL << 48) | 5, userGuid, 0x20),
        };
        using var store = NewStore(log);
        var recs = VssMachineryRecords();
        recs[60] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))));
        using (var v1 = MftVol(new FileBlockSource(vol), recs, recordCount: 64, entries: entries))
            store.ImageFast(v1, "gen1");

        var changed = VssMachineryRecords();
        changed[60] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((256, 512))));
        changed[60][1023] ^= 0xFF;
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the user file's clusters
        using (var v2 = MftVol(new FileBlockSource(vol), changed, recordCount: 64, entries: entries))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 23 records, outside-sys: [60]"));
        var outPath = Path.Combine(_dir, "guid-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // Root (FRN 5) carries a real $STANDARD_INFORMATION + index here (fakes
    // elsewhere use a bare data run): SI timestamp churn (B6K rec5: the SVI
    // children change bumps the root mtime) must stay invisible, while an
    // index flip (a namespace change) refuses like any user record.
    private static byte[] SiAttr(ulong mtime)
    {
        var a = new byte[24 + 48];
        BitConverter.GetBytes(0x10u).CopyTo(a, 0);
        BitConverter.GetBytes((uint)a.Length).CopyTo(a, 4);
        a[8] = 0; // resident
        BitConverter.GetBytes(48u).CopyTo(a, 16); // contentLen
        BitConverter.GetBytes((ushort)24).CopyTo(a, 20); // contentOff
        for (var i = 0; i < 4; i++)
            BitConverter.GetBytes(0x1D0000000000000UL + mtime + (ulong)i).CopyTo(a, 24 + 8 * i);
        return a;
    }

    private static Dictionary<long, byte[]> RootSiRecords(ulong mtime = 7)
    {
        var records = SysUserRecords();
        records[5] = MftRecordBuilder.Live(1024, 7, SiAttr(mtime),
            MftRecordBuilder.DataAttr(false, [], type: 0x90)); // resident $INDEX_ROOT stub
        return records;
    }

    [Fact]
    public void RootTimestampFlip_SkipsPreStream()
    {
        var vol = SeedVolume("mftroot.img", RandomChunk(301), RandomChunk(302), RandomChunk(303), RandomChunk(304));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), RootSiRecords()))
            store.ImageFast(v1, "gen1");

        var churned = RootSiRecords(mtime: 9); // SI timestamps bumped, index identical
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, churned);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void RootIndexFlip_WritesGen()
    {
        // Same root record, but the INDEX half moves (a file was created in
        // /): FRN 5 is no longer blanket-masked, so the gate refuses and the
        // gen records the namespace change. Growth-tail still backs it: the
        // fresh slot past the parent count refuses on its own.
        var vol = SeedVolume("mftroot2.img", RandomChunk(311), RandomChunk(312), RandomChunk(313), RandomChunk(314));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), RootSiRecords()))
            store.ImageFast(v1, "gen1");

        var changed = RootSiRecords();
        changed[5][130] ^= 0xFF; // inside the resident $INDEX_ROOT stub (attr at 120), past the SI content
        MutateBytes(vol, 2L * Mib + 1000, 100); // chunk 2: the new file's clusters
        using (var v2 = MftVol(new FileBlockSource(vol), changed))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 17 records, outside-sys: [5]"));
        var outPath = Path.Combine(_dir, "root-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }

    // B6K USA-tail finding: re-stamps flip the sector-end fixup words
    // ([510:512)/[1022:1024)) alongside the USA block. The tail mask must
    // cover them (usaOff/usaCount-driven stride), so a tails-only touch on
    // sys AND user records still skips with zero bulk reads.
    [Fact]
    public void UsaTailFlip_SkipsPreStream()
    {
        var vol = SeedVolume("mfttail.img", RandomChunk(321), RandomChunk(322), RandomChunk(323), RandomChunk(324));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), TouchCounters(SysUserRecords())))
            store.ImageFast(v1, "gen1");

        var touched = TouchCounters(SysUserRecords(), lsnBase: 9000);
        foreach (var r in new long[] { 0, 3, 6, 16 })
        {
            touched[r][510] ^= 0xFF;
            touched[r][511] ^= 0xFF;
            touched[r][1022] ^= 0xFF;
            touched[r][1023] ^= 0xFF;
        }
        using var inner = new FileBlockSource(vol);
        using var counting = new CountingBlockSource(inner, Mib);
        using var v2 = MftVol(counting, touched);
        var ex = Assert.Throws<StoredSkippedException>(() =>
            store.ImageFast(v2, "gen2", skipIfUnchanged: true));
        Assert.Equal(1, ex.Generation);
        Assert.Empty(counting.ReadChunks);
        Assert.Equal(1, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 0 changed of 17 records, outside-sys: []"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("skip-compare:"));
        Assert.True(store.Verify(1));
    }

    [Fact]
    public void GrowthTailLiveSlot_WritesGen()
    {
        // A LIVE slot past the parent count is an unproven new allocation:
        // the record gate refuses (names the tail FRN) and the gen records
        // it. Deletion-visibility delay is accepted elsewhere (sentinel
        // tails drop out); growth never skips blind.
        var vol = SeedVolume("mftgrowth.img", RandomChunk(331), RandomChunk(332), RandomChunk(333), RandomChunk(334));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var v1 = MftVol(new FileBlockSource(vol), SysUserRecords(), recordCount: 20))
            store.ImageFast(v1, "gen1");

        var grown = SysUserRecords();
        grown[20] = MftRecordBuilder.Live(1024, 7,
            MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((16, 900))));
        MutateBytes(vol, 900L * 4096, 100); // LCN 900: the new file's clusters, chunk 3
        using (var v2 = MftVol(new FileBlockSource(vol), grown, recordCount: 21))
        {
            var m = store.ImageFast(v2, "gen2", skipIfUnchanged: true);
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
        Assert.True(HasLine(log, "skip-records: 1 changed of 18 records, outside-sys: [20]"));
        var outPath = Path.Combine(_dir, "growth-rt.img");
        store.Export(2, outPath);
        Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        Assert.True(store.Verify(2));
    }
}
