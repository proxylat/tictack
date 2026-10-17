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

    private static FakeMftVolume MftVol(IBlockSource inner, Dictionary<long, byte[]> records) =>
        new(records: records, mftRuns: new (long, long)[] { (0, 100) }, inner: inner);

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
        // on-box remount signature) yields skip: the pre-stream plan is
        // sys-polluted so streaming happens, but the post-stream
        // manifest compare proves every diff is sys-confined.
        var vol = SeedVolume("mftvol.img", RandomChunk(71), RandomChunk(72), RandomChunk(73), RandomChunk(74));
        using var store = NewStore();
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
}
