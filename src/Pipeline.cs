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

        private readonly ConcurrentQueue<FileChangedEventArgs> _queue = new ConcurrentQueue<FileChangedEventArgs>();
        private readonly ConcurrentDictionary<string, DateTime> _debounce = new ConcurrentDictionary<string, DateTime>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private CancellationTokenSource? _cts;
        private Task? _processor;
        private Timer? _startRetryTimer;
        private Timer? _deferredCheckTimer;
        private SrcLock? _lock;
        private readonly StateDb? _stateDb;
        private readonly DeferredDeletion _deferred;
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
            string? deferredPath = null)
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

        static bool IsDriveReady(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return true;
                return DriveInfo.GetDrives().Any(d =>
                    d.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase) && d.IsReady);
            }
            catch { return true; }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            if (!Directory.Exists(_config.Path))
            {
                _log.Warn("Source directory not found, will retry: " + _config.Path);
                _startRetryTimer = new Timer(_ => RetryStart(), null, 10000, 10000);
                return;
            }
            if (!IsDriveReady(_config.Destination))
            {
                _log.Warn("Destination drive not ready: " + _config.Destination + ", will retry");
                _startRetryTimer = new Timer(_ => RetryStart(), null, 10000, 10000);
                return;
            }
            DoStart();
        }

        void DoStart()
        {
            var lockTimeout = _config.Sync != null && _config.Sync.LockHandling == "retry"
                ? (TimeSpan?)TimeSpan.FromMinutes(_config.Sync.RetryLockMinutes > 0 ? _config.Sync.RetryLockMinutes : 10)
                : null;
            try { Directory.CreateDirectory(_config.Destination); } catch { }
            _lock = new SrcLock(Path.Combine(_config.Destination, ".tictack.lock"), _log, lockTimeout);
            _monitor.Changed += OnChanged;
            _monitor.Error += (s, e) => _log.Error("Monitor error", e.Exception);
            _monitor.Start();
            _processor = Task.Run(() => ProcessLoop());
            Task.Run(() => InitialSync());
            _deferredCheckTimer = new Timer(_ => CheckDeferredDeletions(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            _parityTimer = new Timer(_ => EnforceParity(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
            _log.Debug("Started: " + _config.Path + " -> " + _config.Destination);
        }

        void RetryStart()
        {
            if (_cts!.IsCancellationRequested) return;
            if (!Directory.Exists(_config.Path)) return;
            if (!IsDriveReady(_config.Destination))
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
            _queue.Enqueue(e);
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }

        private async Task ProcessLoop()
        {
            var token = _cts!.Token;
            while (!token.IsCancellationRequested)
            {
                FileChangedEventArgs? e;
                if (_queue.TryDequeue(out e))
                {
                    DateTime until;
                    if (_debounce.TryGetValue(e.FullPath, out until) && DateTime.UtcNow < until)
                    {
                        _queue.Enqueue(e);
                        try { _signal.Release(); } catch (SemaphoreFullException) { }
                        try { await Task.Delay(until - DateTime.UtcNow, token); } catch (OperationCanceledException) { }
                        continue;
                    }
                    DateTime removed;
                    _debounce.TryRemove(e.FullPath, out removed);
                    await ProcessEvent(e, token);
                }
                else
                {
                    SweepDebounced();
                    await FlushPendingDeletions(token);
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
                        _queue.Enqueue(new FileChangedEventArgs(ChangeType.Modified, path));
                    else if (Directory.Exists(Path.GetDirectoryName(path)))
                        _queue.Enqueue(new FileChangedEventArgs(ChangeType.Deleted, path));
                    else
                        continue;
                    try { _signal.Release(); } catch (SemaphoreFullException) { }
                }
            }
        }

        private void InitialSync()
        {
            if (!Directory.Exists(_config.Path)) return;
            var cache = _stateDb?.LoadAll();
            var sourcePaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var pendingState = new List<(string path, long size, long mtime)>();
            var stateLock = new object();

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
                var files = Directory.EnumerateFiles(_config.Path, "*", SearchOption.AllDirectories)
                    .Where(f => _filter == null || _filter.ShouldProcess(f))
                    .ToList();
                var options = new ParallelOptions
                {
                    CancellationToken = _cts!.Token,
                    MaxDegreeOfParallelism = Math.Max(1, _config.Sync != null ? _config.Sync.InitialSyncWorkers : 2)
                };
                Parallel.ForEach(files, options, f =>
                {
                    try
                    {
                        if (!FileSnapshot.TryRead(f, out var sourceSnapshot)) return;
                        var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                        sourcePaths.TryAdd(rel, 0);
                        var dst = Path.Combine(_config.Destination, rel);

                        if (cache != null && cache.TryGetValue(rel, out var s)
                            && s.size == sourceSnapshot.Length && s.mtime == sourceSnapshot.LastWriteTimeUtcTicks)
                            return;

                        if (_comparer.AreEqual(f, dst, sourceSnapshot)) return;
                        var e = new FileChangedEventArgs(ChangeType.Created, f);
                        var args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        try
                        {
                            _versioning.ArchivePreviousVersionAsync(dst, _cts!.Token).GetAwaiter().GetResult();
                        }
                        catch { }

                        ActionResult result;
                        result = _copyAction.ExecuteAsync(args, _cts!.Token).GetAwaiter().GetResult();
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
                        var valid = _validator.ValidateAsync(f, dst, freshSnapshot).GetAwaiter().GetResult();
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
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log.Error("Initial sync failed: " + f, ex);
                    }
                });

                FlushState();
                EnforceParity(new HashSet<string>(sourcePaths.Keys, StringComparer.OrdinalIgnoreCase));
            }
            catch (UnauthorizedAccessException) { _log.Warn("Access denied scanning " + _config.Path); }
            catch (PathTooLongException) { _log.Warn("Path too long scanning " + _config.Path); }
            catch (OperationCanceledException) { return; }
            _log.Info("Sync complete: " + _config.Path);
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

                var args = new FileActionArgs(e, _config.Path, _config.Destination);

                switch (e.ChangeType)
                {
                    case ChangeType.Deleted:
                        if (!Directory.Exists(_config.Path))
                        {
                            _log.Warn("Source folder missing, deletion blocked: " + e.FullPath);
                            break;
                        }
                        var delRel = e.FullPath.Substring(_config.Path.Length).TrimStart('\\', '/');
                        long size = 0;
                        if (_stateDb != null)
                        {
                            try { size = _stateDb.GetSize(delRel) ?? 0; } catch { }
                        }
                        _pendingDeletions.Add(new PendingDeletion { Path = e.FullPath, DestPath = args.DestPath, SizeBytes = size });
                        break;

                    case ChangeType.Renamed:
                        await FlushPendingDeletions(ct);
                        if (args.OldDestPath != null && (File.Exists(args.OldDestPath) || Directory.Exists(args.OldDestPath)))
                            await _retry.ExecuteAsync(() => _renameAction.ExecuteAsync(args, ct), ct);
                        if (_stateDb != null)
                        {
                            var oldRel = e.OldFullPath!.Substring(_config.Path.Length).TrimStart('\\', '/');
                            var newRel = e.FullPath.Substring(_config.Path.Length).TrimStart('\\', '/');
                            if (Directory.Exists(e.FullPath))
                            {
                                _stateDb.Delete(oldRel);
                                _stateDb.UpdatePrefix(oldRel, newRel);
                            }
                            else
                            {
                                _stateDb.Delete(oldRel);
                                try
                                {
                                    if (File.Exists(e.FullPath))
                                    {
                                        var fi = new FileInfo(e.FullPath);
                                        _stateDb.Upsert(newRel, fi.Length, fi.LastWriteTimeUtc.Ticks);
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
                            if (Directory.Exists(e.FullPath))
                                return;
                            if (File.Exists(args.DestPath))
                                await _deletion.HandleDeletionAsync(e.FullPath, args.DestPath, ct);
                            return;
                        }
                        if (!FileSnapshot.TryRead(e.FullPath, out var sourceSnapshot)) return;
                        args = new FileActionArgs(e, _config.Path, _config.Destination, sourceSnapshot);
                        if (_comparer.AreEqual(e.FullPath, args.DestPath, sourceSnapshot)) return;

                        await _versioning.ArchivePreviousVersionAsync(args.DestPath, ct);

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
                                _stateDb.Upsert(rel, freshSnapshot.Length, freshSnapshot.LastWriteTimeUtcTicks);
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

        public void Stop()
        {
            if (_disposed) return;
            if (_cts != null) _cts.Cancel();
            _monitor.Changed -= OnChanged;
            _monitor.Stop();
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
                try { stateCount = _stateDb.Count(); }
                catch { _log.Debug("StateDb count failed"); }
            }
            bool blocked = false;
            string? reason = null;

            if (_config.Sync != null && _config.Sync.DeleteThresholdCount > 0 && totalCount >= _config.Sync.DeleteThresholdCount)
            {
                blocked = true;
                reason = totalCount + " deletions >= threshold " + _config.Sync.DeleteThresholdCount;
            }
            else if (_config.Sync != null && _config.Sync.DeleteThresholdSizeGb.HasValue && _config.Sync.DeleteThresholdSizeGb.Value > 0)
            {
                var thresholdBytes = _config.Sync.DeleteThresholdSizeGb.Value * 1024L * 1024L * 1024L;
                if (totalSize >= thresholdBytes)
                {
                    blocked = true;
                    reason = (totalSize / (1024L * 1024L)) + " MB deleted >= threshold " + _config.Sync.DeleteThresholdSizeGb.Value + " GB";
                }
            }
            else if (_config.Sync != null && _config.Sync.DeleteThresholdPercent > 0 && stateCount > 50)
            {
                var percent = (double)totalCount / stateCount * 100;
                if (percent >= _config.Sync.DeleteThresholdPercent)
                {
                    blocked = true;
                    reason = percent.ToString("F1") + "% of " + stateCount + " files >= " + _config.Sync.DeleteThresholdPercent + "%";
                }
            }

            if (blocked)
            {
                _log.Error("Delete guard: " + reason + ". Deferring.");
                var files = batch.ConvertAll(d => d.Path);
                _deferred.RecordPending(files, totalSize);
                return;
            }

            foreach (var d in batch)
            {
                await _deletion.HandleDeletionAsync(d.Path, d.DestPath, ct);
                if (_stateDb != null)
                {
                    try
                    {
                        var rel = d.Path.Substring(_config.Path.Length).TrimStart('\\', '/');
                        _stateDb.Delete(rel);
                    }
                    catch (Exception ex) { _log.Debug("StateDb delete failed: " + ex.Message); }
                }
            }
            _log.Debug("Deleted: " + batch.Count + " files");
        }

        void EnforceParity()
        {
            if (_disposed) return;
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_config.Path))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(_config.Path, "*", SearchOption.AllDirectories))
                    {
                        if (_cts!.IsCancellationRequested) return;
                        if (_filter != null && !_filter.ShouldProcess(f)) continue;
                        var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                        sourcePaths.Add(rel);
                    }
                }
                catch (UnauthorizedAccessException) { _log.Warn("Access denied scanning " + _config.Path); }
            }
            EnforceParity(sourcePaths);
        }

        static bool UnderDir(string path, string prefix)
        {
            return path.StartsWith(prefix + '/', StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + '\\', StringComparison.OrdinalIgnoreCase);
        }

        void EnforceParity(HashSet<string> sourcePaths)
        {
            if (_disposed || !Directory.Exists(_config.Destination)) return;

            var staleFiles = new List<(string path, string rel)>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(_config.Destination, "*", SearchOption.AllDirectories))
                {
                    if (_cts!.IsCancellationRequested) return;
                    var name = Path.GetFileName(f);
                    if (name == ".tictack.lock" || name == ".tictack-deferred.json") continue;
                    var rel = f.Substring(_config.Destination.Length).TrimStart('\\', '/');
                    if (UnderDir(rel, ".archive") || UnderDir(rel, ".versions")) continue;
                    if (sourcePaths.Contains(rel)) continue;
                    staleFiles.Add((f, rel));
                }
            }
            catch (UnauthorizedAccessException) { _log.Warn("Access denied scanning " + _config.Destination); }

            if (staleFiles.Count > 0)
            {
                foreach (var sf in staleFiles)
                {
                    _deletion.HandleDeletionAsync(null, sf.path, CancellationToken.None).GetAwaiter().GetResult();
                    if (_stateDb != null)
                    {
                        try { _stateDb.Delete(sf.rel); }
                        catch (Exception ex) { _log.Debug("StateDb parity delete failed: " + ex.Message); }
                    }
                }
                _log.Info("Cleanup: archived " + staleFiles.Count + " stale files");
            }

            foreach (var dir in Directory.EnumerateDirectories(_config.Destination, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length))
            {
                if (_cts!.IsCancellationRequested) return;
                var rel = dir.Substring(_config.Destination.Length).TrimStart('\\', '/');
                if (rel == ".archive" || UnderDir(rel, ".archive")) continue;
                if (rel == ".versions" || UnderDir(rel, ".versions")) continue;
                if (rel == ".tictack.lock") continue;
                if (sourcePaths.Any(f => UnderDir(f, rel))) continue;
                _log.Info("Cleanup: removing stale dir " + dir);
                _deletion.HandleDeletionAsync(null, dir, CancellationToken.None).GetAwaiter().GetResult();
                if (_stateDb != null)
                {
                    try { _stateDb.Delete(rel); }
                    catch (Exception ex) { _log.Debug("StateDb parity dir delete failed: " + ex.Message); }
                }
            }
        }

        private void CheckDeferredDeletions()
        {
            if (_deferred == null || !_deferred.HasPending) return;

            var action = _deferred.Check();
            if (action.Type == DeferredActionType.Proceed)
            {
                _log.Warn("Deferred deletion: proceeding with " + (action.Files?.Count ?? 0) + " files");
                Task.Run(async () =>
                {
                    foreach (var f in action.Files!)
                    {
                        var destPath = Path.Combine(_config.Destination, f.Substring(_config.Path.Length).TrimStart('\\', '/'));
                        await _deletion.HandleDeletionAsync(f, destPath, CancellationToken.None);
                        if (_stateDb != null)
                        {
                            try
                            {
                                var rel = f.Substring(_config.Path.Length).TrimStart('\\', '/');
                                _stateDb.Delete(rel);
                            }
                            catch (Exception ex) { _log.Debug("StateDb deferred delete failed: " + ex.Message); }
                        }
                    }
                });
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
