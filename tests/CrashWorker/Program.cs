using System.Diagnostics;
using TicTack;

if (args.Length < 1)
    return 2;

if (args[0].Equals("copy", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 5 || !Enum.TryParse<CopyCheckpoint>(args[3], true, out var checkpoint))
        return 2;

    var change = new FileChangedEventArgs(ChangeType.Created, args[1]);
    var fileArgs = new FileActionArgs(change, Path.GetDirectoryName(args[1])!, Path.GetDirectoryName(args[2])!);
    var action = new CopyAction(new FileAccessor(), true, point =>
    {
        if (point == checkpoint)
        {
            // Signal the checkpoint, then hang until the parent kills us.
            // (Environment.FailFast hangs on Windows under Error Reporting.)
            File.WriteAllText(args[4], "reached");
            Thread.Sleep(Timeout.Infinite);
        }
    });
    var result = await action.ExecuteAsync(fileArgs, CancellationToken.None);
    return result.Success ? 0 : 1;
}

if (args[0].Equals("lock", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3 || !int.TryParse(args[2], out var holdMs))
        return 2;

    using var sourceLock = new SrcLock(args[1], new WorkerLogger(), TimeSpan.Zero);
    if (!sourceLock.IsHeld)
        return 3;
    await Task.Delay(holdMs);
    return 0;
}

if (args[0].Equals("statedb", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
        return 2;
    using var db = new StateDb(args[1]);
    db.Upsert("crash-a.txt", 100, 1000);
    db.Upsert("crash-b.txt", 200, 2000);
    // Signal the writes are committed, then hang until the parent kills us.
    // The worker's connection never closes, so the -wal file must still hold
    // uncheckpointed frames at kill time. (Environment.FailFast hangs on
    // Windows under Error Reporting.)
    File.WriteAllText(args[2], "reached");
    Thread.Sleep(Timeout.Infinite);
}

if (args[0].Equals("repro", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2)
        return 2;
    return await Repro.RunAsync(args[1]);
}

return 2;

internal sealed class WorkerLogger : ILogger
{
    public void Debug(string msg) { }
    public void Info(string msg) { }
    public void Warn(string msg) => Console.Error.WriteLine(msg);
    public void Error(string msg, Exception? ex = null) => Console.Error.WriteLine(msg + (ex == null ? "" : ": " + ex.Message));
}

// Hang-hang repro: mirrors CommandTests step by step (same calls as
// Program.RunOnceAsync -> BuildPipeline -> SyncPipeline.RunOnceAsync, with a
// default config) and prints a timestamped breadcrumb after every stage.
// A background watchdog proves the process is alive even if the main flow
// freezes, so one run pinpoints the hanging stage.
internal static class Repro
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    internal static void Trace(string msg) => Mark(msg);

    private static void Mark(string msg)
    {
        Console.WriteLine($"[{_sw.Elapsed:mm\\:ss\\.f}] {msg}");
        Console.Out.Flush();
    }

    public static async Task<int> RunAsync(string root)
    {
        var watchdog = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    Thread.Sleep(10000);
                    Mark("watchdog: still alive");
                }
            }
            catch { }
        })
        { IsBackground = true };
        watchdog.Start();

        try
        {
            root = Path.GetFullPath(root);
            var source = Path.Combine(root, "source");
            var dest = Path.Combine(root, "dest");
            Mark("repro start, root=" + root);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(source, "repro.txt"), "repro-content");
            Mark("source file written");

            if (!Directory.Exists(source)) { Mark("FAIL: source missing"); return 1; }
            Mark("readiness: source exists (IsDriveReady skipped: internal, audited bounded)");

            // Mirror of Program.BuildPipeline with a default config.
            var accessor = new FileAccessor();
            var level = Config.ParseVerification(null);
            var comparer = new TraceComparer(ComparerFactory.Create(level, accessor));
            var validator = new TraceValidator(ValidatorFactory.Create(level, accessor));
            var retry = new ExponentialBackoffRetry();
            var versioning = new TraceVersioning(VersioningFactory.Create(null, dest));
            var deletion = new TraceDeletion(DeletionStrategyFactory.Create(null, dest));
            var copyAction = new TraceAction(new CopyAction(accessor, true));
            var renameAction = new RenameAction();
            var log = new TraceLogger();
            StateDb? stateDb = null;
            try { stateDb = new StateDb(Path.Combine(root, "state.db")); }
            catch (Exception ex) { Mark("StateDB init failed, continuing without: " + ex.Message); }
            Mark("state db ready");

            var monitorConfig = new MonitorConfig();
            IFileMonitor CreateWatcher() => OperatingSystem.IsWindows()
                ? new FileWatcherMonitor(source, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds)
                : new FsWatchMonitor(source, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds);
            IFileMonitor monitor;
            switch (monitorConfig.Type != null ? monitorConfig.Type.ToLowerInvariant() : "")
            {
                case "watcher": monitor = CreateWatcher(); break;
                case "polling": monitor = new PollingMonitor(source, monitorConfig.PollingIntervalSeconds); break;
                default:
                    monitor = new CompositeMonitor(
                        CreateWatcher(),
                        new PollingMonitor(source, monitorConfig.PollingIntervalSeconds));
                    break;
            }
            Mark("monitor constructed: " + monitor.GetType().Name);

            var src = new SourceConfig { Path = source, Destination = dest, DebounceSeconds = 0 };
            using var pipeline = new SyncPipeline(src, monitor, comparer, copyAction, renameAction,
                retry, validator, versioning, deletion, log, stateDb,
                autoExcludePrefixes: Array.Empty<string>(),
                deferredPath: Path.Combine(root, "tictack-deferred-src.json"));
            Mark("pipeline constructed");
            Mark("starting initial sync (RunOnceAsync)");
            var ok = await pipeline.RunOnceAsync();
            Mark("initial sync returned " + ok);
            var destFile = Path.Combine(dest, "repro.txt");
            if (!ok) { Mark("REPRO FAILED: RunOnceAsync returned false"); return 1; }
            if (!File.Exists(destFile)) { Mark("REPRO FAILED: dest file missing"); return 1; }
            Mark("dest file present");
            Mark("REPRO PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Mark("REPRO EXCEPTION: " + ex);
            return 1;
        }
    }
}

