using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TicTack
{
    // One file awaiting completion: bytes sit in Tmp, nothing durable yet.
    // Internal: same-assembly only (SyncPipeline owns the single instance).
    internal sealed class PendingCompletion
    {
        public FileActionArgs Args = null!;
        public TempCopyResult Tmp = null!;
        public string FailLabel = "";
        public string ValidationLabel = "";
        public Func<FileSnapshot, CancellationToken, Task> Upsert = null!;
        public TaskCompletionSource<FileSnapshot?> Done = null!;
    }

    // Pipelined completion (complete_mode=pipelined): one dedicated thread owns
    // flush -> rename -> validate -> upsert per item, in queue order. Copy
    // workers never stall inside fsync; the bounded queue (capacity derived
    // from the worker count) blocks them when the disk can't keep up, so
    // memory stays flat. Crash invariant: the upsert delegate runs only after
    // the rename is durable — a crash before that leaves an orphan .tictack.tmp
    // for PowerGuard, never state ahead of durability.
    internal sealed class FileCompleter
    {
        private readonly CopyAction _copy;
        private readonly IValidator _validator;
        private readonly ILogger _log;
        private readonly Channel<PendingCompletion> _queue;
        private Task? _loop;

        public FileCompleter(CopyAction copy, IValidator validator, ILogger log, int capacity)
        {
            _copy = copy;
            _validator = validator;
            _log = log;
            _queue = Channel.CreateBounded<PendingCompletion>(new BoundedChannelOptions(Math.Max(1, capacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public Task LoopTask => _loop ?? Task.CompletedTask;

        public void Start(CancellationToken ct)
        {
            _loop = Task.Run(() => LoopAsync(ct));
        }

        // No new items. The loop finishes the in-flight item, releases
        // everyone still queued as canceled (orphan tmps -> PowerGuard),
        // then exits. Shutdown abandons; it never upserts ahead of rename.
        public void Complete()
        {
            _queue.Writer.TryComplete();
        }

        public async Task<FileSnapshot?> EnqueueAsync(
            FileActionArgs args,
            TempCopyResult tmp,
            string failLabel,
            string validationLabel,
            Func<FileSnapshot, CancellationToken, Task> upsert,
            CancellationToken ct)
        {
            var item = new PendingCompletion
            {
                Args = args,
                Tmp = tmp,
                FailLabel = failLabel,
                ValidationLabel = validationLabel,
                Upsert = upsert,
                // Async continuations: the completer thread must never run
                // funnel continuations inline (a blocked funnel would stall
                // every file behind it).
                Done = new TaskCompletionSource<FileSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            // Funnel cancelled while queued or waiting: release it as canceled
            // and let the loop skip the item below.
            using var reg = ct.Register(() => item.Done.TrySetCanceled());
            await _queue.Writer.WriteAsync(item, ct);
            return await item.Done.Task;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(ct))
                {
                    // Abandoned: the funnel already left (canceled). The tmp
                    // stays orphaned for PowerGuard; never touch it here.
                    if (item.Done.Task.IsCompleted) continue;
                    FileSnapshot? fresh = null;
                    try
                    {
                        var src = item.Args.ChangeEvent.FullPath;
                        var dst = item.Args.DestPath;
                        var result = _copy.CompleteTemp(item.Tmp, ct);
                        if (!result.Success)
                        {
                            _log.Error(item.FailLabel + ": " + src + ": " + result.ErrorMessage);
                        }
                        else if (!FileSnapshot.TryRead(src, out var snap))
                        {
                            _log.Error(item.ValidationLabel + ": source disappeared: " + src);
                        }
                        else
                        {
                            bool valid;
                            if (item.Tmp.SourceHash != null && item.Args.SourceSnapshot.HasValue
                                && snap.Equals(item.Args.SourceSnapshot.Value)
                                && _validator is HashValidator hashValidator)
                            {
                                valid = await hashValidator.ValidateWithSourceHashAsync(dst, item.Tmp.SourceHash, snap);
                            }
                            else
                            {
                                valid = await _validator.ValidateAsync(src, dst, snap);
                            }
                            if (!valid)
                            {
                                _log.Error(item.ValidationLabel + ": " + src + " -> " + dst);
                            }
                            else if (await TryUpsertAsync(item, snap, src, ct))
                            {
                                fresh = snap;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown mid-item: the rename may already be durable
                        // without an upsert — safe (next run re-copies).
                        item.Done.TrySetCanceled();
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Completion failed: " + item.Args.ChangeEvent.FullPath, ex);
                    }
                    item.Done.TrySetResult(fresh);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Abandon path: release anyone still queued so no funnel
                // waits forever on a loop that is gone.
                while (_queue.Reader.TryRead(out var pending)) pending.Done.TrySetCanceled();
            }
        }

        // True when the snapshot is claimed in state. False (logged): durable
        // but unclaimed — the next run re-copies, never diverges.
        private async Task<bool> TryUpsertAsync(PendingCompletion item, FileSnapshot snap, string src, CancellationToken ct)
        {
            try
            {
                await item.Upsert(snap, ct);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn("Completion upsert failed (will re-copy): " + src + ": " + ex.Message);
                return false;
            }
        }
    }
}
