using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // Delete-threshold guard batching, moved verbatim out of SyncPipeline.
    // Collects source-deleted paths via Add; FlushAsync evaluates the
    // count/size/percent guards and either defers (records + arms the
    // hourly recheck through onDefer) or deletes through TrackedDeleter.
    internal sealed class DeletionGuard
    {
        private readonly SourceConfig _config;
        private readonly StateDb? _stateDb;
        private readonly TrackedDeleter _deleter;
        private readonly DeferredDeletion _deferred;
        private readonly ILogger _log;
        private readonly Action _onDefer;
        private readonly List<PendingDeletion> _pendingDeletions = new List<PendingDeletion>();

        public DeletionGuard(
            SourceConfig config,
            StateDb? stateDb,
            TrackedDeleter deleter,
            DeferredDeletion deferred,
            ILogger log,
            Action onDefer)
        {
            _config = config;
            _stateDb = stateDb;
            _deleter = deleter;
            _deferred = deferred;
            _log = log;
            _onDefer = onDefer;
        }

        public void Add(PendingDeletion deletion)
        {
            _pendingDeletions.Add(deletion);
        }

        public async Task FlushAsync(CancellationToken ct)
        {
            if (_pendingDeletions.Count == 0) return;

            var batch = new List<PendingDeletion>(_pendingDeletions);
            _pendingDeletions.Clear();

            int totalCount = batch.Count;
            long totalSize = 0;
            foreach (var d in batch)
            {
                // The state lookup can be unavailable; the destination file
                // still has the size the guard needs.
                if (d.SizeBytes == 0)
                {
                    try
                    {
                        var fi = new FileInfo(PathUtil.EnsureExtended(d.DestPath));
                        if (fi.Exists) d.SizeBytes = fi.Length;
                    }
                    catch (Exception ex) { _log.Debug("Delete size probe failed for " + d.DestPath + ": " + ex.Message); }
                }
                totalSize += d.SizeBytes;
            }

            long stateCount = 0;
            var countKnown = true;
            if (_stateDb != null)
            {
                try { stateCount = await _stateDb.CountAsync(); }
                catch (Exception ex)
                {
                    countKnown = false;
                    _log.Error("StateDb count failed, percent delete guard cannot be evaluated: " + ex.Message);
                }
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
            else if (_config.Sync != null && _config.Sync.DeleteThresholdPercent > 0 && !countKnown)
            {
                // Fail closed: without a baseline the percent guard cannot be
                // evaluated, so the batch is held instead of deleted.
                blocked = true;
                reason = "StateDb count unavailable, percent guard cannot be evaluated";
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
                _deferred.RecordPending(files, _config.Path);
                _onDefer();
                return;
            }

            foreach (var d in batch)
            {
                await _deleter.DeleteAsync(d.Path, d.DestPath, PathUtil.Relative(d.Path, _config.Path), "Deletion failed", ct);
            }
            _log.Debug("Deleted: " + batch.Count + " files");
        }

        public sealed class PendingDeletion
        {
            public string Path { get; set; } = string.Empty;
            public string DestPath { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
        }
    }
}
