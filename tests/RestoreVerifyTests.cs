namespace TicTack;

public class RestoreVerifyTests : IDisposable
{
    private readonly string _root;
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly EventMonitor _monitor = new();
    private readonly RecordingAction _copy;
    private readonly RecordingAction _rename;
    private readonly RecordingValidator _validator;
    private readonly RecordingDeletion _deletion;
    private readonly RecordingLogger _log = new();

    public RestoreVerifyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tictack-restore-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_root, "src");
        _dstDir = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
        _copy = new RecordingAction(new CopyAction(new FileAccessor()));
        _rename = new RecordingAction(new RenameAction());
        _validator = new RecordingValidator(new SizeValidator());
        _deletion = new RecordingDeletion(new MirrorDeletion());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SyncPipeline StartPipeline(string dbName, Action<SourceConfig>? tweak)
    {
        var cfg = new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 };
        // Writable cadence dir: the default /var/lib/tictack is not
        // writable by the test user, and without verify.db the restore
        // pass (like full-verify) silently disables itself.
        cfg.StateDbPath = Path.Combine(_root, "state");
        tweak?.Invoke(cfg);
        var db = new StateDb(Path.Combine(_root, dbName));
        var p = new SyncPipeline(cfg, _monitor, new SizeComparer(), _copy, _rename,
            new ExponentialBackoffRetry(1, 0, 1), _validator, new NoVersioning(),
            _deletion, _log, db);
        p.Start();
        WaitFor(() => _log.Messages.Count(m => m.Contains("Sync complete")) >= 1, "initial sync");
        return p;
    }

    // Call after _log.Messages.Clear(): waits for the next completion.
    private void WaitForSecondSync() =>
        WaitFor(() => _log.Messages.Count(m => m.Contains("Sync complete")) >= 1, "rescan");

    private static void WaitFor(Func<bool> cond, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    // Fresh verify.db checks shard 0: pick a name the scrub skips so the
    // tamper survives to the restore pass deterministically.
    private static string NameOutsideShardZero()
    {
        for (int i = 0; i < 1000; i++)
        {
            var name = "restore-" + i + ".txt";
            if (Scrubber.ShardOf(name) != 0) return name;
        }
        throw new InvalidOperationException("no off-shard name found");
    }

    private static void TweakVerify(SourceConfig cfg)
    {
        cfg.Sync = new SyncConfig { FullRoutineCheckDays = 0, RoutineCheckDays = 1, RoutineCheckFiles = 10 };
    }

    [Fact]
    public async Task DisabledByDefault_RunsNoRestorePass()
    {
        using var pipeline = StartPipeline("restore-off.db", null);
        var a = Path.Combine(_srcDir, "a.txt");
        File.WriteAllText(a, "off-by-default");
        _monitor.Fire(ChangeType.Created, a);
        WaitFor(() => File.Exists(Path.Combine(_dstDir, "a.txt")), "dest copy");
        _log.Messages.Clear();
        await pipeline.RequestRescanAsync();
        WaitForSecondSync();
        Assert.DoesNotContain(_log.Messages, m => m.Contains("Routine check"));
    }

    [Fact]
    public async Task Mismatch_AlertsAndLeavesDestinationAlone()
    {
        var name = NameOutsideShardZero();
        File.WriteAllText(Path.Combine(_srcDir, name), "pristine-12345");
        using var pipeline = StartPipeline("restore-on.db", TweakVerify);
        WaitFor(() => File.ReadAllText(Path.Combine(_dstDir, name)) == "pristine-12345", "initial copy");

        // Same-size content tamper on the destination only. SizeComparer
        // sees no change (FullRoutineCheckDays=0), scrub skips the shard.
        // Align mtimes so the scan skips it: only restore must notice.
        // The initial sync already consumed the fresh-store due, so
        // backdate the stamp to force the rescan's restore pass.
        var dstTampered = Path.Combine(_dstDir, name);
        File.WriteAllText(dstTampered, "tampered-12345");
        File.SetLastWriteTimeUtc(dstTampered, File.GetLastWriteTimeUtc(Path.Combine(_srcDir, name)));
        pipeline.StampRestoreVerify(DateTime.UtcNow.AddDays(-2));
        _log.Messages.Clear();
        await pipeline.RequestRescanAsync();
        WaitForSecondSync();

        Assert.Contains(_log.Messages, m => m.Contains("Routine check FAILED") && m.Contains(name));
        // Read-only proof: the tampered file is reported, not repaired.
        Assert.Equal("tampered-12345", File.ReadAllText(Path.Combine(_dstDir, name)));
    }

    [Fact]
    public void Match_LogsSummary()
    {
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), "clean-copy");
        using var pipeline = StartPipeline("restore-ok.db", TweakVerify);
        WaitFor(() => File.Exists(Path.Combine(_dstDir, "b.txt")), "dest copy");

        // Fresh store: the initial sync's own restore pass is due and runs.
        Assert.Contains(_log.Messages, m => m.Contains("Routine check:") && m.Contains("match"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("Routine check FAILED"));
    }
}
