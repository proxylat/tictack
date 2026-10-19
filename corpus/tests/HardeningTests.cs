namespace Corpus;

// Ransomware hardening (fixes gap 4): WORM gate refuses destructive GC loud
// with data intact but lets metadata-only plans through; the change alarm
// fires fail-closed on synthetic high churn, stays silent under threshold,
// and both knobs off preserve current behavior.
public sealed class HardeningTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public HardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_hard_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Store NewStore(TestLogger? log = null, double alarm = 0) =>
        new(_storeRoot, _dbPath, 1, 3, log ?? new TestLogger(), changeAlarmPercent: alarm);

    private GarbageCollector NewGc(CorpusRetentionConfig retention, TestLogger log, bool worm = false) =>
        new(_storeRoot, _dbPath, retention, log, worm);

    private static CorpusRetentionConfig Keep(int last) =>
        new() { KeepLast = last };

    private static byte[] Chunk(int seed)
    {
        var b = new byte[Mib];
        new Random(seed).NextBytes(b);
        return b;
    }

    private string WriteVol(string name, params int[] seeds)
    {
        var path = Path.Combine(_dir, name);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        foreach (var s in seeds)
        {
            var c = Chunk(s);
            f.Write(c, 0, c.Length);
        }
        return path;
    }

    private void Image(string vol, string name, double alarm = 0, ImageOptions? opts = null)
    {
        using var store = NewStore(alarm: alarm);
        using var source = new FileBlockSource(vol);
        if (opts == null)
            store.Image(source, name);
        else
            store.Image(source, name, opts);
    }

    private int ManifestCount() =>
        Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").Length;

    private SnapshotManifest LoadGen(int gen)
    {
        var paths = Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), $"{gen:D6}-*.json");
        Assert.Single(paths);
        return ManifestStore.Load(paths[0], ChunkId.EnsureKey(_storeRoot));
    }

    [Fact]
    public void WormMode_BlocksPrune_Loud_DataIntact()
    {
        var vol1 = WriteVol("v1.img", 11, 12);
        var vol2 = WriteVol("v2.img", 13, 14);
        var vol3 = WriteVol("v3.img", 15, 16);
        Image(vol1, "g1");
        Image(vol2, "g2");
        Image(vol3, "g3");
        var log = new TestLogger();
        var plan = NewGc(Keep(1), log).Plan();
        Assert.Equal(2, plan.DropManifests.Count);

        var ex = Assert.Throws<WormBlockedException>(() => NewGc(Keep(1), log, worm: true).Execute(plan));
        Assert.Contains("worm mode", ex.Message);
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:", StringComparison.Ordinal) && m.Contains("worm mode"));
        // Data intact: dropped gens still load, drop packs still on disk.
        Assert.Equal(2, LoadGen(1).Chunks.Count);
        Assert.Equal(2, LoadGen(2).Chunks.Count);
        Assert.Equal(3, ManifestCount());
        foreach (var p in plan.DropPacks)
            Assert.True(File.Exists(p), "worm block must not delete " + p);
    }

    [Fact]
    public void WormMode_AllowsMetadataOnly()
    {
        Image(WriteVol("v1.img", 21, 22), "g1");
        Image(WriteVol("v2.img", 23, 24), "g2");
        var log = new TestLogger();
        var plan = NewGc(Keep(4), log, worm: true).Plan();
        Assert.Empty(plan.DropManifests);
        Assert.Empty(plan.DropPacks);
        NewGc(Keep(4), log, worm: true).Execute(plan);
        Assert.Equal(2, ManifestCount());
    }

    [Fact]
    public void WormMode_Off_ExecutesDrops()
    {
        Image(WriteVol("v1.img", 31, 32), "g1");
        Image(WriteVol("v2.img", 33, 34), "g2");
        var log = new TestLogger();
        var plan = NewGc(Keep(1), log).Plan();
        Assert.Single(plan.DropManifests);
        NewGc(Keep(1), log).Execute(plan);
        Assert.Equal(1, ManifestCount());
    }

    [Fact]
    public void ChangeAlarm_Fires_OnHighChurn()
    {
        Image(WriteVol("v1.img", 41, 42, 43, 44), "g1", alarm: 50);
        var log = new TestLogger();
        using (var store = NewStore(log, alarm: 50))
        using (var source = new FileBlockSource(WriteVol("v2.img", 45, 46, 47, 48)))
        {
            var ex = Assert.Throws<ChangeAlarmException>(() => store.Image(source, "g2"));
            Assert.Contains("change alarm", ex.Message);
        }
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:", StringComparison.Ordinal) && m.Contains("change alarm"));
        // Commit refused: the gen stays free.
        Assert.Equal(1, ManifestCount());
    }

    [Fact]
    public void ChangeAlarm_Silent_UnderThreshold()
    {
        Image(WriteVol("v1.img", 51, 52, 53, 54), "g1", alarm: 50);
        var log = new TestLogger();
        using (var store = NewStore(log, alarm: 50))
        using (var source = new FileBlockSource(WriteVol("v2.img", 51, 52, 53, 99)))
        {
            var m = store.Image(source, "g2");
            Assert.Equal(2, m.Generation);
        }
        Assert.DoesNotContain(log.Messages, m => m.Contains("change alarm"));
        Assert.Equal(2, ManifestCount());
    }

    [Fact]
    public void ChangeAlarm_Disabled_AllowsAnything()
    {
        Image(WriteVol("v1.img", 61, 62), "g1");
        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(WriteVol("v2.img", 63, 64)))
        {
            var m = store.Image(source, "g2");
            Assert.Equal(2, m.Generation);
        }
        Assert.Equal(2, ManifestCount());
    }

    [Fact]
    public void ChangeAlarm_Force_Overrides()
    {
        Image(WriteVol("v1.img", 71, 72), "g1");
        var parent = LoadGen(1);
        var changed = new List<ManifestEntry>
        {
            new("ff", 10),
            new("00", 20),
        };
        var log = new TestLogger();
        // Fail-closed by default ...
        Assert.Throws<ChangeAlarmException>(() =>
            Hardening.CheckChangeAlarm(parent, changed, 50, force: false, log));
        // ... unless forced, which warns and proceeds.
        Hardening.CheckChangeAlarm(parent, changed, 50, force: true, log);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:", StringComparison.Ordinal) && m.Contains("change alarm"));
    }

    [Fact]
    public void ChurnFraction_Math()
    {
        var a = new List<ManifestEntry> { new("aa", 1), new("bb", 2) };
        Assert.Equal(0, Hardening.ChurnFraction(a, new List<ManifestEntry> { new("aa", 1), new("bb", 2) }));
        Assert.Equal(1, Hardening.ChurnFraction(a, new List<ManifestEntry> { new("cc", 1), new("dd", 2) }));
        Assert.Equal(0.5, Hardening.ChurnFraction(a, new List<ManifestEntry> { new("aa", 1) }));
        Assert.Equal(0, Hardening.ChurnFraction(new List<ManifestEntry>(), new List<ManifestEntry>()));
        // Threshold 0 disables: full churn passes without a throw.
        Hardening.CheckChangeAlarm(null, a, 0, force: false, new TestLogger());
    }
}
