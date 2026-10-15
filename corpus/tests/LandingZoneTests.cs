namespace Corpus;

// Landing zone (item 16): fast-capture default path + idle optimize +
// scheduled GC cadence. Manual --gc untouched (see GcTests).
public sealed class LandingZoneTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public LandingZoneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_lz_" + Guid.NewGuid());
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

    private string SeedVolume(string name, params byte[][] chunks)
    {
        var vol = Path.Combine(_dir, name);
        using var f = new FileStream(vol, FileMode.Create, FileAccess.Write, FileShare.Read);
        foreach (var c in chunks)
            f.Write(c, 0, c.Length);
        return vol;
    }

    private int PackCount() =>
        Directory.Exists(Path.Combine(_storeRoot, "packs"))
            ? Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").Length : 0;

    private long IndexCount()
    {
        using var idx = new ChunkIndex(_dbPath);
        return idx.Count();
    }

    [Fact]
    public void FastCaptureThenOptimizeRoundTrips()
    {
        var vol = SeedVolume("vol.img", RandomChunk(11), RandomChunk(12));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
        {
            var m = store.ImageFast(source, "fast1");
            Assert.Equal(1, m.Generation);
            Assert.Equal(2, store.PendingLandingCount());
        }
        Assert.Equal(0, PackCount());
        Assert.Equal(0, IndexCount());
        using (var store = NewStore())
        {
            var (packed, deduped, dropped) = store.OptimizeLanding();
            Assert.Equal(2, packed);
            Assert.Equal(0, deduped);
            Assert.Equal(0, dropped);
            Assert.Equal(0, store.PendingLandingCount());
        }
        Assert.Equal(1, PackCount());
        Assert.Equal(2, IndexCount());
        using (var store = NewStore())
        {
            Assert.True(store.Verify(1));
            var outPath = Path.Combine(_dir, "rt.img");
            store.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void ExportReadsThroughLandingBeforeOptimize()
    {
        var vol = SeedVolume("vol.img", RandomChunk(21), RandomChunk(22));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            store.ImageFast(source, "fast1");
        using (var store = NewStore())
        {
            Assert.True(store.Verify(1));
            var outPath = Path.Combine(_dir, "pre.img");
            store.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void OptimizeDedupsAgainstMainStore()
    {
        var vol = SeedVolume("vol.img", RandomChunk(31), RandomChunk(32));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            store.Image(source, "classic1");
        Assert.Equal(1, PackCount());
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            store.ImageFast(source, "fast2");
        using (var store = NewStore())
        {
            var (packed, deduped, dropped) = store.OptimizeLanding();
            Assert.Equal(0, packed);
            Assert.Equal(2, deduped);
            Assert.Equal(0, dropped);
        }
        Assert.Equal(1, PackCount());
        using (var store = NewStore())
        {
            Assert.True(store.Verify(1));
            Assert.True(store.Verify(2));
        }
    }

    [Fact]
    public void OptimizeDropsUnreferencedLanding()
    {
        var v1 = SeedVolume("v1.img", RandomChunk(41), RandomChunk(42));
        var v2 = SeedVolume("v2.img", RandomChunk(43), RandomChunk(44));
        using (var store = NewStore())
        {
            using (var s1 = new FileBlockSource(v1)) store.ImageFast(s1, "fast1");
            using (var s2 = new FileBlockSource(v2)) store.ImageFast(s2, "fast2");
            Assert.Equal(4, store.PendingLandingCount());
        }
        var retention = new CorpusRetentionConfig { KeepLast = 1 };
        var gc = new GarbageCollector(_storeRoot, _dbPath, retention, new TestLogger());
        gc.Execute(gc.Plan());
        var live = new GarbageCollector(_storeRoot, _dbPath, retention, new TestLogger()).LiveDigestHex();
        using (var store = NewStore())
        {
            var (packed, deduped, dropped) = store.OptimizeLanding(live);
            Assert.Equal(2, packed);
            Assert.Equal(0, deduped);
            Assert.Equal(2, dropped);
            Assert.Equal(0, store.PendingLandingCount());
        }
        using (var store = NewStore())
        {
            Assert.True(store.Verify(2));
            var outPath = Path.Combine(_dir, "kept.img");
            store.Export(2, outPath);
            Assert.Equal(File.ReadAllBytes(v2), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void ScheduledCadencePureChecks()
    {
        var day = new DateTime(2026, 10, 15);
        Assert.False(GcScheduler.IsDueDaily(day.AddHours(3), day, new TimeSpan(2, 0, 0)));
        Assert.True(GcScheduler.IsDueDaily(day.AddHours(3), day.AddDays(-1), new TimeSpan(2, 0, 0)));
        Assert.False(GcScheduler.IsDueDaily(day.AddHours(1), day.AddDays(-1), new TimeSpan(2, 0, 0)));
        Assert.True(GcScheduler.IsDueDaily(day.AddHours(1), day.AddDays(-3), new TimeSpan(2, 0, 0)));
        Assert.True(GcScheduler.IsDueInterval(day, DateTime.MinValue, TimeSpan.FromMinutes(15)));
        Assert.True(GcScheduler.IsDueInterval(day.AddMinutes(16), day, TimeSpan.FromMinutes(15)));
        Assert.False(GcScheduler.IsDueInterval(day.AddMinutes(5), day, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void SchedulerTickRunsOptimizeAndGc()
    {
        var v1 = SeedVolume("v1.img", RandomChunk(51), RandomChunk(52));
        var v2 = SeedVolume("v2.img", RandomChunk(53), RandomChunk(54));
        using (var store = NewStore())
        {
            using (var s1 = new FileBlockSource(v1)) store.ImageFast(s1, "fast1");
            using (var s2 = new FileBlockSource(v2)) store.ImageFast(s2, "fast2");
        }
        var stateDir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(stateDir);
        var now = DateTime.Now;
        File.WriteAllText(Path.Combine(stateDir, "gc-last-run"), now.AddDays(-2).ToString("o"));
        File.WriteAllText(Path.Combine(stateDir, "optimize-last-run"), now.AddHours(-1).ToString("o"));
        using (var sched = new GcScheduler(_storeRoot, _dbPath,
            new CorpusRetentionConfig { KeepLast = 1 }, new TestLogger(), 1, 3,
            new GcSchedule(new TimeSpan(0, 0, 0), TimeSpan.FromMinutes(15))))
        {
            sched.Tick(now);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000001-*.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "000002-*.json"));
        using (var store = NewStore())
        {
            Assert.Equal(0, store.PendingLandingCount());
            Assert.True(store.Verify(2));
        }
    }

    [Fact]
    public void LandingRoundTripsPlaintext()
    {
        var vol = SeedVolume("vol.img", RandomChunk(61), RandomChunk(62));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
            store.ImageFast(source, "gen1");
        using (var store = NewStore())
        {
            Assert.True(store.Verify(1));
            var (packed, _, _) = store.OptimizeLanding();
            Assert.Equal(2, packed);
        }
        using (var store = NewStore())
        {
            Assert.True(store.Verify(1));
            var outPath = Path.Combine(_dir, "enc.img");
            store.Export(1, outPath);
            Assert.Equal(File.ReadAllBytes(vol), File.ReadAllBytes(outPath));
        }
    }

    [Fact]
    public void FastCaptureRejectsIncremental()
    {
        var vol = SeedVolume("vol.img", RandomChunk(71));
        using (var store = NewStore())
        using (var source = new FileBlockSource(vol))
        {
            Assert.Throws<InvalidOperationException>(() =>
                store.ImageFast(source, "x", new ImageOptions(Incremental: true)));
            Assert.Throws<InvalidOperationException>(() =>
                store.ImageFast(source, "x", new ImageOptions(ParentGeneration: 1)));
        }
    }

    [Fact]
    public void OptimizeCleansLandingTmps()
    {
        Directory.CreateDirectory(Path.Combine(_storeRoot, "landing"));
        File.WriteAllBytes(Path.Combine(_storeRoot, "landing", "junk.pack.tmp"), new byte[] { 1 });
        using (var store = NewStore())
        {
            var (packed, deduped, dropped) = store.OptimizeLanding();
            Assert.Equal((0, 0, 0), (packed, deduped, dropped));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(_storeRoot, "landing")));
    }
}
