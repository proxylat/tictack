namespace TicTack;

public class PipelineTests : IDisposable
{
    private readonly string _root;
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly EventMonitor _monitor = new();
    private readonly RecordingAction _copy = new(new CopyAction(new FileAccessor()));
    private readonly RecordingAction _rename = new(new RenameAction());
    private readonly RecordingValidator _validator = new(new SizeValidator());
    private readonly RecordingDeletion _deletion = new(new MirrorDeletion());
    private readonly RecordingLogger _log = new();
    private StateDb _db = null!;
    private SyncPipeline _pipeline = null!;
    private int _syncsSeen;

    public PipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tictack-test-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_root, "src");
        _dstDir = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
        StartPipeline(null);
    }

    private void StartPipeline(Action<SourceConfig>? tweak)
    {
        var cfg = new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 };
        tweak?.Invoke(cfg);
        _db = new StateDb(Path.Combine(_root, "state.db"));
        _pipeline = new SyncPipeline(cfg, _monitor, new SizeComparer(), _copy, _rename,
            new ExponentialBackoffRetry(1, 0, 1), _validator, new NoVersioning(),
            _deletion, _log, _db);
        _pipeline.Start();
        _syncsSeen++;
        var expected = _syncsSeen;
        WaitFor(() => _log.Messages.Count(m => m.Contains("Sync complete")) >= expected, "initial sync");
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        try { Directory.Delete(_root, true); } catch { }
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
    public async Task Start_Twice_OnlyStartsOnce()
    {
        // Bounded wait: without the re-entry guard the second Start blocks
        // forever in SrcLock (observed as a 600s runner hang).
        var second = Task.Run(() => _pipeline.Start());
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(second, finished);
        await second;
        Assert.Equal(1, _log.Messages.Count(m => m.Contains("Started:")));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("Could not acquire sync lock"));
    }

    [Fact]
    public void Created_CopiesValidatesAndUpdatesState()
    {
        var file = Path.Combine(_srcDir, "a.txt");
        File.WriteAllText(file, "x");
        _monitor.Fire(ChangeType.Created, file);

        WaitFor(() => _db.LoadAll().ContainsKey("a.txt"), "state update");

        Assert.Equal(Path.Combine(_dstDir, "a.txt"), _copy.Calls.Single().DestPath);
        Assert.Equal(1, _validator.Calls);
    }

    [Fact]
    public void Created_UsesFreshSnapshotAfterCopy()
    {
        var file = Path.Combine(_srcDir, "fresh.txt");
        var changed = false;
        _copy.AfterExecute = args =>
        {
            if (changed) return;
            changed = true;
            File.AppendAllText(args.ChangeEvent.FullPath, "y");
            _monitor.Fire(ChangeType.Modified, file);
        };
        File.WriteAllText(file, "x");
        _monitor.Fire(ChangeType.Created, file);

        WaitFor(() => _db.LoadAll().ContainsKey("fresh.txt"), "fresh state update");

        Assert.Equal(2, _validator.LastSnapshot?.Length);
        Assert.Equal(2, _db.LoadAll()["fresh.txt"].size);
    }

    [Fact]
    public void Created_WhenComparerEqual_SkipsCopy()
    {
        var file = Path.Combine(_srcDir, "same.txt");
        File.WriteAllText(file, "x");
        File.WriteAllText(Path.Combine(_dstDir, "same.txt"), "x");
        var control = Path.Combine(_srcDir, "control.txt");
        File.WriteAllText(control, "control");
        _monitor.Fire(ChangeType.Created, file);
        _monitor.Fire(ChangeType.Created, control);

        // The control file must copy: proves the pipeline processed events promptly.
        WaitFor(() => _db.LoadAll().ContainsKey("control.txt"), "control copy");

        Assert.DoesNotContain(_copy.Calls, c => c.DestPath.EndsWith("same.txt"));
        Assert.False(_db.LoadAll().ContainsKey("same.txt"));
    }

    [Fact]
    public void CopyFailure_LogsError_AndDoesNotUpdateState()
    {
        using var failingPipeline = new SyncPipeline(
            new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 },
            _monitor, new SizeComparer(), new FaultingAction("boom"), _rename,
            new ExponentialBackoffRetry(1, 0, 1), _validator, new NoVersioning(), _deletion, _log,
            _db = new StateDb(Path.Combine(_root, "failure-state.db")));
        _pipeline.Dispose();
        failingPipeline.Start();
        var file = Path.Combine(_srcDir, "bad.txt");
        File.WriteAllText(file, "x");
        _monitor.Fire(ChangeType.Created, file);

        WaitFor(() => _log.Messages.Any(m => m.StartsWith("ERR:Copy failed")), "error log");

        Assert.Empty(_db.LoadAll());
    }

    [Fact]
    public void Deleted_InvokesDeletionStrategy_AndRemovesState()
    {
        _db.Upsert("gone.txt", 1, 1);
        var file = Path.Combine(_srcDir, "gone.txt");
        _monitor.Fire(ChangeType.Deleted, file);

        WaitFor(() => !_db.LoadAll().ContainsKey("gone.txt"), "state removal");

        Assert.Equal((file, Path.Combine(_dstDir, "gone.txt")), _deletion.Calls.Single());
    }

    [Fact]
    public void Deleted_WhenSourceMissing_IsBlocked()
    {
        Directory.Delete(_srcDir, true);
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "gone.txt"));

        WaitFor(() => _log.Messages.Any(m => m.StartsWith("WRN:Source folder missing")), "warning");

        Assert.Empty(_deletion.Calls);
    }

    [Fact]
    public void Created_WhenSourceVanished_RemovesDestAndStateRow()
    {
        // A Created/Modified event whose source is already gone must delete the
        // destination through TrackedDeleter, so the stale state row cannot
        // inflate CountAsync and skew the percent delete guard.
        var rel = "vanished.txt";
        var srcPath = Path.Combine(_srcDir, rel);
        var destPath = Path.Combine(_dstDir, rel);
        File.WriteAllText(destPath, "orphan");
        _db.Upsert(rel, 6, 1);

        _monitor.Fire(ChangeType.Created, srcPath);

        WaitFor(() => !_db.LoadAll().ContainsKey(rel), "state row removal");

        Assert.Equal((srcPath, destPath), _deletion.Calls.Single());
        Assert.False(File.Exists(destPath));
    }

    [Fact]
    public void Renamed_MovesDestFile_AndUpdatesState()
    {
        var oldSrc = Path.Combine(_srcDir, "old.txt");
        var newSrc = Path.Combine(_srcDir, "new.txt");
        File.WriteAllText(newSrc, "x");
        var oldDest = Path.Combine(_dstDir, "old.txt");
        File.WriteAllText(oldDest, "x");
        _db.Upsert("old.txt", 1, 1);

        _monitor.Fire(ChangeType.Renamed, newSrc, oldSrc);

        WaitFor(() => _db.LoadAll().ContainsKey("new.txt"), "state rename");

        var call = _rename.Calls.Single();
        Assert.Equal(oldDest, call.OldDestPath);
        Assert.Equal(Path.Combine(_dstDir, "new.txt"), call.DestPath);
        Assert.False(_db.LoadAll().ContainsKey("old.txt"));
    }

    [Fact]
    public void Renamed_WhenTargetDisappears_SkipsStateMetadataWithoutError()
    {
        var oldSrc = Path.Combine(_srcDir, "old.txt");
        var newSrc = Path.Combine(_srcDir, "new.txt");
        _db.Upsert("old.txt", 1, 1);

        _monitor.Fire(ChangeType.Renamed, newSrc, oldSrc);

        WaitFor(() => _log.Messages.Any(m => m.Contains("Renamed: " + oldSrc)), "rename processing");

        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("ERR:Processing"));
        Assert.False(_db.LoadAll().ContainsKey("old.txt"));
        Assert.False(_db.LoadAll().ContainsKey("new.txt"));
    }

    [Fact]
    public void StartupParity_KeepsNestedMirror_WhenSourceMatches()
    {
        _pipeline.Dispose();

        Directory.CreateDirectory(Path.Combine(_srcDir, "Documents"));
        Directory.CreateDirectory(Path.Combine(_dstDir, "Documents"));
        Directory.CreateDirectory(Path.Combine(_dstDir, ".archive", "old"));
        File.WriteAllText(Path.Combine(_srcDir, "Documents", "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_dstDir, "Documents", "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_dstDir, ".archive", "old", "x.txt"), "x");

        StartPipeline(null);

        Assert.True(File.Exists(Path.Combine(_dstDir, "Documents", "readme.txt")));
        Assert.DoesNotContain(_deletion.Calls, c => c.dst.Contains("Documents") && c.dst.EndsWith("Documents"));
        Assert.DoesNotContain(_deletion.Calls, c => c.dst.Contains(".archive"));
    }

    [Fact]
    public void Created_ExcludedPattern_IsNotCopied()
    {
        _pipeline.Dispose();
        StartPipeline(c => c.Filter.Exclude.Add("*.tmp"));

        var file = Path.Combine(_srcDir, "skip.tmp");
        File.WriteAllText(file, "x");
        var control = Path.Combine(_srcDir, "ok.txt");
        File.WriteAllText(control, "control");
        _monitor.Fire(ChangeType.Created, file);
        _monitor.Fire(ChangeType.Created, control);

        // The control file must copy: proves the pipeline processed events promptly.
        WaitFor(() => _db.LoadAll().ContainsKey("ok.txt"), "control copy");

        Assert.DoesNotContain(_copy.Calls, c => c.DestPath.EndsWith("skip.tmp"));
    }

    [Fact]
    public async Task RunOnceAsync_UsesPipelineEngineAndReleasesLock()
    {
        _pipeline.Dispose();
        var file = Path.Combine(_srcDir, "once.txt");
        File.WriteAllText(file, "once");
        using var once = new SyncPipeline(
            new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 },
            new EventMonitor(), new SizeComparer(), new CopyAction(new FileAccessor()),
            new RenameAction(), new ExponentialBackoffRetry(1, 0, 1), new SizeValidator(),
            new NoVersioning(), new MirrorDeletion(), _log,
            new StateDb(Path.Combine(_root, "once-state.db")));

        Assert.True(await once.RunOnceAsync());
        Assert.True(File.Exists(Path.Combine(_dstDir, "once.txt")));
        once.Dispose();
        Assert.False(File.Exists(Path.Combine(_dstDir, ".tictack.lock")));
    }

    [Fact]
    public async Task Dispose_CancelsInitialSyncAndReleasesLock()
    {
        _pipeline.Dispose();
        var monitor = new EventMonitor();
        var blocking = new BlockingAction();
        var cfg = new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 };
        using var pipeline = new SyncPipeline(cfg, monitor, new SizeComparer(), blocking, _rename,
            new ExponentialBackoffRetry(1, 0, 1), _validator, new NoVersioning(), _deletion, _log);

        File.WriteAllText(Path.Combine(_srcDir, "blocking.txt"), "x");
        pipeline.Start();
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.Dispose();

        Assert.False(File.Exists(Path.Combine(_dstDir, ".tictack.lock")));
    }

    [Fact]
    public async Task RunOnce_FailsClosedWhenSourceEnumerationFails()
    {
        _pipeline.Dispose();
        var stale = Path.Combine(_dstDir, "stale.txt");
        File.WriteAllText(stale, "keep");
        var monitor = new EventMonitor();
        var deletion = new RecordingDeletion(new MirrorDeletion());
        using var pipeline = new SyncPipeline(
            new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 },
            monitor, new SizeComparer(), new CopyAction(new FileAccessor()), new RenameAction(),
            new ExponentialBackoffRetry(1, 0, 1), new SizeValidator(), new NoVersioning(), deletion, _log,
            enumerateFiles: (_, _) => throw new IOException("injected scan failure"));

        Assert.False(await pipeline.RunOnceAsync());
        Assert.Equal("keep", File.ReadAllText(stale));
        Assert.Empty(deletion.Calls);
    }

    [Fact]
    public async Task DestinationLoss_BlocksLiveEvent()
    {
        _pipeline.Dispose();
        var ready = true;
        var monitor = new EventMonitor();
        var copy = new RecordingAction(new CopyAction(new FileAccessor()));
        var log = new RecordingLogger();
        using var pipeline = new SyncPipeline(
            new SourceConfig { Path = _srcDir, Destination = _dstDir, DebounceSeconds = 0 },
            monitor, new SizeComparer(), copy, new RenameAction(),
            new ExponentialBackoffRetry(1, 0, 1), new SizeValidator(), new NoVersioning(), new MirrorDeletion(), log,
            driveReady: _ => ready);

        pipeline.Start();
        WaitFor(() => log.Messages.Any(m => m.Contains("Sync complete")), "initial sync");
        ready = false;
        var file = Path.Combine(_srcDir, "mount-loss.txt");
        File.WriteAllText(file, "must not copy");
        monitor.Fire(ChangeType.Created, file);

        // The block warning proves the mount-loss event was processed while down.
        WaitFor(() => log.Messages.Any(m => m.Contains("event blocked") && m.Contains("mount-loss.txt")), "drive-loss block");

        // Re-enable and prove the pipeline is still alive and prompt.
        ready = true;
        var control = Path.Combine(_srcDir, "control.txt");
        File.WriteAllText(control, "control");
        monitor.Fire(ChangeType.Created, control);
        WaitFor(() => copy.Calls.Any(c => c.DestPath.EndsWith("control.txt")), "control copy");

        Assert.DoesNotContain(copy.Calls, c => c.DestPath.EndsWith("mount-loss.txt"));
        Assert.False(File.Exists(Path.Combine(_dstDir, "mount-loss.txt")));
    }

    [Fact]
    public void DeleteThreshold_Count_BlocksBatch_AndDefers()
    {
        _pipeline.Dispose();
        // Debounce holds both deletes so they flush as one batch of 2.
        StartPipeline(c =>
        {
            c.Sync.DeleteThresholdCount = 2;
            c.DebounceSeconds = 0.5;
        });

        var destA = Path.Combine(_dstDir, "a.txt");
        var destB = Path.Combine(_dstDir, "b.txt");
        File.WriteAllText(destA, "a");
        File.WriteAllText(destB, "b");
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "a.txt"));
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "b.txt"));

        var deferredPath = Path.Combine(_dstDir, ".tictack-deferred.json");
        WaitFor(() => File.Exists(deferredPath), "deferred record");

        Assert.Empty(_deletion.Calls);
        Assert.True(File.Exists(destA));
        Assert.True(File.Exists(destB));
        Assert.Contains(_log.Messages, m => m.Contains("Delete guard"));
        var deferred = File.ReadAllText(deferredPath);
        Assert.Contains("a.txt", deferred);
        Assert.Contains("b.txt", deferred);
    }

    [Fact]
    public void DeleteThreshold_Size_BlocksBatch()
    {
        _pipeline.Dispose();
        StartPipeline(c => c.Sync.DeleteThresholdSizeGb = 1);

        // Sizes come from the state DB, so no real 2 GB file is needed.
        _db.Upsert("big.bin", 2L * 1024 * 1024 * 1024, DateTime.UtcNow.Ticks);
        var dest = Path.Combine(_dstDir, "big.bin");
        File.WriteAllText(dest, "small");
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "big.bin"));

        WaitFor(() => _log.Messages.Any(m => m.Contains("Delete guard")), "guard");

        Assert.Empty(_deletion.Calls);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void DeleteThreshold_Percent_BlocksAboveFloor()
    {
        _pipeline.Dispose();
        // DeleteThresholdSizeGb keeps its 50 GB default: the percent branch
        // must still fire when the size branch does not trip.
        StartPipeline(c => c.Sync.DeleteThresholdPercent = 1);

        for (int i = 0; i < 51; i++)
            _db.Upsert($"p{i}.txt", 1, 1);
        var dest = Path.Combine(_dstDir, "p0.txt");
        File.WriteAllText(dest, "x");
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "p0.txt"));

        WaitFor(() => _log.Messages.Any(m => m.Contains("Delete guard")), "guard");

        Assert.Empty(_deletion.Calls);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public void DeleteThreshold_Percent_FloorLetsSmallStateThrough()
    {
        _pipeline.Dispose();
        StartPipeline(c => c.Sync.DeleteThresholdPercent = 1);

        for (int i = 0; i < 50; i++)
            _db.Upsert($"q{i}.txt", 1, 1);
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "q0.txt"));

        WaitFor(() => _deletion.Calls.Count == 1, "deletion");
    }

    [Fact]
    public void DeleteThreshold_Percent_ExactBoundaryBlocks()
    {
        _pipeline.Dispose();
        // 1/64*100 is exactly 1.5625 in binary: >= blocks, > would not.
        StartPipeline(c => c.Sync.DeleteThresholdPercent = 1.5625);

        for (int i = 0; i < 64; i++)
            _db.Upsert($"r{i}.txt", 1, 1);
        _monitor.Fire(ChangeType.Deleted, Path.Combine(_srcDir, "r0.txt"));

        WaitFor(() => _log.Messages.Any(m => m.Contains("Delete guard")), "guard");

        Assert.Empty(_deletion.Calls);
    }

    [Fact]
    public void Created_CaseDistinctNames_BothCopied()
    {
        if (OperatingSystem.IsWindows()) return; // Windows paths are case-insensitive

        _pipeline.Dispose();
        StartPipeline(c => c.DebounceSeconds = 2);

        var upper = Path.Combine(_srcDir, "Case.txt");
        var lower = Path.Combine(_srcDir, "case.txt");
        File.WriteAllText(upper, "upper");
        File.WriteAllText(lower, "lower");
        // Same debounce window: a case-folding event map would coalesce these.
        _monitor.Fire(ChangeType.Created, upper);
        _monitor.Fire(ChangeType.Created, lower);

        WaitFor(() => File.Exists(Path.Combine(_dstDir, "Case.txt")) && File.Exists(Path.Combine(_dstDir, "case.txt")),
            "both case-distinct copies");

        Assert.Equal("upper", File.ReadAllText(Path.Combine(_dstDir, "Case.txt")));
        Assert.Equal("lower", File.ReadAllText(Path.Combine(_dstDir, "case.txt")));
    }

}
