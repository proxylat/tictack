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
    await db.UpsertAsync("crash-a.txt", 100, 1000);
    await db.UpsertAsync("crash-b.txt", 200, 2000);
    // Signal the writes are committed, then hang until the parent kills us.
    // The worker's connection never closes, so the -wal file must still hold
    // uncheckpointed frames at kill time. (Environment.FailFast hangs on
    // Windows under Error Reporting.)
    File.WriteAllText(args[2], "reached");
    Thread.Sleep(Timeout.Infinite);
}

if (args[0].Equals("deferred-drain", StringComparison.OrdinalIgnoreCase))
{
    // Crash-matrix probe for the two-phase hold drain: claims an expired
    // batch, sweeps (confirms) only the FIRST path, then hangs for the
    // parent to kill. Post-kill the store must still hold the second path
    // as claimed (HasPending true, re-drain returns it) while the first is
    // gone. ConfirmClaimed stands in for the dest-file delete + sweep.
    if (args.Length != 4)
        return 2;
    using var dd = new DeferredDeletion(args[1], args[2], 0, new WorkerLogger());
    dd.RecordPending(new[] { Path.Combine(args[1], "held-a.txt"), Path.Combine(args[1], "held-b.txt") });
    var chunk = dd.TakeExpiredChunk();
    if (chunk.Files.Count > 0)
        dd.ConfirmClaimed(new[] { chunk.Files[0] });
    File.WriteAllLines(args[3], chunk.Files);
    Thread.Sleep(Timeout.Infinite);
}

return 2;

internal sealed class WorkerLogger : ILogger
{
    public void Debug(string msg) { }
    public void Info(string msg) { }
    public void Warn(string msg) => Console.Error.WriteLine(msg);
    public void Error(string msg, Exception? ex = null) => Console.Error.WriteLine(msg + (ex == null ? "" : ": " + ex.Message));
}
