using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace TicTack
{
    public class SyncPipeline : IDisposable
    {
        private readonly SourceConfig _config;
        private readonly IFileMonitor _monitor;
        private readonly IFileComparer _comparer;
        private readonly IFileAction _copyAction;
        private readonly IFileAction _renameAction;
        private readonly ExponentialBackoffRetry _retry;
        private readonly IValidator _validator;
        private readonly IVersioningStrategy _versioning;
        private readonly IDeletionStrategy _deletion;
        private readonly ILogger _log;
        private readonly IFileFilter? _filter;
        private readonly IDisposable? _queueCounter;

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private readonly ConcurrentDictionary<string, FileChangedEventArgs> _pendingEvents = new ConcurrentDictionary<string, FileChangedEventArgs>(PathComparer);
        private readonly ConcurrentDictionary<string, DateTime> _debounce = new ConcurrentDictionary<string, DateTime>(PathComparer);
        private readonly ReadyDrain _drain;
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private CancellationTokenSource? _cts;
        private bool _started;
        private Task? _processor;
        private Task<bool>? _initialSync;
        // Coalesces concurrent on-demand covering scans to one run.
        private int _scanRunning;
        private readonly List<Task> _deferredTasks = new List<Task>();
        private readonly object _taskLock = new object();
        private Timer? _startRetryTimer;
        private Timer? _deferredCheckTimer;
        private SrcLock? _lock;
        private TimeSpan? _lockTimeout;
        private readonly ChurnMonitor _churn;
        private bool _churnArmed;
        private readonly StateDb? _stateDb;
        private readonly DeferredDeletion _deferred;
        private readonly TrackedDeleter _deleter;
        private readonly ParityScanner _parityScanner;
        private readonly Scrubber _scrubber;
        private readonly Func<string, bool> _directoryExists;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateFiles;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateDirectories;
        private readonly Func<string, bool> _driveReady;
        private readonly DeletionGuard _deletions;
        private readonly FileCompleter? _completer;
        private Timer? _parityTimer;
        private EventHandler<MonitorErrorEventArgs>? _monitorErrorHandler;
        private volatile bool _parityRequested;
        private long _completedItems;
        private DateTime _lastProgressUtc;
        private bool _disposed;
        // Full-verify cadence store (own JobRunStore, own file): last date a
        // full content re-verify completed. Null when FullVerifyDays <= 0.
        private readonly JobRunStore? _verifyStore;
        private readonly string _verifyKey;
        private readonly string _restoreKey;
        public SyncPipeline(
            SourceConfig config,
            IFileMonitor monitor,
            IFileComparer comparer,
            IFileAction copyAction,
            IFileAction renameAction,
            ExponentialBackoffRetry retry,
            IValidator validator,
            IVersioningStrategy versioning,
            IDeletionStrategy deletion,
            ILogger log,
            StateDb? stateDb = null,
            string[]? autoExcludePrefixes = null,
            string? deferredDir = null,
            string? deferredFilePrefix = null,
            Func<string, bool>? directoryExists = null,
            Func<string, SearchOption, IEnumerable<string>>? enumerateFiles = null,
            Func<string, SearchOption, IEnumerable<string>>? enumerateDirectories = null,
            Func<string, bool>? driveReady = null)
        {
            _config = config;
            _monitor = monitor;
            _comparer = comparer;
            _copyAction = copyAction;
            _renameAction = renameAction;
            _retry = retry;
            _validator = validator;
            _versioning = versioning;
            _deletion = deletion;
            _log = log;
            _stateDb = stateDb;
            _directoryExists = directoryExists ?? Directory.Exists;
            _enumerateFiles = enumerateFiles ?? ((path, option) => Directory.EnumerateFiles(path, "*", option));
            _enumerateDirectories = enumerateDirectories ?? ((path, option) => Directory.EnumerateDirectories(path, "*", option));
            _driveReady = driveReady ?? (path => DriveGuard.IsReady(path));
            _drain = new ReadyDrain(_config.Sync != null
                && string.Equals(_config.Sync.DrainStrategy, "ready_queue", StringComparison.OrdinalIgnoreCase));
            // Hash-during-copy: only the real CopyAction paired with a HashValidator
            // can produce a trusted copy-time hash. Test wrappers (RecordingAction)
            // leave the flag off and take the classic double-read fallback below.
            if (copyAction is CopyAction copy && validator is HashValidator) copy.ComputeSourceHash = true;
            // Pipelined completion: the completer owns flush -> rename ->
            // validate -> upsert on one thread; workers only copy. Capacity
            // is derived from the worker count (2x) for backpressure. Test
            // wrappers (RecordingAction) stay inline — same fallback shape
            // as the hash flag above.
            _completer = _config.Sync != null
                && string.Equals(_config.Sync.CompleteMode, "pipelined", StringComparison.OrdinalIgnoreCase)
                && copyAction is CopyAction realCopy
                ? new FileCompleter(realCopy, validator, log, 2 * Math.Max(1, _config.Sync.InitialSyncWorkers))
                : null;
            // Capture the queue itself, not `this`: a constructor that throws
            // after this point would otherwise leak the whole pipeline through
            // the static EventSource's counter callback.
            var pendingEvents = _pendingEvents;
            _queueCounter = TicTackEventSource.Log.RegisterQueueCounter(_config.Path, () => pendingEvents.Count);

            var holdDays = _config.Sync != null && _config.Sync.DeleteHoldDays > 0 ? _config.Sync.DeleteHoldDays : SyncConfig.DefaultDeleteHoldDays;
            _deferred = new DeferredDeletion(
                deferredDir ?? _config.Destination,
                deferredFilePrefix ?? "tictack-deferred",
                holdDays, _log);

            var filters = new List<IFileFilter>();
            if (_config.Filter != null && _config.Filter.Exclude != null && _config.Filter.Exclude.Count > 0)
                filters.Add(new PatternFilter(_config.Filter.Exclude.ToArray()));
            if (autoExcludePrefixes != null && autoExcludePrefixes.Length > 0)
                filters.Add(new PathPrefixFilter(autoExcludePrefixes));
            long? maxSize = _config.Filter != null ? Config.ParseFileSizeLimit(_config.Filter.MaxFileSizeMb) : null;
            if (maxSize.HasValue && maxSize.Value > 0)
                filters.Add(new SizeFilter(maxSize.Value));
            _filter = filters.Count > 0 ? new CompositeFilter(filters) : null;
            _deleter = new TrackedDeleter(_deletion, _stateDb, _log);
            _parityScanner = new ParityScanner(_config, _filter, _directoryExists, _enumerateFiles, _enumerateDirectories, _deleter, _log);
            // Anomaly brake: feeds on copies/deletes/renames, freezes all
            // three funnels on trip. Disarmed until the first full scan
            // completes (ResetState disarms again); the marker file is the
            // human clear switch. Exempt while disarmed or initial sync runs.
            var syncCfg = _config.Sync;
            _churnArmed = false;
            _churn = new ChurnMonitor(Path.Combine(_config.Destination, ".tictack-churn-hold"),
                TimeSpan.FromMinutes(syncCfg != null && syncCfg.ChurnWindowMinutes > 0 ? syncCfg.ChurnWindowMinutes : 60),
                (long)((syncCfg != null && syncCfg.ChurnBytesGb > 0 ? syncCfg.ChurnBytesGb : 100) * 1024L * 1024L * 1024L),
                syncCfg != null && syncCfg.ChurnFileCount > 0 ? syncCfg.ChurnFileCount : 50000,
                syncCfg != null && syncCfg.ChurnRenameCount > 0 ? syncCfg.ChurnRenameCount : 10000,
                _log, null,
                () => !_churnArmed || (_initialSync != null && !_initialSync.IsCompleted));
            _deletions = new DeletionGuard(_config, stateDb, _deleter, _deferred, log, ArmDeferredCheckTimer, _churn);
            // Own cadence file, not the scheduler's jobs.db: keyed per source
            // like the state DB name. Carries the full-verify stamp and the
            // scrub shard index. Failures fall back to always-verify.
            // Always created (not gated on FullVerifyDays): the scrub shard
            // rotation persists here too, and FullVerifyDue still returns
            // false when days <= 0, so behavior is unchanged.
            _verifyKey = "fullverify-" + SourceLeaf(_config).ToLowerInvariant() + "-" + DeferredDeletion.StableHash(_config.Path);
            _restoreKey = "restore-" + SourceLeaf(_config).ToLowerInvariant() + "-" + DeferredDeletion.StableHash(_config.Path);
            try
            {
                var dir = !string.IsNullOrEmpty(_config.StateDbPath) ? _config.StateDbPath
                    : OperatingSystem.IsWindows()
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TicTack")
                        : "/var/lib/tictack";
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                _verifyStore = new JobRunStore(Path.Combine(dir, "verify.db"));
            }
            catch (Exception ex) { _log.Debug("Verify cadence store unavailable, verifying every run: " + ex.Message); }
            _scrubber = new Scrubber(_config, _filter, stateDb, _deleter.DeleteAsync, _verifyStore, log, _directoryExists, _enumerateFiles);
            // Opt-in vault mode: harden the destination root at startup.
            // Service mode only — a CLI run as the user locks itself out.
            if (syncCfg != null && syncCfg.DestinationProtect)
                DestinationGuard.Enforce(_config.Destination, log);
        }

        public void Start()
        {
            Start(true);
        }

        private void Start(bool startWorkers)
        {
            if (_started) return;
            _cts = new CancellationTokenSource();
            _lastProgressUtc = DateTime.UtcNow;
            if (!_directoryExists(_config.Path))
            {
                _log.Warn("Source directory not found, will retry: " + _config.Path);
                _startRetryTimer = new Timer(_ => RetryStart(), null, 10000, 10000);
                return;
            }
            if (!_driveReady(_config.Destination))
            {
                _log.Warn("Destination drive not ready: " + _config.Destination + ", will retry");
                _startRetryTimer = new Timer(_ => RetryStart(), null, 10000, 10000);
                return;
            }
            DoStart(startWorkers);
        }

        void DoStart(bool startWorkers = true)
        {
            // Start is not re-entrant: a second call would replace the CTS,
            // double-subscribe the monitor, and launch duplicate workers.
            // Worse, the duplicate DoStart would block in SrcLock acquisition.
            if (_started) return;
            _started = true;
            var lockTimeout = _config.Sync != null && _config.Sync.LockHandling == "retry"
                ? (TimeSpan?)TimeSpan.FromSeconds(_config.Sync.RetryLockSeconds > 0 ? _config.Sync.RetryLockSeconds : 600)
                : null;
            try { Directory.CreateDirectory(_config.Destination); }
            catch (Exception ex) { _log.Debug("Destination create failed: " + ex.Message); }
            _lockTimeout = lockTimeout;
            _lock = new SrcLock(Path.Combine(_config.Destination, ".tictack.lock"), _log, lockTimeout, mode: LockMode.Shared);
            if (!_lock.IsHeld)
            {
                _log.Error("Could not acquire sync lock; pipeline will not start: " + _config.Destination);
                _lock.Dispose();
                _lock = null;
                _startRetryTimer = new Timer(_ => RetryStart(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                return;
            }
            _monitor.Changed += OnChanged;
            _monitorErrorHandler = (s, e) => _log.Error("Monitor error", e.Exception);
            _monitor.Error += _monitorErrorHandler;
            _monitor.Start();
            if (startWorkers) StartWorkers();
            _log.Debug("Started: " + _config.Path + " -> " + _config.Destination);
        }

        private void StartWorkers()
        {
            // Completer first: it must be listening before any copy
            // worker can enqueue.
            _completer?.Start(_cts!.Token);
            _initialSync = Task.Run(() => InitialSyncAsync());
            _processor = Task.Run(() => ProcessLoop());
            // Arm the deadline deferred-deletion recheck only when something
            // is actually pending (e.g. persisted from a previous run).
            // Recording new pending deletions arms it too; the timer fires
            // once at the next expiry/warning deadline, never periodically.
            if (_deferred.HasPending) ArmDeferredCheckTimer();
            _parityTimer = new Timer(_ => QueueParityCheck(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        }

        public async Task<bool> RunOnceAsync(Func<Task>? prepare = null)
        {
            Start(false);
            if (_lock == null || !_lock.IsHeld)
            {
                await StopAsync();
                return false;
            }

            try
            {
                if (prepare != null) await prepare();
                StartWorkers();
                return await _initialSync!;
            }
            finally
            {
                await StopAsync();
            }
        }

        public void ResetState()
        {
            _stateDb?.Clear();
            // The post-reset full scan replays every file as new: keep the
            // brake disarmed until it completes, or it trips instantly.
            _churn.Reset();
            _churnArmed = false;
        }

        // Rebuild path: drop every hold through the open store instead of
        // deleting the .db around it — Windows refuses to delete a file
        // another handle (ours) has open.
        public void ClearDeferredHolds() => _deferred.ClearStore();

        void RetryStart()
        {
            if (_cts!.IsCancellationRequested) return;
            if (!_directoryExists(_config.Path)) return;
            if (!_driveReady(_config.Destination))
            {
                _log.Debug("Destination drive still not ready: " + _config.Destination);
                return;
            }
            if (_startRetryTimer != null)
            {
                _startRetryTimer.Dispose();
                _startRetryTimer = null;
            }
            DoStart();
        }

        private void OnChanged(object? sender, FileChangedEventArgs e)
        {
            _debounce[e.FullPath] = DateTime.UtcNow.AddSeconds(_config.DebounceSeconds);
            _pendingEvents[e.FullPath] = e;
            _drain.Signal(e.FullPath, _debounce[e.FullPath]);
            // Restore fast path: a reappearing file drops its hold row by
            // indexed key. CancelIfPending is a no-op without I/O unless
            // holds exist, so the steady-state hot path stays untouched.
            if (e.ChangeType != ChangeType.Deleted)
            {
                try { _deferred.CancelIfPending(e.FullPath); } catch { }
            }
            _signal.Release();
        }

        private async Task ProcessLoop()
        {
            var token = _cts!.Token;
            if (_initialSync != null)
            {
                try { await _initialSync; }
                catch (OperationCanceledException) { }
            }
            while (!token.IsCancellationRequested)
            {
                FileChangedEventArgs e;
                if (_drain.TryTake(DateTime.UtcNow, _pendingEvents, _debounce, out e, out var wait))
                {
                    await ProcessEvent(e, token);
                    // Progress = the loop is alive and working. Updated even
                    // when the event failed: failure still flows through
                    // retry/logging, so movement here means not wedged.
                    _lastProgressUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _completedItems);
                }
                else if (wait.HasValue)
                {
                    try { await Task.Delay(wait.Value, token); } catch (OperationCanceledException) { }
                }
                else
                {
                    if (_parityRequested)
                    {
                        // Runs on the processor thread so parity never races
                        // live copies (a file copied mid-scan was being seen
                        // as destination-only and deleted).
                        _parityRequested = false;
                        await WithExclusiveLockAsync(async () =>
                        {
                            await _parityScanner.EnforceAsync(token);
                            await _scrubber.RunAsync(token);
                        });
                        continue;
                    }
                    SweepDebounced();
                    await _deletions.FlushAsync(token);
                    await WaitForSignal(token);
                }
            }
        }

        private void SweepDebounced()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _debounce)
            {
                if (kv.Value <= now)
                {
                    DateTime removed;
                    _debounce.TryRemove(kv.Key, out removed);
                    var path = kv.Key;
                    if (File.Exists(path))
                        _pendingEvents[path] = new FileChangedEventArgs(ChangeType.Modified, path);
                    else if (_directoryExists(Path.GetDirectoryName(path)!))
                        _pendingEvents[path] = new FileChangedEventArgs(ChangeType.Deleted, path);
                    else
                        continue;
                    _signal.Release();
                }
            }
        }

        // On-demand covering scan for monitor gap signals (watcher
        // overflow, USN re-baseline). Reuses the bounded chunked scan +
        // trailing parity of initial sync, so creates, modifies AND
        // deletes are covered with nothing retained after. Concurrent
        // requests coalesce to one scan; requests while the startup
        // scan is still running are dropped (it covers everything).
        public Task<bool> RequestRescanAsync()
        {
            if (!_started) return Task.FromResult(false);
            if (_initialSync != null && !_initialSync.IsCompleted) return Task.FromResult(false);
            if (!TryClaimScan()) return Task.FromResult(false);
            return RescanAsync();
        }

        // Testable coalescing mechanism: only one covering scan runs at a
        // time; concurrent RequestRescanAsync calls drop to false.
        internal bool TryClaimScan() => Interlocked.Exchange(ref _scanRunning, 1) == 0;
        internal void ReleaseScan() => Interlocked.Exchange(ref _scanRunning, 0);

        private async Task<bool> RescanAsync()
        {
            try
            {
                _log.Info("Gap-triggered rescan starting: " + _config.Path);
                return await ScanAndSyncAsync("Gap rescan");
            }
            finally { ReleaseScan(); }
        }

        private Task<bool> InitialSyncAsync() => ScanAndSyncAsync("Initial sync");

        private async Task<bool> ScanAndSyncAsync(string reason)
        {
            if (!_directoryExists(_config.Path)) return false;
            // No snapshot: per-file point lookups via TryGetStateAsync keep
            // startup memory flat no matter how large the state table grows.
            var sourcePaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var pendingState = new List<(string path, long size, long mtime, string? fileId, long ctime, string? hash)>();
            var stateLock = new object();
            var scanFailed = 0;
            long scanned = 0, copied = 0, skipped = 0, moved = 0;
            // dir_sync=per-batch: one dir fsync per distinct parent per
            // 500-file checkpoint instead of per file. Wired onto the shared
            // CopyAction (cast: wrappers like RecordingAction keep per-file,
            // the safe default) and cleared in finally below so the trickle
            // event path never inherits it.
            var dirBatch = _config.Sync != null
                && string.Equals(_config.Sync.DirSync, "per-batch", StringComparison.OrdinalIgnoreCase)
                ? new DirSyncBatcher() : null;
            if (_copyAction is CopyAction batchCopy) batchCopy.DirBatch = dirBatch;
            var scanStart = DateTime.UtcNow;
            // Debug only: the matching "Sync complete" line is the proof of
            // work. Gap rescans already log their own Info above; logging
            // every scan start at Info doubles that line and spams startup.
            _log.Debug(reason + " scan starting: " + _config.Path);

            void FlushState()
            {
                if (_stateDb == null || pendingState.Count == 0) return;
                lock (stateLock)
                {
                    try
                    {
                        // Crash invariant: directory entries durable BEFORE
                        // state claims the files copied. A failed dir fsync
                        // drops the batch (next run re-copies, safe) instead
                        // of upserting ahead of durability (divergence).
                        if (dirBatch != null && !dirBatch.FlushAll(_log))
                        {
                            Interlocked.Exchange(ref scanFailed, 1);
                            _log.Warn("Directory fsync failed, state batch dropped (will re-copy): " + _config.Path);
                            pendingState.Clear();
                            return;
                        }
                        _stateDb.UpsertBatch(pendingState);
                        pendingState.Clear();
                    }
                    catch (Exception ex) { _log.Debug("StateDb batch upsert failed: " + ex.Message); }
                }
            }

            try
            {
                var files = _enumerateFiles(_config.Path, SearchOption.AllDirectories)
                    .Where(f => _filter == null || _filter.ShouldProcess(f));
                var options = new ParallelOptions
                {
                    CancellationToken = _cts!.Token,
                    MaxDegreeOfParallelism = Math.Max(1, _config.Sync != null ? _config.Sync.InitialSyncWorkers : 2)
                };
                // Chunked bulk-fetch: state rows for a bounded file chunk are
                // read with a few IN-queries under one gate hold instead of one
                // point lookup per file. Memory stays bounded (chunk-sized list
                // plus dict); a failed fetch degrades to copy-everything, the
                // same decision the old per-file catch made.
                var chunk = new List<(string f, string rel)>(2000);
                foreach (var path in files)
                {
                    chunk.Add((path, PathUtil.Relative(path, _config.Path)));
                    if (chunk.Count < 2000) continue;
                    await ProcessChunkAsync(chunk, options);
                    chunk.Clear();
                }
                if (chunk.Count > 0) await ProcessChunkAsync(chunk, options);

                async Task ProcessChunkAsync(List<(string f, string rel)> batch, ParallelOptions opts)
                {
                    _cts!.Token.ThrowIfCancellationRequested();
                    var states = new Dictionary<string, (long size, long mtime)>(StringComparer.OrdinalIgnoreCase);
                    if (_stateDb != null)
                    {
                        try { states = await _stateDb.GetStatesAsync(batch.Select(x => x.rel)); }
                        catch (Exception ex) { _log.Debug("StateDb bulk lookup failed, copying: " + ex.Message); }
                    }
                    await Parallel.ForEachAsync(batch, opts, async (item, token) => await ProcessFileAsync(item, states, token));
                }

                async Task ProcessFileAsync((string f, string rel) item, Dictionary<string, (long size, long mtime)> states, CancellationToken token)
                {
                    var (f, rel) = item;
                    try
                    {
                        var n = Interlocked.Increment(ref scanned);
                        if (n % 1000 == 0)
                            _log.Debug($"{reason} progress: {n} scanned, {Interlocked.Read(ref copied)} copied, {Interlocked.Read(ref skipped)} skipped ({(DateTime.UtcNow - scanStart).TotalSeconds:F0}s): " + _config.Path);
                        if (!FileSnapshot.TryRead(f, out var sourceSnapshot))
                        {
                            Interlocked.Exchange(ref scanFailed, 1);
                            return;
                        }
                        sourcePaths.TryAdd(rel, 0);
                        var dst = Path.Combine(_config.Destination, rel);

                        if (!_driveReady(_config.Destination))
                        {
                            _log.Warn("Destination drive is not ready, initial copy blocked: " + f);
                            return;
                        }

                        var unchanged = states.TryGetValue(rel, out var s)
                            && s.size == sourceSnapshot.Length
                            && s.mtime == sourceSnapshot.LastWriteTimeUtcTicks;
                        if (unchanged && _comparer.RequiresContentRead && File.Exists(dst) && !FullVerifyDue())
                        {
                            // State hit under a content comparer: skip without
                            // re-hashing. The Exists gate stays here because the
                            // comparer below would otherwise re-read both files
                            // on every warm run; metadata comparers stat dst
                            // themselves, so they fall through and the extra
                            // Exists stat is gone on every path.
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Full-verify run under a metadata comparer: the
                        // comparer below cannot see same-size corruption, so
                        // byte-compare once (constant memory, early exit).
                        // Equal still skips; a mismatch copies below WITHOUT
                        // consulting the comparer (it would size-match and
                        // skip, hiding the corruption the verify just found).
                        var verifyFailed = unchanged && FullVerifyDue() && File.Exists(dst) && !ContentEqual(f, dst);
                        if (unchanged && FullVerifyDue() && File.Exists(dst) && !verifyFailed)
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Create-time move replay: an untracked path whose file ID
                        // matches one tracked row is a rename, not a new file.
                        if (!unchanged && _stateDb != null && FileIdentity.TryGet(f, out var moveId, out var moveCtime)
                            && moveId != null
                            && await TryReplayMoveAsync(f, rel, sourceSnapshot, moveId, moveCtime))
                        {
                            Interlocked.Increment(ref moved);
                            return;
                        }

                        if (!verifyFailed && _comparer.AreEqual(f, dst, sourceSnapshot))
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }
                        var e = new FileChangedEventArgs(ChangeType.Created, f);
                        var args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        // Pipelined upsert: the completer owns the batch add
                        // (plus the 500-file FlushState) under the same lock,
                        // so the checkpoint cadence is unchanged.
                        Func<FileSnapshot, CancellationToken, Task> batchUpsert = (snap, _) =>
                        {
                            if (_stateDb == null) return Task.CompletedTask;
                            // Lazy identity: captured after the durable rename
                            // the completer just performed, outside the gate.
                            string? fid = null;
                            long fctime = 0;
                            try
                            {
                                if (FileIdentity.TryGet(f, out var bid, out var bct)) { fid = bid; fctime = bct; }
                            }
                            catch { }
                            lock (stateLock)
                            {
                                pendingState.Add((rel, snap.Length, snap.LastWriteTimeUtcTicks, fid, fctime, null));
                                if (pendingState.Count >= 500) FlushState();
                            }
                            return Task.CompletedTask;
                        };
                        var fresh = await ExecuteCopyAsync(args,
                            CopyWithRetryAsync,
                            reason + " failed", reason + " validation FAILED", token, batchUpsert);
                        if (fresh == null) return;
                        _churn.ReportCopy(fresh.Value.Length);

                        if (!UsePipelined(batchUpsert) && _stateDb != null)
                        {
                            string? nfid = null;
                            long nfctime = 0;
                            try
                            {
                                if (FileIdentity.TryGet(f, out var nid, out var nct)) { nfid = nid; nfctime = nct; }
                            }
                            catch { }
                            lock (stateLock)
                            {
                                pendingState.Add((rel, fresh.Value.Length, fresh.Value.LastWriteTimeUtcTicks, nfid, nfctime, null));
                                if (pendingState.Count >= 500) FlushState();
                            }
                        }
                        Interlocked.Increment(ref copied);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref scanFailed, 1);
                        _log.Error(reason + " failed: " + f, ex);
                    }
                }

                FlushState();
                if (scanFailed != 0)
                {
                    _log.Warn("Initial source scan incomplete, parity cleanup blocked: " + _config.Path);
                    return false;
                }
                await WithExclusiveLockAsync(async () =>
                {
                    await _parityScanner.EnforceAsync(new HashSet<string>(sourcePaths.Keys, StringComparer.OrdinalIgnoreCase), _cts!.Token);
                    await _scrubber.RunAsync(_cts!.Token);
                });
                // A full scan just completed: arm the anomaly brake (and
                // re-arm after a ResetState disarm).
                _churnArmed = true;
                // Successful scan + parity: stamp the full-verify cadence.
                // Never fails the sync if the store is unavailable.
                if (_verifyStore != null)
                {
                    try { _verifyStore.SetLastRun(_verifyKey, DateTime.UtcNow); } catch { }
                }
                await RunRestoreVerifyAsync(_cts!.Token);
            }
            catch (UnauthorizedAccessException) { _log.Warn("Access denied scanning " + _config.Path); return false; }
            catch (PathTooLongException) { _log.Warn("Path too long scanning " + _config.Path); return false; }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { _log.Warn("Source scan incomplete, parity cleanup blocked: " + ex.Message); return false; }
            finally { if (_copyAction is CopyAction doneCopy) doneCopy.DirBatch = null; }
            _log.Info($"Sync complete: {_config.Path} ({scanned} scanned, {copied} copied, {moved} moved, {skipped} skipped, {scanned - copied - skipped - moved} failed, {(DateTime.UtcNow - scanStart).TotalSeconds:F0}s)");
            return true;
        }

        // Local leaf derivation, mirroring Program.SourceName: Pipeline.cs
        // is also linked into CrashWorker, which excludes TicTack.cs, so it
        // cannot reference Program. Same rule, same results.
        private static string SourceLeaf(SourceConfig cfg)
        {
            var name = Path.GetFileName(cfg.Path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? "default" : name;
        }

        // Test hook: backdate or clear the full-verify stamp without
        // waiting out the cadence. Production never calls this.
        internal void StampFullVerify(DateTime date)
        {
            if (_verifyStore == null) return;
            try { _verifyStore.SetLastRun(_verifyKey, date); } catch { }
        }

        // Same for the proof-by-restore stamp. Production never calls this.
        internal void StampRestoreVerify(DateTime date)
        {
            if (_verifyStore == null) return;
            try { _verifyStore.SetLastRun(_restoreKey, date); } catch { }
        }

        // Proof-by-restore: hash-compare a bounded random sample of the
        // destination against the source. Read-only — mismatches log Error
        // and re-alert on the next run (no stamp), repair is the scrub's
        // job. Skips are silent per-file; only the summary is logged.
        private async Task RunRestoreVerifyAsync(CancellationToken ct)
        {
            var days = _config.Sync != null ? _config.Sync.RestoreVerifyDays : 0;
            var count = _config.Sync != null && _config.Sync.RestoreVerifyFiles > 0 ? _config.Sync.RestoreVerifyFiles : 10;
            if (days <= 0 || _stateDb == null || _verifyStore == null) return;
            try
            {
                var last = _verifyStore.GetLastRun(_restoreKey);
                if (last != null && (DateTime.UtcNow.Date - last.Value).TotalDays < days) return;
            }
            catch { }
            if (!_directoryExists(_config.Destination))
            {
                _log.Error("Restore verify: destination unavailable, backups unverifiable: " + _config.Destination);
                return;
            }
            List<string> sample;
            try { sample = await _stateDb.GetRandomPathsAsync(count); }
            catch (Exception ex) { _log.Debug("Restore verify sample failed: " + ex.Message); return; }
            if (sample.Count == 0) return;
            int ok = 0, failed = 0, skipped = 0;
            foreach (var rel in sample)
            {
                if (ct.IsCancellationRequested) return;
                string hs, hd;
                try { hs = FileHasher.ComputeHex(PathUtil.EnsureExtended(Path.Combine(_config.Path, rel)), null); }
                catch { skipped++; continue; }
                try { hd = FileHasher.ComputeHex(PathUtil.EnsureExtended(Path.Combine(_config.Destination, rel)), null); }
                catch { skipped++; continue; }
                if (hs == hd) ok++;
                else { failed++; _log.Error("Restore verify FAILED (destination differs from source): " + rel); }
            }
            if (failed > 0)
            {
                // No stamp: re-alerts on the next run until fixed.
                _log.Error("Restore verify: " + failed + " of " + sample.Count + " sampled files differ — reruns next scan until clean");
                return;
            }
            _log.Info("Restore verify: " + ok + "/" + sample.Count + " sampled files match (" + skipped + " skipped)");
            try { _verifyStore.SetLastRun(_restoreKey, DateTime.UtcNow); } catch { }
        }

        // Full-verify cadence: due when enabled and never stamped, or the
        // stamp is at least FullVerifyDays old (date granularity). Store
        // failures fail open toward verifying rather than skipping.
        private bool FullVerifyDue()
        {
            var days = _config.Sync != null ? _config.Sync.FullVerifyDays : 0;
            if (days <= 0 || _verifyStore == null) return false;
            try
            {
                var last = _verifyStore.GetLastRun(_verifyKey);
                if (last == null) return true;
                return (DateTime.UtcNow.Date - last.Value).TotalDays >= days;
            }
            catch { return true; }
        }

        // Streaming byte comparison for full-verify under metadata
        // comparers: constant memory regardless of file size, early exit on
        // first differing block. Exceptions fail open (return false) so the
        // caller copies rather than trusting unreadable bytes.
        private static bool ContentEqual(string a, string b)
        {
            const int bufSize = 81920;
            try
            {
                using var fa = new FileStream(PathUtil.EnsureExtended(a), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufSize, FileOptions.SequentialScan);
                using var fb = new FileStream(PathUtil.EnsureExtended(b), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufSize, FileOptions.SequentialScan);
                if (fa.Length != fb.Length) return false;
                var ba = new byte[bufSize];
                var bb = new byte[bufSize];
                int ra;
                while ((ra = fa.Read(ba, 0, ba.Length)) > 0)
                {
                    var rb = 0;
                    while (rb < ra)
                    {
                        var n = fb.Read(bb, rb, ra - rb);
                        if (n == 0) return false;
                        rb += n;
                    }
                    for (var i = 0; i < ra; i++)
                        if (ba[i] != bb[i]) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // Create-time move replay: an untracked source path whose file ID
        // matches exactly one tracked row (same size+mtime, old source gone)
        // is a rename, not a new file. Replay as a destination File.Move plus
        // a state Delete+Upsert so a big rename costs one move instead of
        // delete+recopy (and instead of tripping the delete threshold).
        // Ambiguous or unsafe cases return false and the caller copies fresh.
        private async Task<bool> TryReplayMoveAsync(string f, string rel, FileSnapshot snap, string fileId, long ctime)
        {
            List<(string path, long size, long mtime)> donors;
            try { donors = await _stateDb!.FindByFileIdAsync(fileId); }
            catch (Exception ex) { _log.Debug("Move lookup failed, copying: " + rel + " (" + ex.Message + ")"); return false; }
            string? donorRel = null;
            foreach (var d in donors)
            {
                if (d.size == snap.Length && d.mtime == snap.LastWriteTimeUtcTicks) { donorRel = d.path; break; }
            }
            if (donorRel == null || string.Equals(donorRel, rel, StringComparison.OrdinalIgnoreCase)) return false;
            // Old source must be gone: still-present means a hardlink or a
            // copy sharing the ID space, not a move. Directories checked too:
            // a renamed-away file never leaves a directory behind.
            var oldSrc = Path.Combine(_config.Path, donorRel);
            try
            {
                if (File.Exists(oldSrc) || _directoryExists(oldSrc)) return false;
            }
            catch { return false; }
            var oldDst = Path.Combine(_config.Destination, donorRel);
            var newDst = Path.Combine(_config.Destination, rel);
            bool oldDstExists;
            try
            {
                oldDstExists = File.Exists(PathUtil.EnsureExtended(oldDst));
                if (File.Exists(PathUtil.EnsureExtended(newDst))) return false;
            }
            catch { return false; }
            if (!oldDstExists)
            {
                // Stale donor row (dest manually deleted): drop it so it
                // cannot skew the delete-guard denominator, copy fresh.
                try { await _stateDb!.DeleteAsync(donorRel); } catch { }
                return false;
            }
            // No versioning/archive of oldDst: the bytes move with it, and
            // the new name's history starts here.
            try
            {
                var newDir = Path.GetDirectoryName(newDst);
                if (!string.IsNullOrEmpty(newDir) && !Directory.Exists(newDir))
                    Directory.CreateDirectory(PathUtil.EnsureExtended(newDir));
                File.Move(PathUtil.EnsureExtended(oldDst), PathUtil.EnsureExtended(newDst));
            }
            catch (Exception ex) { _log.Debug("Move replay failed, copying: " + rel + " (" + ex.Message + ")"); return false; }
            try
            {
                await _stateDb!.DeleteAsync(donorRel);
                await _stateDb.UpsertAsync(rel, snap.Length, snap.LastWriteTimeUtcTicks, fileId, ctime);
            }
            catch (Exception ex) { _log.Debug("Move replay state update failed: " + rel + " (" + ex.Message + ")"); }
            _churn.ReportRename();
            _log.Info("Replayed move: " + donorRel + " -> " + rel);
            return true;
        }

        // Shared copy flow for InitialSyncAsync and ProcessEvent: version the
        // previous destination, copy, re-read the source, validate. Returns the
        // fresh snapshot on success, null on any failure (already logged with
        // the caller's labels, so log strings are unchanged by the sharing).
        private async Task<FileSnapshot?> ExecuteCopyAsync(
            FileActionArgs args,
            Func<FileActionArgs, CancellationToken, Task<ActionResult>> copy,
            string failLabel,
            string validationLabel,
            CancellationToken ct,
            Func<FileSnapshot, CancellationToken, Task>? pipelinedUpsert = null)
        {
            if (_churn.IsHeld())
            {
                _log.Debug("Churn hold active, skipping copy: " + args.ChangeEvent.FullPath);
                return null;
            }
            var src = args.ChangeEvent.FullPath;
            var dst = args.DestPath;
            try
            {
                var archiveResult = await _versioning.ArchivePreviousVersionAsync(dst, ct);
                if (!archiveResult.Success)
                {
                    _log.Error("Versioning failed: " + dst + ": " + archiveResult.ErrorMessage);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Versioning failed: " + dst, ex);
                return null;
            }

            // Pipelined mode: copy on this worker, complete (flush -> rename
            // -> validate -> upsert) on the completer thread. The upsert
            // delegate already ran when fresh returns non-null, so callers
            // skip their own upsert via UsePipelined below.
            if (UsePipelined(pipelinedUpsert))
                return await ExecutePipelinedAsync(args, pipelinedUpsert!, failLabel, validationLabel, ct);

            ActionResult result;
            try
            {
                result = await copy(args, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Retryable failures surface here after the retry budget is
                // exhausted; keep the caller's original log label.
                _log.Error(failLabel + ": " + src + ": " + ex.Message);
                return null;
            }
            if (!result.Success)
            {
                _log.Error(failLabel + ": " + src + ": " + result.ErrorMessage);
                return null;
            }

            if (!FileSnapshot.TryRead(src, out var freshSnapshot))
            {
                _log.Error(validationLabel + ": source disappeared: " + src);
                return null;
            }
            // Fast path: the copy loop already hashed the source bytes. Trusted only
            // when the source is byte-identical to the pre-copy snapshot (mid-copy
            // changes force fresh != pre and fall back to the double-read below).
            bool valid;
            if (result.SourceHash != null && args.SourceSnapshot.HasValue
                && freshSnapshot.Equals(args.SourceSnapshot.Value)
                && _validator is HashValidator hashValidator)
            {
                valid = await hashValidator.ValidateWithSourceHashAsync(dst, result.SourceHash, freshSnapshot);
            }
            else
            {
                valid = await _validator.ValidateAsync(src, dst, freshSnapshot);
            }
            if (!valid)
            {
                _log.Error(validationLabel + ": " + src + " -> " + dst);
                return null;
            }
            return freshSnapshot;
        }

        private Task<ActionResult> CopyWithRetryAsync(FileActionArgs args, CancellationToken ct) =>
            _retry.ExecuteAsync(async () =>
            {
                var copyResult = await _copyAction.ExecuteAsync(args, ct);
                if (!copyResult.Success && copyResult.Retryable)
                    throw new IOException(copyResult.ErrorMessage);
                return copyResult;
            }, ct);

        private bool UsePipelined(Func<FileSnapshot, CancellationToken, Task>? upsert) =>
            upsert != null && _completer != null && _copyAction is CopyAction;

        private async Task<FileSnapshot?> ExecutePipelinedAsync(
            FileActionArgs args,
            Func<FileSnapshot, CancellationToken, Task> upsert,
            string failLabel,
            string validationLabel,
            CancellationToken ct)
        {
            var src = args.ChangeEvent.FullPath;
            TempCopyResult tmp;
            try
            {
                tmp = await CopyTempWithRetryAsync(args, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error(failLabel + ": " + src + ": " + ex.Message);
                return null;
            }
            if (!tmp.Success)
            {
                _log.Error(failLabel + ": " + src + ": " + tmp.ErrorMessage);
                return null;
            }
            try
            {
                return await _completer!.EnqueueAsync(args, tmp, failLabel, validationLabel, upsert, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error(failLabel + ": " + src + ": " + ex.Message);
                return null;
            }
        }

        private Task<TempCopyResult> CopyTempWithRetryAsync(FileActionArgs args, CancellationToken ct) =>
            _retry.ExecuteAsync(async () =>
            {
                var tmp = await ((CopyAction)_copyAction).CopyToTempAsync(args, ct);
                if (!tmp.Success && tmp.Retryable)
                    throw new IOException(tmp.ErrorMessage);
                return tmp;
            }, ct);

        private async Task WaitForSignal(CancellationToken token)
        {
            try { await _signal.WaitAsync(token); }
            catch (OperationCanceledException) { }
        }

        private async Task ProcessEvent(FileChangedEventArgs e, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_filter != null && !_filter.ShouldProcess(e.FullPath)) return;
                if (!_driveReady(_config.Destination))
                {
                    _log.Warn("Destination drive is not ready, event blocked: " + e.FullPath);
                    return;
                }

                var args = new FileActionArgs(e, _config.Path, _config.Destination);

                switch (e.ChangeType)
                {
                    case ChangeType.Deleted:
                        if (!_directoryExists(_config.Path))
                        {
                            _log.Warn("Source folder missing, deletion blocked: " + e.FullPath);
                            break;
                        }
                        var delRel = PathUtil.Relative(e.FullPath, _config.Path);
                        long size = 0;
                        if (_stateDb != null)
                        {
                            try { size = await _stateDb.GetSizeAsync(delRel) ?? 0; }
                            catch (Exception ex) { _log.Warn("StateDb size lookup failed, using destination size: " + ex.Message); }
                        }
                        _deletions.Add(new DeletionGuard.PendingDeletion { Path = e.FullPath, DestPath = args.DestPath, SizeBytes = size });
                        break;

                    case ChangeType.Renamed:
                        await _deletions.FlushAsync(ct);
                        if (args.OldDestPath != null && (File.Exists(args.OldDestPath) || _directoryExists(args.OldDestPath)))
                        {
                            await _retry.ExecuteAsync(async () =>
                            {
                                var renameResult = await _renameAction.ExecuteAsync(args, ct);
                                if (!renameResult.Success && renameResult.Retryable)
                                    throw new IOException(renameResult.ErrorMessage);
                                return renameResult;
                            }, ct);
                        }
                        if (_stateDb != null)
                        {
                            var oldRel = PathUtil.Relative(e.OldFullPath!, _config.Path);
                            var newRel = PathUtil.Relative(e.FullPath, _config.Path);
                            if (_directoryExists(e.FullPath))
                            {
                                await _stateDb.DeleteAsync(oldRel);
                                await _stateDb.UpdatePrefixAsync(oldRel, newRel);
                            }
                            else
                            {
                                await _stateDb.DeleteAsync(oldRel);
                                try
                                {
                                    if (File.Exists(e.FullPath))
                                    {
                                        var fi = new FileInfo(e.FullPath);
                                        await _stateDb.UpsertAsync(newRel, fi.Length, fi.LastWriteTimeUtc.Ticks);
                                        // Identity follows the rename: capture
                                        // lazily, never fail the rename for it.
                                        try
                                        {
                                            if (FileIdentity.TryGet(e.FullPath, out var rid, out var rctime) && rid != null)
                                                await _stateDb.UpdateIdentityAsync(newRel, rid, rctime);
                                        }
                                        catch (Exception ex) { _log.Debug("Rename identity capture skipped: " + ex.Message); }
                                    }
                                }
                                catch (FileNotFoundException) { }
                                catch (DirectoryNotFoundException) { }
                            }
                        }
                        _log.Debug("Renamed: " + e.OldFullPath + " -> " + e.FullPath);
                        break;

                    case ChangeType.Created:
                    case ChangeType.Modified:
                        await _deletions.FlushAsync(ct);
                        if (!File.Exists(e.FullPath))
                        {
                            if (_directoryExists(e.FullPath))
                                return;
                            if (File.Exists(args.DestPath))
                            {
                                // Route through TrackedDeleter so the state row is
                                // removed too; a stale row inflates CountAsync and
                                // skews the percent delete guard's denominator.
                                await _deleter.DeleteAsync(e.FullPath, args.DestPath, PathUtil.Relative(e.FullPath, _config.Path), "Deletion failed", ct);
                            }
                            return;
                        }
                        if (!FileSnapshot.TryRead(e.FullPath, out var sourceSnapshot))
                        {
                            _log.Warn("Source unreadable, copy skipped: " + e.FullPath);
                            return;
                        }
                        args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        // Event-time move replay: same create-time rule as the
                        // scan path, before the comparer burns two full reads.
                        if (_stateDb != null && !File.Exists(args.DestPath)
                            && FileIdentity.TryGet(e.FullPath, out var emoveId, out var emoveCtime) && emoveId != null
                            && await TryReplayMoveAsync(e.FullPath, PathUtil.Relative(e.FullPath, _config.Path), sourceSnapshot, emoveId, emoveCtime))
                            return;
                        if (_comparer.AreEqual(e.FullPath, args.DestPath, sourceSnapshot)) return;

                        // Pipelined upsert: mirrors the single-row update
                        // below; the completer runs it after durable rename.
                        Func<FileSnapshot, CancellationToken, Task> singleUpsert = async (snap, _) =>
                        {
                            if (_stateDb != null)
                            {
                                try
                                {
                                    var rel = PathUtil.Relative(e.FullPath, _config.Path);
                                    string? fid = null;
                                    long fctime = 0;
                                    try
                                    {
                                        if (FileIdentity.TryGet(e.FullPath, out var sid, out var sct)) { fid = sid; fctime = sct; }
                                    }
                                    catch { }
                                    await _stateDb.UpsertAsync(rel, snap.Length, snap.LastWriteTimeUtcTicks, fid, fctime);
                                }
                                catch (Exception ex) { _log.Debug("StateDb update skipped: " + ex.Message); }
                            }
                        };
                        var fresh = await ExecuteCopyAsync(args,
                            CopyWithRetryAsync,
                            "Copy failed", "Validation FAILED", ct, singleUpsert);
                        if (fresh == null) return;
                        _churn.ReportCopy(fresh.Value.Length);
                        var freshSnapshot = fresh.Value;
                        if (!UsePipelined(singleUpsert) && _stateDb != null)
                        {
                            try
                            {
                                var rel = PathUtil.Relative(e.FullPath, _config.Path);
                                string? fid = null;
                                long fctime = 0;
                                try
                                {
                                    if (FileIdentity.TryGet(e.FullPath, out var eid, out var ect)) { fid = eid; fctime = ect; }
                                }
                                catch { }
                                await _stateDb.UpsertAsync(rel, freshSnapshot.Length, freshSnapshot.LastWriteTimeUtcTicks, fid, fctime);
                            }
                            catch (Exception ex) { _log.Debug("StateDb update skipped: " + ex.Message); }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("Processing " + e.FullPath, ex);
            }
        }

        public async Task StopAsync()
        {
            if (_disposed) return;
            _started = false;
            if (_cts != null) _cts.Cancel();
            _monitor.Changed -= OnChanged;
            if (_monitorErrorHandler != null)
            {
                _monitor.Error -= _monitorErrorHandler;
                _monitorErrorHandler = null;
            }
            _monitor.Stop();
            _startRetryTimer?.Dispose();
            _deferredCheckTimer?.Dispose();
            _parityTimer?.Dispose();

            var tasks = new List<Task>();
            if (_processor != null) tasks.Add(_processor);
            if (_initialSync != null) tasks.Add(_initialSync);
            // Abandon queued completions first: orphan tmps go to PowerGuard,
            // and outstanding funnels release via the cancellation above.
            // Completed-then-upserted items keep the crash invariant even here.
            _completer?.Complete();
            if (_completer != null) tasks.Add(_completer.LoopTask);
            lock (_taskLock) tasks.AddRange(_deferredTasks);
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error("Pipeline shutdown worker failed", ex); }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public string SourcePath => _config.Path;

        // Watchdog probe (stall-while-alive): null = healthy, otherwise a
        // one-line fault for the log + alert_path. Idle (empty queue) is
        // healthy — zero-CPU idle means silence, not stall. Debounced-only
        // backlog is healthy too: nothing is overdue until its time comes.
        // Pure logic lives in EvaluateHealth (directly testable); this
        // gathers live state.
        public string? CheckHealth(TimeSpan stallAfter)
        {
            if (_disposed || _cts == null) return null;
            Exception? processorError = null;
            if (_processor != null && _processor.IsFaulted)
                processorError = _processor.Exception;
            return EvaluateHealth(_pendingEvents.Count, _lastProgressUtc,
                DateTime.UtcNow, stallAfter, processorError);
        }

        internal static string? EvaluateHealth(int pendingCount, DateTime lastProgressUtc,
            DateTime now, TimeSpan stallAfter, Exception? processorError)
        {
            if (processorError != null)
                return "processor task faulted: " + processorError.GetBaseException().Message;
            if (pendingCount > 0 && now - lastProgressUtc > stallAfter)
                return "no progress for " + (now - lastProgressUtc).TotalMinutes.ToString("F0")
                    + " min with " + pendingCount + " queued events";
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            if (_startRetryTimer != null) _startRetryTimer.Dispose();
            if (_deferredCheckTimer != null) _deferredCheckTimer.Dispose();
            if (_parityTimer != null) _parityTimer.Dispose();
            if (_cts != null) _cts.Dispose();
            _signal.Dispose();
            _queueCounter?.Dispose();
            _monitor.Dispose();
            if (_stateDb != null) _stateDb.Dispose();
            if (_verifyStore != null) _verifyStore.Dispose();
            _deferred.Dispose();
            if (_lock != null) _lock.Dispose();
        }

        private void QueueParityCheck()
        {
            if (_disposed || _cts == null || _cts.IsCancellationRequested) return;
            _parityRequested = true;
            _signal.Release();
        }

        // One-shot deadline timer: fires once at the next hold expiry or
        // due daily warning, then re-arms. Nothing pending means disarmed —
        // zero wake-ups, zero scans, zero disk writes while waiting.
        private void ArmDeferredCheckTimer()
        {
            lock (_taskLock)
            {
                if (_disposed || _cts == null || _cts.IsCancellationRequested) return;
                DateTime? due;
                try { due = _deferred.NextDueUtc(); }
                catch { return; }
                if (due == null)
                {
                    try { _deferredCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                    return;
                }
                var delay = due.Value - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (_deferredCheckTimer == null)
                    _deferredCheckTimer = new Timer(_ => CheckDeferredDeletions(), null, delay, Timeout.InfiniteTimeSpan);
                else
                {
                    try { _deferredCheckTimer.Change(delay, Timeout.InfiniteTimeSpan); } catch { }
                }
            }
        }

        private void CheckDeferredDeletions()
        {
            lock (_taskLock)
            {
                if (_disposed || _cts == null || _cts.IsCancellationRequested) return;
                var task = Task.Run(async () =>
                {
                    try { await CheckDeferredDeletionsAsync(); }
                    catch (Exception ex) { _log.Error("Deferred deletion check failed", ex); }
                    finally { ArmDeferredCheckTimer(); }
                });
                _deferredTasks.Add(task);
            }
        }

        // Destructive sweeps (parity deletes, deferred flush) run under a
        // brief exclusive lock: drop our shared roster, take exclusive
        // fail-fast (no retry — the next cycle retries), work, then fall
        // back to shared. Skips the sweep when exclusivity is unavailable.
        private async Task WithExclusiveLockAsync(Func<Task> work)
        {
            var path = Path.Combine(_config.Destination, ".tictack.lock");
            try
            {
                _lock?.Dispose();
                _lock = null;
                _lock = new SrcLock(path, _log, null, mode: LockMode.Exclusive);
                if (!_lock.IsHeld)
                {
                    _log.Warn("Exclusive lock unavailable, skipping destructive sweep");
                    return;
                }
                await work();
            }
            finally
            {
                _lock?.Dispose();
                _lock = null;
                _lock = new SrcLock(path, _log, _lockTimeout, mode: LockMode.Shared);
                if (!_lock.IsHeld)
                {
                    _log.Error("Shared lock re-acquire failed after exclusive sweep");
                    _lock.Dispose();
                    _lock = null;
                }
            }
        }

        // Expiry drains in bounded 500-path chunks so a 2M-file hold never
        // materializes a second in-memory list; warnings are metadata-only.
        private async Task CheckDeferredDeletionsAsync()
        {
            _deferred.CheckWarnings();
            if (_churn.IsHeld())
            {
                _log.Debug("Churn hold active, deferred drain paused");
                return;
            }
            var total = 0;
            var cancelled = 0;
            await WithExclusiveLockAsync(async () =>
            {
                while (true)
                {
                    var chunk = _deferred.TakeExpiredChunk(500);
                    cancelled += chunk.Cancelled;
                    if (chunk.Files.Count == 0) break;
                    total += chunk.Files.Count;
                    foreach (var f in chunk.Files)
                    {
                        var rel = PathUtil.Relative(f, _config.Path);
                        var destPath = Path.Combine(_config.Destination, rel);
                        await _deleter.DeleteAsync(f, destPath, rel, "Deferred deletion failed", CancellationToken.None);
                    }
                }
            });
            if (total > 0)
                _log.Warn("Deferred deletion: proceeding with " + total + " files");
            else if (cancelled > 0)
                _log.Info("Deferred deletion: cancelled, files reappeared");
        }

    }
}
