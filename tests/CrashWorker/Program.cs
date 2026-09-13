using System.Diagnostics;
using TicTack;

if (args.Length < 1)
    return 2;

if (args[0].Equals("copy", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 4 || !Enum.TryParse<CopyCheckpoint>(args[3], true, out var checkpoint))
        return 2;

    var change = new FileChangedEventArgs(ChangeType.Created, args[1]);
    var fileArgs = new FileActionArgs(change, Path.GetDirectoryName(args[1])!, Path.GetDirectoryName(args[2])!);
    var action = new CopyAction(new FileAccessor(), true, point =>
    {
        if (point == checkpoint) Environment.FailFast("Crash checkpoint: " + point);
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

return 2;

internal sealed class WorkerLogger : ILogger
{
    public void Debug(string msg) { }
    public void Info(string msg) { }
    public void Warn(string msg) => Console.Error.WriteLine(msg);
    public void Error(string msg, Exception? ex = null) => Console.Error.WriteLine(msg + (ex == null ? "" : ": " + ex.Message));
}
