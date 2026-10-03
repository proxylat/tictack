namespace TicTack;

public class MoveReplayTests : IDisposable
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

    public MoveReplayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tictack-mover-" + Guid.NewGuid().ToString("N"));
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

    private SyncPipeline StartDbPipeline(string dbName, Action<SourceConfig>? tweak, out StateDb db)
    {
        var cfg = new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 };
        tweak?.Invoke(cfg);
        db = new StateDb(Path.Combine(_root, dbName));
        var p = new SyncPipeline(cfg, _monitor, new SizeComparer(), _copy, _rename,
            new ExponentialBackoffRetry(1, 0, 1), _validator, new NoVersioning(),
            _deletion, _log, db);
        p.Start();
        WaitFor(() => _log.Messages.Count(m => m.Contains("Sync complete")) >= 1, "initial sync");
        return p;
    }

    private static void WaitFor(Func<bool> cond, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task ScanMoveReplay_MovesInsteadOfCopying()
    {
        using var pipeline = StartDbPipeline("move-state.db", null, out var db);
        var a = Path.Combine(_srcDir, "a.txt");
        File.WriteAllText(a, "move-me");
        _monitor.Fire(ChangeType.Created, a);
        WaitFor(() => db.LoadAll().ContainsKey("a.txt"), "state update");
        WaitFor(() => File.Exists(Path.Combine(_dstDir, "a.txt")), "dest copy");

        _copy.Calls.Clear();
        var b = Path.Combine(_srcDir, "b.txt");
        File.Move(a, b);

        await pipeline.RequestRescanAsync();
        WaitFor(() => File.Exists(Path.Combine(_dstDir, "b.txt")), "replayed dest");

        Assert.Equal("move-me", File.ReadAllText(Path.Combine(_dstDir, "b.txt")));
        Assert.False(File.Exists(Path.Combine(_dstDir, "a.txt")));
        Assert.Empty(_copy.Calls);
        Assert.Contains(_log.Messages, m => m.Contains("Replayed move"));
        pipeline.Dispose();
        db.Dispose();
    }

    [Fact]
    public async Task IdentityCapture_EventCopyStoresFileId()
    {
        using var pipeline = StartDbPipeline("ident-state.db", null, out var db);
        var file = Path.Combine(_srcDir, "id.txt");
        File.WriteAllText(file, "identify-me");
        _monitor.Fire(ChangeType.Created, file);
        WaitFor(() => db.LoadAll().ContainsKey("id.txt"), "state update");

        var row = await db.GetFullStateAsync("id.txt");
        Assert.True(row.HasValue);
        Assert.False(string.IsNullOrEmpty(row.Value.fileId));
        pipeline.Dispose();
        db.Dispose();
    }

    [Fact]
    public async Task FullVerify_Disabled_SkipsTamperedDest()
    {
        using var pipeline = StartDbPipeline("verifyA-state.db",
            cfg => cfg.Sync = new SyncConfig { FullRoutineCheckDays = 0 }, out var db);
        var src = Path.Combine(_srcDir, "v.txt");
        var dst = Path.Combine(_dstDir, "v.txt");
        File.WriteAllText(src, "0123456789");
        _monitor.Fire(ChangeType.Created, src);
        WaitFor(() => File.Exists(dst), "dest copy");

        File.WriteAllText(dst, "XXXXXXXXXX");
        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
        await pipeline.RequestRescanAsync();

        Assert.Equal("XXXXXXXXXX", File.ReadAllText(dst));
        pipeline.Dispose();
        db.Dispose();
    }

    [Fact]
    public async Task FullVerify_Due_RestoresTamperedDest()
    {
        var stateDir = Path.Combine(_root, "vstate");
        Directory.CreateDirectory(stateDir);
        using var pipeline = StartDbPipeline("verifyB-state.db",
            cfg => { cfg.Sync = new SyncConfig { FullRoutineCheckDays = 14 }; cfg.StateDbPath = stateDir; }, out var db);
        var src = Path.Combine(_srcDir, "v.txt");
        var dst = Path.Combine(_dstDir, "v.txt");
        File.WriteAllText(src, "0123456789");
        _monitor.Fire(ChangeType.Created, src);
        WaitFor(() => File.Exists(dst), "dest copy");

        // The initial sync stamped the cadence today; backdate past the
        // 14-day window so the rescan below is actually due.
        pipeline.StampFullVerify(DateTime.UtcNow.AddDays(-15));
        File.WriteAllText(dst, "XXXXXXXXXX");
        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
        await pipeline.RequestRescanAsync();
        WaitFor(() => File.ReadAllText(dst) == "0123456789", "tamper restore");

        Assert.Equal("0123456789", File.ReadAllText(dst));
        pipeline.Dispose();
        db.Dispose();
    }
}
