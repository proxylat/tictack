namespace Corpus;

// GcHost: the blocking service lifetime around GcScheduler. Proves a
// scheduled GC run actually fires through the hosted (timer) path —
// Start() ticks immediately, so a stale stamp collects without sleeps.
public sealed class GcHostTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public GcHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_gch_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private static bool WaitFor(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    private static string SafeReadStamp(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    [Fact]
    public async Task HostedRunFiresScheduledGc()
    {
        var v1 = SeedVolume("v1.img", RandomChunk(81), RandomChunk(82));
        var v2 = SeedVolume("v2.img", RandomChunk(83), RandomChunk(84));
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        {
            using (var s1 = new FileBlockSource(v1)) store.ImageFast(s1, "fast1");
            using (var s2 = new FileBlockSource(v2)) store.ImageFast(s2, "fast2");
        }
        var stateDir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(stateDir);
        var now = DateTime.Now;
        File.WriteAllText(Path.Combine(stateDir, "gc-last-run"), now.AddDays(-2).ToString("o"));
        var optStamp = Path.Combine(stateDir, "optimize-last-run");
        File.WriteAllText(optStamp, now.AddHours(-1).ToString("o"));
        var seededOpt = SafeReadStamp(optStamp);

        var manifests = Path.Combine(_storeRoot, "manifests");
        var landing = Path.Combine(_storeRoot, "landing");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var host = new GcHost(_storeRoot, _dbPath,
            new CorpusRetentionConfig { KeepLast = 1 }, new TestLogger(), 1, 3,
            new GcSchedule(new TimeSpan(0, 0, 0), TimeSpan.FromMinutes(15))))
        {
            var run = Task.Run(() => host.Run(cts.Token));
            // One tick does GC then optimize: wait for the END of the full
            // pass (optimize stamp rewrite), not just intermediate
            // end-states. Manifest-drop + landing-empty can both be true
            // while Execute is still dropping packs; verifying then races
            // the collector and fails on slow CI disks.
            var fired = WaitFor(() =>
                Directory.GetFiles(manifests, "000001-*.json").Length == 0
                && (!Directory.Exists(landing) || Directory.GetFiles(landing).Length == 0)
                && SafeReadStamp(optStamp) != seededOpt,
                TimeSpan.FromSeconds(25));
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fired, "hosted scheduler never finished one full GC+optimize pass");
        }
        Assert.Single(Directory.GetFiles(manifests, "000002-*.json"));
        using (var store = new Store(_storeRoot, _dbPath, 1, 3, new TestLogger()))
        {
            Assert.Equal(0, store.PendingLandingCount());
            Assert.True(store.Verify(2));
        }
    }

    [Fact]
    public void RunReturnsPromptlyWhenAlreadyCancelled()
    {
        using var host = new GcHost(_storeRoot, _dbPath,
            new CorpusRetentionConfig { KeepLast = 1 }, new TestLogger(), 1, 3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        host.Run(cts.Token); // would hang the test run if it ignored the token
    }

    [Fact]
    public void StartStopDisposeAreIdempotent()
    {
        using var host = new GcHost(_storeRoot, _dbPath,
            new CorpusRetentionConfig { KeepLast = 1 }, new TestLogger(), 1, 3);
        host.Start();
        host.Start();
        host.Stop();
        host.Stop();
        host.Dispose();
        host.Stop(); // finally-block safe after Dispose
        Assert.Throws<ObjectDisposedException>(() => host.Start());
        Assert.Throws<ObjectDisposedException>(() => host.Run(CancellationToken.None));
    }
}