internal sealed class TraceLogger : ILogger
{
    public void Debug(string msg) => Repro.Trace("log debug: " + msg);
    public void Info(string msg) => Repro.Trace("log info: " + msg);
    public void Warn(string msg) => Repro.Trace("log warn: " + msg);
    public void Error(string msg, Exception? ex = null) => Repro.Trace("log error: " + msg + (ex == null ? "" : " / " + ex.Message));
}

internal sealed class TraceComparer : IFileComparer
{
    private readonly IFileComparer _inner;
    public TraceComparer(IFileComparer inner) { _inner = inner; }
    public bool RequiresContentRead => _inner.RequiresContentRead;
    public bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
    {
        Repro.Trace("enter comparer");
        try { return _inner.AreEqual(sourcePath, destPath, sourceSnapshot); }
        finally { Repro.Trace("exit comparer"); }
    }
}

internal sealed class TraceAction : IFileAction
{
    private readonly IFileAction _inner;
    public TraceAction(IFileAction inner) { _inner = inner; }
    public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
    {
        Repro.Trace("enter copy");
        try { return await _inner.ExecuteAsync(args, ct); }
        finally { Repro.Trace("exit copy"); }
    }
}

internal sealed class TraceValidator : IValidator
{
    private readonly IValidator _inner;
    public TraceValidator(IValidator inner) { _inner = inner; }
    public async Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
    {
        Repro.Trace("enter validator");
        try { return await _inner.ValidateAsync(sourcePath, destPath, sourceSnapshot); }
        finally { Repro.Trace("exit validator"); }
    }
}

internal sealed class TraceVersioning : IVersioningStrategy
{
    private readonly IVersioningStrategy _inner;
    public TraceVersioning(IVersioningStrategy inner) { _inner = inner; }
    public async Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
    {
        Repro.Trace("enter versioning");
        try { return await _inner.ArchivePreviousVersionAsync(destPath, ct); }
        finally { Repro.Trace("exit versioning"); }
    }
}

internal sealed class TraceDeletion : IDeletionStrategy
{
    private readonly IDeletionStrategy _inner;
    public TraceDeletion(IDeletionStrategy inner) { _inner = inner; }
    public async Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
    {
        Repro.Trace("enter deletion");
        try { return await _inner.HandleDeletionAsync(sourcePath, destPath, ct); }
        finally { Repro.Trace("exit deletion"); }
    }
}
