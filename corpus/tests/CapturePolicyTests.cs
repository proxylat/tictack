namespace Corpus;

// Items 1-minimal/2/3/4: edge-case + policy tests. All fake-based (real USN/MFT
// needs elevated NTFS; on-box validation is manual, not CI).
public sealed class CapturePolicyTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public CapturePolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_policy_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeLayout : ILayoutFingerprintProvider
    {
        private readonly string? _fp;
        public FakeLayout(string? fp) => _fp = fp;
        public string? LayoutFingerprint() => _fp;
    }

    private sealed class FakeExcl : IExclusionResolver
    {
        private readonly IReadOnlyList<(long StartLcn, long ClusterCount)> _runs;
        public FakeExcl(IReadOnlyList<string> names, IReadOnlyList<(long StartLcn, long ClusterCount)> runs, long bpc = 4096)
        {
            ExclusionNames = names;
            _runs = runs;
            BytesPerCluster = bpc;
        }
        public IReadOnlyList<string> ExclusionNames { get; }
        public long BytesPerCluster { get; }
        public IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns() => _runs;
    }

    private Store NewStore(TestLogger? log = null) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger());

    private SnapshotManifest LoadGen(int gen)
    {
        var manDir = Path.Combine(_storeRoot, "manifests");
        var paths = Directory.GetFiles(manDir, $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    private static FakeChangeSource Dirty(params long[] chunks) =>
        new((_, _, _) => new IncrementalPlan(new HashSet<long>(chunks), "fake:1"));

    private static byte[] RandomChunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void WriteChunks(string path, params byte[][] chunks)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
    }

    private static void StampLive(byte[] record, ushort seq, bool inUse)
    {
        record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
        BitConverter.GetBytes(seq).CopyTo(record, 0x10);
        BitConverter.GetBytes((ushort)48).CopyTo(record, 0x14);
        BitConverter.GetBytes((ushort)(inUse ? 1 : 0)).CopyTo(record, 0x16);
    }

    private static PartitionImageInput Part(int number, string uniqueGuid, string file, string filesystem,
        IBlockSource? source, IChangeSource? change)
    {
        var lengthBytes = new FileInfo(file).Length;
        var firstLba = 2048L + ((number - 1) * 8192L);
        return new PartitionImageInput(number, $"part{number}", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
            uniqueGuid, firstLba, firstLba + (lengthBytes / 512) - 1, firstLba * 512, lengthBytes,
            filesystem, false, null, source, change);
    }

    // Item 2: resident-only file → no data extents, just its record cluster.
    [Fact]
    public void Mft_ResidentOnly_YieldsRecordClusterOnly()
    {
        var record = new byte[1024];
        StampLive(record, 7, true);
        BitConverter.GetBytes(80u).CopyTo(record, 0x18);
        BitConverter.GetBytes(0x80u).CopyTo(record, 48);
        BitConverter.GetBytes(24u).CopyTo(record, 52);
        record[56] = 0; // resident $DATA
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, 72);
        var fileRef = (7UL << 48) | 5UL;
        var mftRuns = new List<(long, long)> { (1000, 10) };

        var ext = MftExtentReader.ParseRecordExtents(record, fileRef, 4096, 1024, mftRuns);

        Assert.NotNull(ext);
        Assert.Equal([(1001L, 1L)], ext); // record 5 @byte 5120 → VCN 1 → LCN 1001
    }

    // Item 2: recycled file IDs (sequence mismatch, deleted, zeroed slot) → skip.
    [Fact]
    public void Mft_FrnReuse_ReturnsNull()
    {
        static byte[] LiveRecord()
        {
            var record = new byte[1024];
            StampLive(record, 7, true);
            BitConverter.GetBytes(144u).CopyTo(record, 0x18);
            BitConverter.GetBytes(0x80u).CopyTo(record, 48);
            BitConverter.GetBytes(64u).CopyTo(record, 52);
            record[56] = 1;
            BitConverter.GetBytes((ushort)48).CopyTo(record, 80);
            record[96] = 0x21; record[97] = 0x10; record[98] = 0x20; record[99] = 0x00;
            record[100] = 0x00;
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, 112);
            return record;
        }
        var mftRuns = new List<(long, long)> { (1000, 10) };

        Assert.NotNull(MftExtentReader.ParseRecordExtents(LiveRecord(), (7UL << 48) | 5UL, 4096, 1024, mftRuns));
        Assert.Null(MftExtentReader.ParseRecordExtents(LiveRecord(), (9UL << 48) | 5UL, 4096, 1024, mftRuns)); // seq mismatch
        var deleted = LiveRecord();
        BitConverter.GetBytes((ushort)0).CopyTo(deleted, 0x16);
        Assert.Null(MftExtentReader.ParseRecordExtents(deleted, (7UL << 48) | 5UL, 4096, 1024, mftRuns)); // deleted
        var zeroed = LiveRecord();
        zeroed[0] = zeroed[1] = zeroed[2] = zeroed[3] = 0;
        Assert.Null(MftExtentReader.ParseRecordExtents(zeroed, (7UL << 48) | 5UL, 4096, 1024, mftRuns)); // reused slot
    }

    // Item 2: every $DATA stream counts — default + named ADS (name fields set
    // to prove the parser is name-insensitive by construction).
    [Fact]
    public void Mft_NamedAds_CapturedWithDefaultStream()
    {
        var record = new byte[1024];
        StampLive(record, 7, true);
        BitConverter.GetBytes(184u).CopyTo(record, 0x18);
        BitConverter.GetBytes(0x80u).CopyTo(record, 48);
        BitConverter.GetBytes(64u).CopyTo(record, 52);
        record[56] = 1;
        BitConverter.GetBytes((ushort)48).CopyTo(record, 80);
        record[96] = 0x21; record[97] = 0x10; record[98] = 0x20; record[99] = 0x00;
        record[100] = 0x00;
        BitConverter.GetBytes(0x80u).CopyTo(record, 112);
        BitConverter.GetBytes(64u).CopyTo(record, 116);
        record[120] = 1;
        record[121] = 5; // name length (chars): a NAMED stream — still captured
        BitConverter.GetBytes((ushort)48).CopyTo(record, 144);
        record[160] = 0x21; record[161] = 0x08; record[162] = 0x40; record[163] = 0x00;
        record[164] = 0x00;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, 176);
        var mftRuns = new List<(long, long)> { (1000, 10) };

        var ext = MftExtentReader.ParseRecordExtents(record, (7UL << 48) | 5UL, 4096, 1024, mftRuns);

        Assert.NotNull(ext);
        Assert.Equal([(32L, 16L), (64L, 8L), (1001L, 1L)], ext);
    }

    // Item 2: sparse markers — MFT offBytes==0 runs and retrieval-pointers
    // LCN -1 — dirty nothing, never a negative chunk index.
    [Fact]
    public void SparseMarkers_DirtyNothing()
    {
        var decoded = MftExtentReader.DecodeDataRuns(new byte[] { 0x01, 0x04, 0x11, 0x02, 0x0A, 0x00 });
        Assert.Equal([(10L, 2L)], decoded); // sparse 4-cluster run skipped
        Assert.Empty(CapturePolicy.DirtyChunksForRuns(new (long, long)[] { (-1, 5) }, 4096, Mib));
        Assert.Equal([0L], CapturePolicy.DirtyChunksForRuns(new (long, long)[] { (-1, 5), (0, 256) }, 4096, Mib));
    }

    // Item 1-minimal: fingerprint mismatch (defrag moved data, no USN) → full.
    [Fact]
    public void Relocation_Mismatch_FallsBackToFull()
    {
        var vol = Path.Combine(_dir, "reloc.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var s = new FileBlockSource(vol))
            store.Image(s, "parent", new ImageOptions(Layout: new FakeLayout("aaa")));
        Assert.Equal("aaa", LoadGen(1).LayoutFingerprint);

        using (var s = new FileBlockSource(vol))
        {
            var m = store.Image(s, "child",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0), Layout: new FakeLayout("bbb")));
            Assert.Equal(0, m.ParentGeneration);
        }
        Assert.Contains(log.Messages, m => m.Contains("fingerprint"));
        Assert.Equal("bbb", LoadGen(2).LayoutFingerprint);
        Assert.True(store.Verify(2));
    }

    // Item 1-minimal: matching fingerprint → incremental proceeds; the hash is
    // stable for identical runs and moves when runs shift.
    [Fact]
    public void Relocation_Match_AllowsIncremental()
    {
        Assert.Equal(
            CapturePolicy.ComputeLayoutFingerprint(new (long, long)[] { (1, 2), (9, 9) }),
            CapturePolicy.ComputeLayoutFingerprint(new (long, long)[] { (1, 2), (9, 9) }));
        Assert.NotEqual(
            CapturePolicy.ComputeLayoutFingerprint(new (long, long)[] { (1, 2) }),
            CapturePolicy.ComputeLayoutFingerprint(new (long, long)[] { (1, 3) }));

        var vol = Path.Combine(_dir, "reloc2.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2));
        using var store = NewStore();
        using (var s = new FileBlockSource(vol))
            store.Image(s, "parent", new ImageOptions(Layout: new FakeLayout("aaa")));
        using (var s = new FileBlockSource(vol))
        {
            var m = store.Image(s, "child",
                new ImageOptions(Incremental: true, ChangeSource: Dirty(0), Layout: new FakeLayout("aaa")));
            Assert.Equal(1, m.ParentGeneration);
            Assert.Equal("aaa", m.LayoutFingerprint);
        }
    }

    // Item 4: excluded extents zeroed in place (partial chunks keep neighbor
    // bytes), names recorded in the manifest, loud warn.
    [Fact]
    public void Exclusions_ZeroedAndRecorded()
    {
        var vol = Path.Combine(_dir, "excl.img");
        WriteChunks(vol, RandomChunk(42), RandomChunk(43));
        var volBytes = File.ReadAllBytes(vol);
        Assert.Contains(volBytes.AsSpan(0, 32 * 1024).ToArray(), b => b != 0); // fixture is really nonzero
        var log = new TestLogger();
        using var store = NewStore(log);
        var excl = new FakeExcl(["pagefile.sys"], [(0, 8)]); // first 32 KiB
        using (var s = new FileBlockSource(vol))
            store.Image(s, "excl", new ImageOptions(Exclusions: excl));

        var m = LoadGen(1);
        Assert.Equal(["pagefile.sys"], m.SkippedExclusions);
        Assert.Contains(log.Messages, msg => msg.Contains("pagefile.sys"));
        var outPath = Path.Combine(_dir, "excl-out.img");
        store.Export(1, outPath);
        var back = File.ReadAllBytes(outPath);
        Assert.All(back.AsSpan(0, 32 * 1024).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(volBytes.AsSpan(32 * 1024).ToArray(), back.AsSpan(32 * 1024).ToArray());
        Assert.True(store.Verify(1));
    }

    // Item 4: $LogFile stays captured (stripped even when listed); null = defaults.
    [Fact]
    public void Exclusions_LogFileNeverExcluded()
    {
        Assert.DoesNotContain(CapturePolicy.EffectiveSkip(null), e => e.Contains("LogFile"));
        Assert.Contains("pagefile.sys", CapturePolicy.EffectiveSkip(null));
        var effective = CapturePolicy.EffectiveSkip(["$LogFile", "pagefile.sys", "X:\\$LogFile"]);
        Assert.Equal(["pagefile.sys"], effective);
    }

    // Item 4: exclusion-set change → full (clean chunks carry old-set bytes).
    [Fact]
    public void Exclusions_SetChange_ForcesFull()
    {
        var vol = Path.Combine(_dir, "excl2.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2));
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var s = new FileBlockSource(vol))
            store.Image(s, "parent", new ImageOptions(Exclusions: new FakeExcl(["pagefile.sys"], [(0, 1)])));
        using (var s = new FileBlockSource(vol))
        {
            var m = store.Image(s, "child", new ImageOptions(Incremental: true, ChangeSource: Dirty(0),
                Exclusions: new FakeExcl(["pagefile.sys", "swapfile.sys"], [(0, 1)])));
            Assert.Equal(0, m.ParentGeneration);
        }
        Assert.Contains(log.Messages, msg => msg.Contains("exclusion set"));
    }

    // Item 3: per-type policy — OneDrive skip+loud, WOF/EFS/dedup capture, ReFS full-only.
    [Fact]
    public void FsPolicy_PerTypeDecisions()
    {
        Assert.Equal(CaptureDecision.SkipLoud, FsTypePolicy.DecideOneDrive(FsTypePolicy.RecallOnDataAccess));
        Assert.Equal(CaptureDecision.SkipLoud, FsTypePolicy.DecideOneDrive(FsTypePolicy.RecallOnOpen));
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideOneDrive(0x20)); // plain archive bit
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideWof());
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideEfs());
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideDedupStore());
        Assert.Equal(CaptureDecision.FullOnlyLoud, FsTypePolicy.DecideFilesystem("refs"));
        Assert.Equal(CaptureDecision.FullOnlyLoud, FsTypePolicy.DecideFilesystem("ReFS"));
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideFilesystem("ntfs"));
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideFilesystem("fat32"));
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideFilesystem("unknown"));
        Assert.Equal(CaptureDecision.Capture, FsTypePolicy.DecideFilesystem(null));
    }

    // Item 3: ReFS partition refuses its incremental parent (loud) → full.
    [Fact]
    public void Refs_ForcesFullInImageDisk()
    {
        var vol = Path.Combine(_dir, "refs.img");
        WriteChunks(vol, RandomChunk(1), RandomChunk(2));
        var guid = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var log = new TestLogger();
        using var store = NewStore(log);
        var clean = new FakeChangeSource((_, _, _) => new IncrementalPlan([], "fake:2"));
        using (var s1 = new FileBlockSource(vol))
            store.ImageDisk(0, "disk0", new DiskImageOptions(),
                new DiskCapturePlan("disk-guid", 1L << 40, 512, [Part(1, guid, vol, "refs", s1, null)]));
        using (var s2 = new FileBlockSource(vol))
        {
            var layout2 = store.ImageDisk(0, "disk0", new DiskImageOptions(Incremental: true),
                new DiskCapturePlan("disk-guid", 1L << 40, 512, [Part(1, guid, vol, "refs", s2, clean)]));
            var child = LoadGen(layout2.Partitions[0].SnapshotGeneration);
            Assert.Equal(0, child.ParentGeneration);
        }
        Assert.Contains(log.Messages, msg => msg.Contains("ReFS"));
    }

    // Item 4: yaml override replaces defaults; absent = defaults.
    [Fact]
    public void Config_Exclusions_YamlOverride()
    {
        var custom = Path.Combine(_dir, "custom.yaml");
        File.WriteAllText(custom, "store_path: /data/store\nexclusions:\n  skip: [custom.sys]\n");
        var cfg = CorpusConfig.Load(custom);
        Assert.NotNull(cfg);
        Assert.Equal(["custom.sys"], cfg!.Exclusions.Skip);
        Assert.Equal(["custom.sys"], CapturePolicy.EffectiveSkip(cfg.Exclusions.Skip));
        Assert.True(CorpusConfig.Validate(cfg, new TestLogger()));

        var minimal = Path.Combine(_dir, "minimal.yaml");
        File.WriteAllText(minimal, "store_path: /data/store\n");
        var cfg2 = CorpusConfig.Load(minimal);
        Assert.NotNull(cfg2);
        Assert.Null(cfg2!.Exclusions.Skip);
        Assert.Equal(CapturePolicy.DefaultExclusions, CapturePolicy.EffectiveSkip(cfg2.Exclusions.Skip));
    }
}
