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

        private readonly ConcurrentDictionary<string, FileChangedEventArgs> _pendingEvents = new ConcurrentDictionary<string, FileChangedEventArgs>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _debounce = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private CancellationTokenSource? _cts;
        private Task? _processor;
        private Task<bool>? _initialSync;
        private readonly List<Task> _deferredTasks = new List<Task>();
        private readonly List<Task> _backgroundTasks = new List<Task>();
        private readonly object _taskLock = new object();
        private Timer? _startRetryTimer;
        private Timer? _deferredCheckTimer;
        private SrcLock? _lock;
        private readonly StateDb? _stateDb;
        private readonly DeferredDeletion _deferred;
        private readonly Func<string, bool> _directoryExists;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateFiles;
        private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateDirectories;
        private readonly Func<string, bool> _driveReady;
        private readonly List<PendingDeletion> _pendingDeletions = new List<PendingDeletion>();
        private Timer? _parityTimer;
        private bool _disposed;

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
            string? deferredPath = null,
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
            _driveReady = driveReady ?? IsDriveReady;
            _queueCounter = TicTackEventSource.Log.RegisterQueueCounter(() => _pendingEvents.Count);

            var holdDays = _config.Sync != null && _config.Sync.DeleteHoldDays > 0 ? _config.Sync.DeleteHoldDays : 7;
            _deferred = new DeferredDeletion(deferredPath ?? Path.Combine(_config.Destination, ".tictack-deferred.json"), holdDays, _log);

            var filters = new List<IFileFilter>();
            if (_config.Filter != null && _config.Filter.Exclude != null && _config.Filter.Exclude.Count > 0)
                filters.Add(new PatternFilter(_config.Filter.Exclude.ToArray()));
            if (autoExcludePrefixes != null && autoExcludePrefixes.Length > 0)
                filters.Add(new PathPrefixFilter(autoExcludePrefixes));
            long? maxSize = _config.Filter != null ? Config.ParseFileSizeLimit(_config.Filter.MaxFileSizeMb) : null;
            if (maxSize.HasValue && maxSize.Value > 0)
                filters.Add(new SizeFilter(maxSize.Value));
            _filter = filters.Count > 0 ? new CompositeFilter(filters) : null;
        }

        internal static bool IsDriveReady(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var mounts = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && fullPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.RootDirectory.FullName.Length)
                    .ToList();
                if (mounts.Count == 0) return false;
                if (OperatingSystem.IsLinux() && mounts[0].RootDirectory.FullName == "/"
                    && (fullPath == "/srv" || fullPath.StartsWith("/srv/", StringComparison.Ordinal)
                        || fullPath == "/mnt" || fullPath.StartsWith("/mnt/", StringComparison.Ordinal)
                        || fullPath == "/media" || fullPath.StartsWith("/media/", StringComparison.Ordinal)
                        || fullPath.StartsWith("/run/media/", StringComparison.Ordinal)))
                    return false;
                return true;
            }
            catch { return false; }
        }

        public void Start()
        {
            Start(true);
        }

        private void Start(bool startWorkers)
        {
            _cts = new CancellationTokenSource();
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
            var lockTimeout = _config.Sync != null && _config.Sync.LockHandling == "retry"
                ? (TimeSpan?)TimeSpan.FromMinutes(_config.Sync.RetryLockMinutes > 0 ? _config.Sync.RetryLockMinutes : 10)
                : null;
            try { Directory.CreateDirectory(_config.Destination); } catch { }
            _lock = new SrcLock(Path.Combine(_config.Destination, ".tictack.lock"), _log, lockTimeout);
            if (!_lock.IsHeld)
            {
                _log.Error("Could not acquire sync lock; pipeline will not start: " + _config.Destination);
                _lock.Dispose();
                _lock = null;
                _startRetryTimer = new Timer(_ => RetryStart(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                return;
            }
            _monitor.Changed += OnChanged;
            _monitor.Error += (s, e) => _log.Error("Monitor error", e.Exception);
            _monitor.Start();
            if (startWorkers) StartWorkers();
            _log.Debug("Started: " + _config.Path + " -> " + _config.Destination);
        }

        private void StartWorkers()
        {
            _initialSync = Task.Run(() => InitialSyncAsync());
            _processor = Task.Run(() => ProcessLoop());
            // Arm the hourly deferred-deletion recheck only when something is
            // actually pending (e.g. persisted from a previous run). Recording
            // new pending deletions arms it too; the check itself still runs
            // hourly and still early-returns once nothing is pending.
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
        }

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
            try { _signal.Release(); } catch (SemaphoreFullException) { }
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
                if (TryTakeReadyEvent(out e, out var wait))
                {
                    await ProcessEvent(e, token);
                }
                else if (wait.HasValue)
                {
                    try { await Task.Delay(wait.Value, token); } catch (OperationCanceledException) { }
                }
                else
                {
                    SweepDebounced();
                    await FlushPendingDeletions(token);
                    await WaitForSignal(token);
                }
            }
        }

        private bool TryTakeReadyEvent(out FileChangedEventArgs result, out TimeSpan? wait)
        {
            result = null!;
            wait = null;
            var now = DateTime.UtcNow;
            DateTime? next = null;
            foreach (var item in _pendingEvents)
            {
                if (!_debounce.TryGetValue(item.Key, out var until) || until <= now)
                {
                    if (_pendingEvents.TryRemove(item.Key, out var candidate))
                    {
                        result = candidate;
                        _debounce.TryRemove(item.Key, out _);
                        return true;
                    }
                }
                else if (!next.HasValue || until < next.Value)
                {
                    next = until;
                }
            }
            if (next.HasValue)
                wait = next.Value - now;
            return false;
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
                    try { _signal.Release(); } catch (SemaphoreFullException) { }
                }
            }
        }

        private async Task<bool> InitialSyncAsync()
        {
            if (!_directoryExists(_config.Path)) return false;
            Dictionary<string, (long size, long mtime)>? cache = null;
            if (_stateDb != null)
            {
                try { cache = await _stateDb.LoadAllAsync(); }
                catch (Exception ex) { _log.Warn("StateDB load failed, continuing without cache: " + ex.Message); }
            }
            var sourcePaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var pendingState = new List<(string path, long size, long mtime)>();
            var stateLock = new object();
            var scanFailed = 0;
            long scanned = 0, copied = 0, skipped = 0;
            var scanStart = DateTime.UtcNow;
            _log.Info("Initial sync scan starting: " + _config.Path);

            void FlushState()
            {
                if (_stateDb == null || pendingState.Count == 0) return;
                lock (stateLock)
                {
                    try
                    {
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
                await Parallel.ForEachAsync(files, options, async (f, token) =>
                {
                    try
                    {
                        var n = Interlocked.Increment(ref scanned);
                        if (n % 1000 == 0)
                            _log.Info($"Initial sync progress: {n} scanned, {Interlocked.Read(ref copied)} copied, {Interlocked.Read(ref skipped)} skipped ({(DateTime.UtcNow - scanStart).TotalSeconds:F0}s): " + _config.Path);
                        if (!FileSnapshot.TryRead(f, out var sourceSnapshot))
                        {
                            Interlocked.Exchange(ref scanFailed, 1);
                            return;
                        }
                        var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                        sourcePaths.TryAdd(rel, 0);
                        var dst = Path.Combine(_config.Destination, rel);

                        if (!_driveReady(_config.Destination))
                        {
                            _log.Warn("Destination drive is not ready, initial copy blocked: " + f);
                            return;
                        }

                        if (cache != null && File.Exists(dst) && cache.TryGetValue(rel, out var s)
                            && s.size == sourceSnapshot.Length && s.mtime == sourceSnapshot.LastWriteTimeUtcTicks)
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        if (_comparer.AreEqual(f, dst, sourceSnapshot))
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }
                        var e = new FileChangedEventArgs(ChangeType.Created, f);
                        var args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        try
                        {
                            var archiveResult = await _versioning.ArchivePreviousVersionAsync(dst, token);
                            if (!archiveResult.Success)
                            {
                                _log.Error("Versioning failed: " + dst + ": " + archiveResult.ErrorMessage);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Error("Versioning failed: " + dst, ex);
                            return;
                        }

                        ActionResult result;
                        result = await _copyAction.ExecuteAsync(args, token);
                        if (!result.Success)
                        {
                            _log.Error("Initial sync failed: " + f + ": " + result.ErrorMessage);
                            return;
                        }

                        if (!FileSnapshot.TryRead(f, out var freshSnapshot))
                        {
                            _log.Error("Initial sync validation FAILED: source disappeared: " + f);
                            return;
                        }
                        var valid = await _validator.ValidateAsync(f, dst, freshSnapshot);
                        if (!valid)
                        {
                            _log.Error("Initial sync validation FAILED: " + f + " -> " + dst);
                            return;
                        }

                        if (_stateDb != null)
                        {
                            lock (stateLock)
                            {
                                pendingState.Add((rel, freshSnapshot.Length, freshSnapshot.LastWriteTimeUtcTicks));
                                if (pendingState.Count >= 500) FlushState();
                            }
                        }
                        Interlocked.Increment(ref copied);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref scanFailed, 1);
                        _log.Error("Initial sync failed: " + f, ex);
                    }
                });

                FlushState();
                if (scanFailed != 0)
                {
                    _log.Warn("Initial source scan incomplete, parity cleanup blocked: " + _config.Path);
                    return false;
                }
                await EnforceParityAsync(new HashSet<string>(sourcePaths.Keys, StringComparer.OrdinalIgnoreCase), _cts!.Token);
            }
            catch (UnauthorizedAccessException) { _log.Warn("Access denied scanning " + _config.Path); return false; }
            catch (PathTooLongException) { _log.Warn("Path too long scanning " + _config.Path); return false; }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { _log.Warn("Source scan incomplete, parity cleanup blocked: " + ex.Message); return false; }
            _log.Info($"Sync complete: {_config.Path} ({scanned} scanned, {copied} copied, {skipped} skipped, {scanned - copied - skipped} failed, {(DateTime.UtcNow - scanStart).TotalSeconds:F0}s)");
            return true;
        }

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
                        var delRel = e.FullPath.Substring(_config.Path.Length).TrimStart('\\', '/');
                        long size = 0;
                        if (_stateDb != null)
                        {
                            try { size = await _stateDb.GetSizeAsync(delRel) ?? 0; } catch { }
                        }
                        _pendingDeletions.Add(new PendingDeletion { Path = e.FullPath, DestPath = args.DestPath, SizeBytes = size });
                        break;

                    case ChangeType.Renamed:
                        await FlushPendingDeletions(ct);
                        if (args.OldDestPath != null && (File.Exists(args.OldDestPath) || _directoryExists(args.OldDestPath)))
                            await _retry.ExecuteAsync(() => _renameAction.ExecuteAsync(args, ct), ct);
                        if (_stateDb != null)
                        {
                            var oldRel = e.OldFullPath!.Substring(_config.Path.Length).TrimStart('\\', '/');
                            var newRel = e.FullPath.Substring(_config.Path.Length).TrimStart('\\', '/');
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
                        await FlushPendingDeletions(ct);
                        if (!File.Exists(e.FullPath))
                        {
                        if (_directoryExists(e.FullPath))
                                return;
                            if (File.Exists(args.DestPath))
                            {
                                var deletionResult = await _deletion.HandleDeletionAsync(e.FullPath, args.DestPath, ct);
                                if (!deletionResult.Success)
                                    _log.Error("Deletion failed: " + args.DestPath + ": " + deletionResult.ErrorMessage);
                            }
                            return;
                        }
                        if (!FileSnapshot.TryRead(e.FullPath, out var sourceSnapshot)) return;
                        args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        if (_comparer.AreEqual(e.FullPath, args.DestPath, sourceSnapshot)) return;

                        var archiveResult = await _versioning.ArchivePreviousVersionAsync(args.DestPath, ct);
                        if (!archiveResult.Success)
                        {
                            _log.Error("Versioning failed: " + args.DestPath + ": " + archiveResult.ErrorMessage);
                            return;
                        }

                        var result = await _retry.ExecuteAsync(
                            () => _copyAction.ExecuteAsync(args, ct), ct);

                        if (!result.Success)
                        {
                            _log.Error("Copy failed: " + e.FullPath + ": " + result.ErrorMessage);
                            return;
                        }

                        if (!FileSnapshot.TryRead(e.FullPath, out var freshSnapshot))
                        {
                            _log.Error("Validation FAILED: source disappeared: " + e.FullPath);
                            return;
                        }
                        var valid = await _validator.ValidateAsync(e.FullPath, args.DestPath, freshSnapshot);
                        if (!valid)
                        {
                            _log.Error("Validation FAILED: " + e.FullPath + " -> " + args.DestPath);
                            return;
                        }
                        if (_stateDb != null)
                        {
                            try
                            {
                                var rel = e.FullPath.Substring(_config.Path.Length).TrimStart('\\', '/');
                                await _stateDb.UpsertAsync(rel, freshSnapshot.Length, freshSnapshot.LastWriteTimeUtcTicks);
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
            if (_cts != null) _cts.Cancel();
            _monitor.Changed -= OnChanged;
            _monitor.Stop();
            _startRetryTimer?.Dispose();
            _deferredCheckTimer?.Dispose();
            _parityTimer?.Dispose();

            var tasks = new List<Task>();
            if (_processor != null) tasks.Add(_processor);
            if (_initialSync != null) tasks.Add(_initialSync);
            lock (_taskLock) tasks.AddRange(_deferredTasks);
            lock (_taskLock) tasks.AddRange(_backgroundTasks);
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error("Pipeline shutdown worker failed", ex); }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
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
            if (_lock != null) _lock.Dispose();
        }

        private async Task FlushPendingDeletions(CancellationToken ct)
        {
            if (_pendingDeletions.Count == 0) return;

            var batch = new List<PendingDeletion>(_pendingDeletions);
            _pendingDeletions.Clear();

            int totalCount = batch.Count;
            long totalSize = 0;
            foreach (var d in batch) totalSize += d.SizeBytes;

            long stateCount = 0;
            if (_stateDb != null)
            {
                try { stateCount = await _stateDb.CountAsync(); }
                catch { _log.Debug("StateDb count failed"); }
            }
            bool blocked = false;
            string? reason = null;

            if (_config.Sync != null && _config.Sync.DeleteThresholdCount > 0 && totalCount >= _config.Sync.DeleteThresholdCount)
            {
                blocked = true;
                reason = totalCount + " deletions >= threshold " + _config.Sync.DeleteThresholdCount;
            }
            else if (_config.Sync != null && _config.Sync.DeleteThresholdSizeGb.HasValue && _config.Sync.DeleteThresholdSizeGb.Value > 0
                && totalSize >= _config.Sync.DeleteThresholdSizeGb.Value * 1024L * 1024L * 1024L)
            {
                blocked = true;
                reason = (totalSize / (1024L * 1024L)) + " MB deleted >= threshold " + _config.Sync.DeleteThresholdSizeGb.Value + " GB";
            }
            else if (_config.Sync != null && _config.Sync.DeleteThresholdPercent > 0 && stateCount > 50
                && (double)totalCount / stateCount * 100 >= _config.Sync.DeleteThresholdPercent)
            {
                blocked = true;
                var percent = (double)totalCount / stateCount * 100;
                reason = percent.ToString("F1") + "% of " + stateCount + " files >= " + _config.Sync.DeleteThresholdPercent + "%";
            }

            if (blocked)
            {
                _log.Error("Delete guard: " + reason + ". Deferring.");
                var files = batch.ConvertAll(d => d.Path);
                _deferred.RecordPending(files, totalSize, _config.Path);
                ArmDeferredCheckTimer();
                return;
            }

            foreach (var d in batch)
            {
                var deletionResult = await _deletion.HandleDeletionAsync(d.Path, d.DestPath, ct);
                if (!deletionResult.Success)
                {
                    _log.Error("Deletion failed: " + d.DestPath + ": " + deletionResult.ErrorMessage);
                    continue;
                }
                if (_stateDb != null)
                {
                    try
                    {
                        var rel = d.Path.Substring(_config.Path.Length).TrimStart('\\', '/');
                        await _stateDb.DeleteAsync(rel);
                    }
                    catch (Exception ex) { _log.Debug("StateDb delete failed: " + ex.Message); }
                }
            }
            _log.Debug("Deleted: " + batch.Count + " files");
        }

        private void QueueParityCheck()
        {
            var token = _cts?.Token;
            if (!token.HasValue || token.Value.IsCancellationRequested || _disposed) return;
            Task task;
            lock (_taskLock)
            {
                if (_cts == null || _cts.IsCancellationRequested || _disposed)
                    return;
                task = Task.Run(() => EnforceParityAsync(token.Value), token.Value);
                _backgroundTasks.Add(task);
            }
            _ = task.ContinueWith(t =>
            {
                lock (_taskLock) _backgroundTasks.Remove(task);
                if (t.IsFaulted && t.Exception != null)
                    _log.Error("Parity worker failed", t.Exception.GetBaseException());
            }, TaskScheduler.Default);
        }

        async Task EnforceParityAsync(CancellationToken ct)
        {
            if (_disposed) return;
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_directoryExists(_config.Path))
            {
                _log.Warn("Source folder missing, parity cleanup blocked: " + _config.Path);
                return;
            }
            try
            {
                foreach (var f in _enumerateFiles(_config.Path, SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;
                    if (_filter != null && !_filter.ShouldProcess(f)) continue;
                    var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                    sourcePaths.Add(rel);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Source scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }
            await EnforceParityAsync(sourcePaths, ct);
        }

        static bool UnderDir(string path, string prefix)
        {
            return path.StartsWith(prefix + '/', StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + '\\', StringComparison.OrdinalIgnoreCase);
        }

        async Task EnforceParityAsync(HashSet<string> sourcePaths, CancellationToken ct)
        {
            if (_disposed || !_directoryExists(_config.Destination)) return;

            var staleFiles = new List<(string path, string rel)>();
            try
            {
                foreach (var f in _enumerateFiles(_config.Destination, SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;
                    var name = Path.GetFileName(f);
                    if (name == ".tictack.lock" || name == ".tictack-deferred.json") continue;
                    var rel = f.Substring(_config.Destination.Length).TrimStart('\\', '/');
                    if (UnderDir(rel, ".archive") || UnderDir(rel, ".versions")) continue;
                    if (sourcePaths.Contains(rel)) continue;
                    staleFiles.Add((f, rel));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Destination scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }

            if (staleFiles.Count > 0)
            {
                foreach (var sf in staleFiles)
                {
                    var deletionResult = await _deletion.HandleDeletionAsync(null, sf.path, ct);
                    if (!deletionResult.Success)
                    {
                        _log.Error("Parity deletion failed: " + sf.path + ": " + deletionResult.ErrorMessage);
                        continue;
                    }
                    if (_stateDb != null)
                    {
                        try { await _stateDb.DeleteAsync(sf.rel); }
                        catch (Exception ex) { _log.Debug("StateDb parity delete failed: " + ex.Message); }
                    }
                }
                _log.Info("Cleanup: archived " + staleFiles.Count + " stale files");
            }

            var sourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in sourcePaths)
            {
                var parent = Path.GetDirectoryName(file);
                while (!string.IsNullOrEmpty(parent) && parent != ".")
                {
                    sourceDirectories.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }

            IEnumerable<string> destinationDirectories;
            try
            {
                destinationDirectories = _enumerateDirectories(_config.Destination, SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException)
            {
                _log.Warn("Destination directory scan incomplete, parity cleanup blocked: " + ex.Message);
                return;
            }
            foreach (var dir in destinationDirectories)
            {
                if (ct.IsCancellationRequested) return;
                var rel = dir.Substring(_config.Destination.Length).TrimStart('\\', '/');
                if (rel == ".archive" || UnderDir(rel, ".archive")) continue;
                if (rel == ".versions" || UnderDir(rel, ".versions")) continue;
                if (rel == ".tictack.lock") continue;
                if (sourceDirectories.Contains(rel)) continue;
                _log.Info("Cleanup: removing stale dir " + dir);
                var deletionResult = await _deletion.HandleDeletionAsync(null, dir, ct);
                if (!deletionResult.Success)
                {
                    _log.Error("Parity deletion failed: " + dir + ": " + deletionResult.ErrorMessage);
                    continue;
                }
                if (_stateDb != null)
                {
                    try { await _stateDb.DeleteAsync(rel); }
                    catch (Exception ex) { _log.Debug("StateDb parity dir delete failed: " + ex.Message); }
                }
            }
        }

        private void ArmDeferredCheckTimer()
        {
            lock (_taskLock)
            {
                _deferredCheckTimer ??= new Timer(_ => CheckDeferredDeletions(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            }
        }

        private void CheckDeferredDeletions()
        {
            if (_deferred == null || !_deferred.HasPending) return;

            var action = _deferred.Check();
            if (action.Type == DeferredActionType.Proceed)
            {
                _log.Warn("Deferred deletion: proceeding with " + (action.Files?.Count ?? 0) + " files");
                var task = Task.Run(async () =>
                {
                    foreach (var f in action.Files!)
                    {
                        var destPath = Path.Combine(_config.Destination, f.Substring(_config.Path.Length).TrimStart('\\', '/'));
                        var deletionResult = await _deletion.HandleDeletionAsync(f, destPath, CancellationToken.None);
                        if (!deletionResult.Success)
                        {
                            _log.Error("Deferred deletion failed: " + destPath + ": " + deletionResult.ErrorMessage);
                            continue;
                        }
                        if (_stateDb != null)
                        {
                            try
                            {
                                var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                                await _stateDb.DeleteAsync(rel);
                            }
                            catch (Exception ex) { _log.Debug("StateDb deferred delete failed: " + ex.Message); }
                        }
                    }
                });
                lock (_taskLock) _deferredTasks.Add(task);
            }
            else if (action.Type == DeferredActionType.Cancel)
            {
                _log.Info("Deferred deletion: cancelled, files reappeared");
            }
        }

        private class PendingDeletion
        {
            public string Path { get; set; } = string.Empty;
            public string DestPath { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
        }
    }
}
