namespace TicTack;

// complete_mode=pipelined: the completer owns flush -> rename ->
// validate -> upsert on one thread. These tests pin the crash
// invariant (no upsert before durable rename) and the inline fallback.
public sealed class CompleterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "TicTackCompleter_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static SourceConfig MakeConfig(string src, string dst, string verification, string completeMode, int workers) => new()
    {
        Path = src,
        Destination = dst,
        DebounceSeconds = 0.05,
        Filter = new FilterConfig { MaxFileSizeMb = "no-limit" },
        Sync = new SyncConfig { Verification = verification, CompleteMode = completeMode, InitialSyncWorkers = workers }
    };

    static SyncPipeline MakePipeline(SourceConfig cfg, EventMonitor monitor, IFileComparer comparer,
        IValidator validator, IFileAction copy, RecordingLogger log, StateDb? stateDb = null) =>
        new(cfg, monitor, comparer, copy, new RenameAction(), new ExponentialBackoffRetry(1, 0, 1),
            validator, new NoVersioning(), new MirrorDeletion(), log, stateDb);

    static void WriteFiles(string dir, int count, int size, string ext = ".bin")
    {
        Directory.CreateDirectory(dir);
        var buffer = new byte[size];
        new Random(42).NextBytes(buffer);
        for (int i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(dir, $"file{i:D5}{ext}"), buffer);
    }

    static async Task<Dictionary<string, (long size, long mtime)>> ReadStateAsync(string statePath, int count)
    {
        var rows = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        using var db = new StateDb(statePath);
        for (int i = 0; i < count; i++)
        {
            var rel = $"file{i:D5}.bin";
            var s = await db.TryGetStateAsync(rel);
            Assert.True(s.HasValue, "missing state row: " + rel);
            rows[rel] = (s.Value.size, s.Value.mtime);
        }
        return rows;
    }

    [Fact]
    public async Task PipelinedInitialSync_MatchesInline()
    {
        Assert.Equal("inline", new SyncConfig().CompleteMode);
        var src = Path.Combine(_dir, "src");
        var dstInline = Path.Combine(_dir, "dst-inline");
        var dstPipe = Path.Combine(_dir, "dst-pipe");
        WriteFiles(src, 50, 1024);

        var stateInline = Path.Combine(_dir, "inline.db");
        using (var db = new StateDb(stateInline))
        using (var pipeline = MakePipeline(MakeConfig(src, dstInline, "hash", "inline", 2), new EventMonitor(),
            new HashComparer(), new HashValidator(), new CopyAction(new FileAccessor()), new RecordingLogger(), db))
        {
            Assert.True(await pipeline.RunOnceAsync());
        }
        var inlineRows = await ReadStateAsync(stateInline, 50);

        var statePipe = Path.Combine(_dir, "pipe.db");
        using (var db = new StateDb(statePipe))
        using (var pipeline = MakePipeline(MakeConfig(src, dstPipe, "hash", "pipelined", 4), new EventMonitor(),
            new HashComparer(), new HashValidator(), new CopyAction(new FileAccessor()), new RecordingLogger(), db))
        {
            Assert.True(await pipeline.RunOnceAsync());
        }
        var pipeRows = await ReadStateAsync(statePipe, 50);

        Assert.Equal(inlineRows, pipeRows);
        for (int i = 0; i < 50; i++)
        {
            var rel = $"file{i:D5}.bin";
            Assert.True(File.ReadAllBytes(Path.Combine(dstInline, rel)).SequenceEqual(
                File.ReadAllBytes(Path.Combine(dstPipe, rel))), "bytes differ: " + rel);
        }
        Assert.Empty(Directory.GetFiles(dstPipe, "*.tictack.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PipelinedConfig_WithWrapper_StaysInlineAndCopies()
    {
        var src = Path.Combine(_dir, "src");
        var dst = Path.Combine(_dir, "dst");
        WriteFiles(src, 20, 512);
        var copy = new RecordingAction(new CopyAction(new FileAccessor()));
        var statePath = Path.Combine(_dir, "state.db");
        using (var db = new StateDb(statePath))
        using (var pipeline = MakePipeline(MakeConfig(src, dst, "date_and_size", "pipelined", 2), new EventMonitor(),
            new DateSizeComparer(), new SizeValidator(), copy, new RecordingLogger(), db))
        {
            Assert.True(await pipeline.RunOnceAsync());
        }
        Assert.Equal(20, copy.Calls.Count);
        for (int i = 0; i < 20; i++)
            Assert.True(File.Exists(Path.Combine(dst, $"file{i:D5}.bin")));
    }

    [Fact]
    public async Task Enqueue_CompletesAndUpserts()
    {
        var src = Path.Combine(_dir, "src");
        var dst = Path.Combine(_dir, "dst");
        WriteFiles(src, 1, 512);
        var srcFile = Path.Combine(src, "file00000.bin");
        var copy = new CopyAction(new FileAccessor());
        var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, srcFile), src, dst);
        var tmp = await copy.CopyToTempAsync(args, CancellationToken.None);
        Assert.True(tmp.Success);

        using var cts = new CancellationTokenSource();
        var completer = new FileCompleter(copy, new SizeValidator(), new RecordingLogger(), 4);
        completer.Start(cts.Token);
        int upserts = 0;
        var fresh = await completer.EnqueueAsync(args, tmp, "Copy failed", "Validation FAILED",
            (snap, _) => { Interlocked.Increment(ref upserts); return Task.CompletedTask; }, cts.Token);
        completer.Complete();
        await completer.LoopTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(fresh);
        Assert.Equal(1, upserts);
        Assert.True(new FileInfo(srcFile).Length == new FileInfo(Path.Combine(dst, "file00000.bin")).Length);
    }

    [Fact]
    public async Task CancelledItem_NeverCompletesOrUpserts()
    {
        var src = Path.Combine(_dir, "src");
        var dst = Path.Combine(_dir, "dst");
        WriteFiles(src, 2, 512);
        var copy = new CopyAction(new FileAccessor());
        using var loopCts = new CancellationTokenSource();
        var completer = new FileCompleter(copy, new SizeValidator(), new RecordingLogger(), 4);
        completer.Start(loopCts.Token);

        // Item 1 parks the single loop thread inside Upsert.
        var src1 = Path.Combine(src, "file00000.bin");
        var args1 = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, src1), src, dst);
        var tmp1 = await copy.CopyToTempAsync(args1, CancellationToken.None);
        Assert.True(tmp1.Success);
        var inUpsert = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpsert = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int upserts1 = 0;
        var t1 = completer.EnqueueAsync(args1, tmp1, "Copy failed", "Validation FAILED",
            async (snap, _) =>
            {
                Interlocked.Increment(ref upserts1);
                inUpsert.TrySetResult(true);
                await releaseUpsert.Task;
            }, loopCts.Token);
        await inUpsert.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Item 2 is cancelled while queued behind it: abandon, never touch.
        var src2 = Path.Combine(src, "file00001.bin");
        var args2 = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, src2), src, dst);
        var tmp2 = await copy.CopyToTempAsync(args2, CancellationToken.None);
        Assert.True(tmp2.Success);
        using var cts2 = new CancellationTokenSource();
        int upserts2 = 0;
        var t2 = completer.EnqueueAsync(args2, tmp2, "Copy failed", "Validation FAILED",
            (snap, _) => { Interlocked.Increment(ref upserts2); return Task.CompletedTask; }, cts2.Token);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);

        releaseUpsert.TrySetResult(true);
        var fresh1 = await t1.WaitAsync(TimeSpan.FromSeconds(10));
        completer.Complete();
        await completer.LoopTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(fresh1);
        Assert.Equal(1, upserts1);
        Assert.Equal(0, upserts2);
        Assert.True(File.Exists(Path.Combine(dst, "file00000.bin")));
        Assert.False(File.Exists(Path.Combine(dst, "file00001.bin")));
        // Item 2's tmp stays orphaned for PowerGuard recovery.
        Assert.Equal(1, Directory.GetFiles(dst, "*.tictack.tmp").Length);
    }
}
