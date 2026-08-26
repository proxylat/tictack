using System.Collections.Concurrent;

namespace TicTack;

public class PipelineTests : IDisposable
{
    private readonly string _root;
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly FakeMonitor _monitor = new();
    private readonly FakeComparer _comparer = new();
    private readonly RecordingAction _copy = new();
    private readonly RecordingAction _delete = new();
    private readonly RecordingAction _rename = new();
    private readonly FakeValidator _validator = new();
    private readonly FakeDeletion _deletion = new();
    private readonly MockLogger _log = new();
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
        _pipeline = new SyncPipeline(cfg, _monitor, _comparer, _copy, _delete, _rename,
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
    public void Created_WhenComparerEqual_SkipsCopy()
    {
        _comparer.Equal = true;
        var file = Path.Combine(_srcDir, "same.txt");
        File.WriteAllText(file, "x");
        _monitor.Fire(ChangeType.Created, file);

        Thread.Sleep(400);
        Assert.Empty(_copy.Calls);
        Assert.Empty(_db.LoadAll());
    }

    [Fact]
    public void CopyFailure_LogsError_AndDoesNotUpdateState()
    {
        _copy.Result = ActionResult.Fail("boom");
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
        _monitor.Fire(ChangeType.Created, file);

        Thread.Sleep(400);
        Assert.Empty(_copy.Calls);
    }

    private sealed class FakeMonitor : IFileMonitor
    {
        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;
        public void Start() { }
        public void Stop() { }
        public void Fire(ChangeType type, string path, string? oldPath = null)
            => Changed?.Invoke(this, new FileChangedEventArgs(type, path, oldPath));
        public void FireError(Exception ex) => Error?.Invoke(this, new MonitorErrorEventArgs(ex));
        public void Dispose() { }
    }

    private sealed class FakeComparer : IFileComparer
    {
        public bool Equal;
        public bool AreEqual(string sourcePath, string destPath) => Equal;
    }

    private sealed class RecordingAction : IFileAction
    {
        public ConcurrentBag<FileActionArgs> Calls { get; } = new();
        public ActionResult Result = ActionResult.Ok();
        public Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeValidator : IValidator
    {
        public int Calls;
        public Task<bool> ValidateAsync(string sourcePath, string destPath)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDeletion : IDeletionStrategy
    {
        public ConcurrentBag<(string src, string dst)> Calls { get; } = new();
        public Task HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
        {
            Calls.Add((sourcePath!, destPath));
            return Task.CompletedTask;
        }
    }
}
