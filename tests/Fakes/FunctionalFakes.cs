using System.Collections.Concurrent;

namespace TicTack;

public sealed class EventMonitor : IFileMonitor
{
    public event EventHandler<FileChangedEventArgs>? Changed;
    public event EventHandler<MonitorErrorEventArgs>? Error;
    public bool Started { get; private set; }

    public void Fire(ChangeType type, string path, string? oldPath = null) =>
        Changed?.Invoke(this, new FileChangedEventArgs(type, path, oldPath));

    public void FireError(Exception exception) =>
        Error?.Invoke(this, new MonitorErrorEventArgs(exception));

    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void Dispose() { }
}

public sealed class RecordingLogger : ILogger
{
    public ConcurrentBag<string> Messages { get; } = new();

    public void Debug(string msg) => Messages.Add("DBG:" + msg);
    public void Info(string msg) => Messages.Add("INF:" + msg);
    public void Warn(string msg) => Messages.Add("WRN:" + msg);
    public void Error(string msg, Exception? ex = null) =>
        Messages.Add("ERR:" + msg + (ex != null ? "|" + ex.Message : ""));
}

public sealed class RecordingAction : IFileAction
{
    private readonly IFileAction _inner;

    public ConcurrentBag<FileActionArgs> Calls { get; } = new();
    public Action<FileActionArgs>? AfterExecute { get; set; }

    public RecordingAction(IFileAction inner) => _inner = inner;

    public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
    {
        Calls.Add(args);
        var result = await _inner.ExecuteAsync(args, ct);
        AfterExecute?.Invoke(args);
        return result;
    }
}

public sealed class FaultingAction : IFileAction
{
    private readonly string _message;

    public FaultingAction(string message) => _message = message;

    public Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct) =>
        Task.FromResult(ActionResult.Fail(_message));
}

public sealed class BlockingAction : IFileAction
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return ActionResult.Ok();
    }
}

public sealed class RecordingValidator : IValidator
{
    private readonly IValidator _inner;

    public int Calls;
    public FileSnapshot? LastSnapshot;

    public RecordingValidator(IValidator inner) => _inner = inner;

    public async Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
    {
        Calls++;
        LastSnapshot = sourceSnapshot;
        return await _inner.ValidateAsync(sourcePath, destPath, sourceSnapshot);
    }
}

public sealed class RecordingDeletion : IDeletionStrategy
{
    private readonly IDeletionStrategy _inner;

    public ConcurrentBag<(string? src, string dst)> Calls { get; } = new();

    public RecordingDeletion(IDeletionStrategy inner) => _inner = inner;

    public async Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct)
    {
        Calls.Add((sourcePath, destPath));
        return await _inner.HandleDeletionAsync(sourcePath, destPath, ct);
    }
}
