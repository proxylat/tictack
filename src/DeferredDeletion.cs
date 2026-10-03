using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace TicTack
{
    // Source-generated JSON metadata: only used for the one-time migration
    // of pre-SQLite batch files. No runtime reflection, AOT/trimming safe.
    [JsonSerializable(typeof(DeferredDeletion.DeferredState))]
    internal sealed partial class DeferredDeletionJsonContext : JsonSerializerContext
    {
    }

    // SQLite-backed deferred deletions: every blocked batch gets its own
    // hold clock (BlockedAt identity), all paths live in one indexed table.
    // The .db file is the source of truth — deleting it (while stopped, or
    // on Linux while running) cancels every hold for that source, keeping
    // destination files. That is the only fail-safe reading: no file means
    // no pending work, never mass deletion. When the last hold clears, the
    // .db (+ WAL sidecars) is deleted, so idle state is zero files, zero
    // RAM, zero timers. Production expiry uses TakeExpiredChunk (bounded);
    // Check is the small-scale/test compat wrapper.
    public sealed class DeferredDeletion : IDisposable
    {
        private const int ChunkSize = 500;

        private readonly string _dir;
        private readonly string _prefix;
        private readonly string? _legacyName;
        private readonly string _dbPath;
        private readonly int _holdDays;
        private readonly ILogger _log;
        private readonly object _lock = new object();
        private SqliteConnection? _conn;
        private bool _hasPending;
        private bool _disposed;

        public DeferredDeletion(string directory, string filePrefix, int holdDays, ILogger log, string? legacyFileName = null)
        {
            _dir = directory;
            _prefix = filePrefix;
            _legacyName = legacyFileName;
            _dbPath = Path.Combine(directory, filePrefix + ".db");
            _holdDays = holdDays;
            _log = log;
            try
            {
                if (Directory.Exists(_dir) && (File.Exists(_dbPath) || HasJsonBatches() || HasLegacyFile()))
                {
                    EnsureConn();
                    MigrateJsonBatches();
                    _hasPending = CountInternal() > 0;
                    if (!_hasPending)
                        DeleteStoreFiles();
                }
            }
            catch (Exception ex) { _log.Warn("Deferred deletion state could not be loaded: " + _dir + " (" + ex.Message + ")"); }
        }

        internal string DbPath => _dbPath;

        // Stable across runs (string.GetHashCode is not): two sources with
        // the same folder name must not share one batch namespace.
        internal static string StableHash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in text.ToUpperInvariant())
                {
                    h ^= c;
                    h *= 16777619;
                }
                return h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        internal static string BatchTimestamp(DateTime blockedAt) =>
            blockedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        // Dedup identity: Windows is case-insensitive, Linux is not, so
        // 'a.txt' and 'A.txt' stay distinct rows there.
        internal static string NormalizeKey(string path) =>
            OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

        public void RecordPending(IEnumerable<string> files, string? sourceRoot = null)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DeferredDeletion));
                var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    var key = NormalizeKey(f);
                    if (!incoming.ContainsKey(key))
                        incoming[key] = f;
                }
                if (incoming.Count == 0) return;
                try
                {
                    EnsureConn();
                    var existing = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var chunk in Chunk(incoming.Keys))
                    {
                        using var cmd = _conn!.CreateCommand();
                        var names = new string[chunk.Count];
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            names[i] = "@p" + i;
                            cmd.Parameters.AddWithValue(names[i], chunk[i]);
                        }
                        // Only generated @pN placeholder names are concatenated;
                        // every value is bound, so no user input reaches SQL.
                        // nosemgrep: csharp-sqli
                        cmd.CommandText = "SELECT path_key FROM pending WHERE path_key IN (" + string.Join(",", names) + ")";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read()) existing.Add(reader.GetString(0));
                    }
                    var added = new List<KeyValuePair<string, string>>();
                    foreach (var kv in incoming)
                        if (!existing.Contains(kv.Key))
                            added.Add(kv);
                    // Two-phase drain: a fresh block during a slow drain must
                    // restart that path's hold clock, not inherit
                    // sweep-deletion. Claimed rows re-blocked here move to
                    // the new batch with claimed reset. Unclaimed dupes keep
                    // the old behavior (ignored, original clock stands).
                    var reblocked = new List<string>();
                    if (existing.Count > 0)
                    {
                        foreach (var chunk in Chunk(incoming.Keys.Where(k => existing.Contains(k))))
                        {
                            using var cmd = _conn!.CreateCommand();
                            var names = new string[chunk.Count];
                            for (int i = 0; i < chunk.Count; i++)
                            {
                                names[i] = "@p" + i;
                                cmd.Parameters.AddWithValue(names[i], chunk[i]);
                            }
                            // Only generated @pN placeholder names are concatenated;
                            // every value is bound, so no user input reaches SQL.
                            // nosemgrep: csharp-sqli
                            cmd.CommandText = "SELECT path_key FROM pending WHERE claimed = 1 AND path_key IN (" + string.Join(",", names) + ")";
                            using var reader = cmd.ExecuteReader();
                            while (reader.Read()) reblocked.Add(reader.GetString(0));
                        }
                    }
                    if (added.Count == 0 && reblocked.Count == 0)
                    {
                        _log.Debug("Deferred deletion: all files already pending, no new batch");
                        return;
                    }

                    var now = DateTime.UtcNow;
                    var batchId = UniqueBatchId(now);
                    using (var tx = _conn!.BeginTransaction())
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT INTO batches (batch_id, blocked_at, last_warning_at, source_root) VALUES (@b, @t, 0, @s)";
                            cmd.Parameters.AddWithValue("@b", batchId);
                            cmd.Parameters.AddWithValue("@t", now.Ticks);
                            cmd.Parameters.AddWithValue("@s", (object?)sourceRoot ?? DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT OR IGNORE INTO pending (path_key, batch_id, path) VALUES (@k, @b, @p)";
                            var k = cmd.Parameters.Add("@k", SqliteType.Text);
                            var b = cmd.Parameters.Add("@b", SqliteType.Text);
                            var p = cmd.Parameters.Add("@p", SqliteType.Text);
                            b.Value = batchId;
                            foreach (var kv in added)
                            {
                                k.Value = kv.Key;
                                p.Value = kv.Value;
                                cmd.ExecuteNonQuery();
                            }
                        }
                        if (reblocked.Count > 0)
                        {
                            foreach (var chunk in Chunk(reblocked))
                            {
                                using var cmd = _conn.CreateCommand();
                                cmd.Transaction = tx;
                                var names = new string[chunk.Count];
                                for (int i = 0; i < chunk.Count; i++)
                                {
                                    names[i] = "@p" + i;
                                    cmd.Parameters.AddWithValue(names[i], chunk[i]);
                                }
                                cmd.Parameters.AddWithValue("@b", batchId);
                                // Only generated @pN placeholder names are concatenated;
                                // every value is bound, so no user input reaches SQL.
                                // nosemgrep: csharp-sqli
                                cmd.CommandText = "UPDATE pending SET batch_id = @b, claimed = 0 WHERE path_key IN (" + string.Join(",", names) + ")";
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                    _hasPending = true;
                    var total = CountInternal();
                    var fresh = added.Count + reblocked.Count;
                    _log.Warn("Deferred deletion: batch " + BatchTimestamp(now) + ": " + fresh
                        + " new files (" + total + " total held), hold for " + _holdDays + " days");
                    var sample = added.Select(kv => kv.Value).ToList();
                    if (reblocked.Count > 0)
                    {
                        foreach (var chunk in Chunk(reblocked))
                        {
                            using var cmd = _conn!.CreateCommand();
                            var names = new string[chunk.Count];
                            for (int i = 0; i < chunk.Count; i++)
                            {
                                names[i] = "@p" + i;
                                cmd.Parameters.AddWithValue(names[i], chunk[i]);
                            }
                            // Only generated @pN placeholder names are concatenated;
                            // every value is bound, so no user input reaches SQL.
                            // nosemgrep: csharp-sqli
                            cmd.CommandText = "SELECT path FROM pending WHERE path_key IN (" + string.Join(",", names) + ")";
                            using var reader = cmd.ExecuteReader();
                            while (reader.Read()) sample.Add(reader.GetString(0));
                        }
                    }
                    LogSample(sample, batchId);
                }
                catch (Exception ex) { _log.Warn("Deferred deletion state could not be saved: " + _dbPath + " (" + ex.Message + ")"); }
            }
        }

        // Watcher fast path: a restored file fires Created, dropping its hold
        // row by indexed key. No I/O at all unless something is held.
        public bool CancelIfPending(string fullPath)
        {
            lock (_lock)
            {
                if (_disposed || !_hasPending) return false;
                try
                {
                    if (_conn == null || !File.Exists(_dbPath))
                    {
                        ResetToEmpty(cancelled: true);
                        return false;
                    }
                    var key = NormalizeKey(fullPath);
                    string? batchId;
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT batch_id FROM pending WHERE path_key = @k";
                        cmd.Parameters.AddWithValue("@k", key);
                        batchId = cmd.ExecuteScalar() as string;
                    }
                    if (batchId == null) return false;
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM pending WHERE path_key = @k";
                        cmd.Parameters.AddWithValue("@k", key);
                        cmd.ExecuteNonQuery();
                    }
                    _log.Debug("Deferred deletion: hold cancelled by restore: " + fullPath);
                    DropBatchIfEmpty(batchId, cancelledByRestore: true);
                    RefreshPendingFlag();
                    return true;
                }
                catch (Exception ex) { _log.Warn("Deferred deletion cancel failed: " + ex.Message); return false; }
            }
        }

        // Next wake-up: earliest expiry or earliest due daily warning across
        // batches. Null means nothing held — the pipeline timer disarms.
        public DateTime? NextDueUtc()
        {
            lock (_lock)
            {
                if (_disposed || !_hasPending || _conn == null) return null;
                try
                {
                    if (!File.Exists(_dbPath))
                    {
                        ResetToEmpty(cancelled: true);
                        return null;
                    }
                    var now = DateTime.UtcNow;
                    DateTime? due = null;
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT blocked_at, last_warning_at FROM batches";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var blocked = new DateTime(reader.GetInt64(0), DateTimeKind.Utc);
                            var warned = new DateTime(reader.GetInt64(1), DateTimeKind.Utc);
                            var expiry = blocked.AddDays(_holdDays);
                            if (expiry < due || due == null) due = expiry;
                            if (now < expiry)
                            {
                                var warnDue = warned == DateTime.MinValue ? now : warned.AddDays(1);
                                if (warnDue < due) due = warnDue;
                            }
                        }
                    }
                    if (due != null && due < now) return now;
                    return due;
                }
                catch (Exception ex) { _log.Warn("Deferred deletion schedule failed: " + ex.Message); return null; }
            }
        }

        // Metadata-only pass: daily warnings + source-missing holds. Touches
        // only the tiny batches table — never the pending rows.
        public DeferredActionType CheckWarnings()
        {
            lock (_lock)
            {
                if (_disposed || !_hasPending || _conn == null) return DeferredActionType.None;
                try
                {
                    if (!File.Exists(_dbPath))
                    {
                        ResetToEmpty(cancelled: true);
                        return DeferredActionType.None;
                    }
                    var now = DateTime.UtcNow;
                    var waiting = false;
                    foreach (var b in ReadBatches())
                    {
                        if (BatchCount(b.BatchId) == 0)
                        {
                            DeleteBatchRow(b.BatchId);
                            continue;
                        }
                        var elapsed = (now - b.BlockedAt).TotalDays;
                        if (elapsed < _holdDays)
                        {
                            if ((now - b.LastWarningAt).TotalDays >= 1)
                            {
                                var remaining = _holdDays - (int)elapsed;
                                var count = BatchCount(b.BatchId);
                                _log.Warn("Deferred deletion: batch " + BatchTimestamp(b.BlockedAt) + ": " + count + " files pending, " + remaining + " day(s) remaining");
                                TouchWarning(b.BatchId, now);
                            }
                            waiting = true;
                        }
                        else if (!string.IsNullOrEmpty(b.SourceRoot) && !Directory.Exists(b.SourceRoot))
                        {
                            if ((now - b.LastWarningAt).TotalDays >= 1)
                            {
                                _log.Warn("Deferred deletion: batch " + BatchTimestamp(b.BlockedAt) + ": source unavailable, holding pending deletions");
                                TouchWarning(b.BatchId, now);
                            }
                            waiting = true;
                        }
                    }
                    RefreshPendingFlag();
                    return waiting ? DeferredActionType.Waiting : DeferredActionType.None;
                }
                catch (Exception ex) { _log.Warn("Deferred deletion warning check failed: " + ex.Message); return DeferredActionType.None; }
            }
        }

        // Bounded expiry drain, two-phase: claims up to limit still-deleted
        // paths from expired batches (oldest first), marking rows claimed
        // instead of deleting them. The caller sweeps dest files, then
        // ConfirmClaimed deletes only still-claimed rows. A kill between
        // claim and sweep leaves claimed rows behind; the next drain
        // re-claims (idempotent) and replays the idempotent deletes. Call
        // in a loop until Files is empty AND Cancelled is 0.
        public ExpiredChunk TakeExpiredChunk(int limit = ChunkSize)
        {
            var result = new ExpiredChunk();
            lock (_lock)
            {
                if (_disposed || !_hasPending || _conn == null || limit <= 0) return result;
                try
                {
                    if (!File.Exists(_dbPath))
                    {
                        ResetToEmpty(cancelled: true);
                        return result;
                    }
                    var now = DateTime.UtcNow;
                    foreach (var b in ReadBatches())
                    {
                        if (result.Files.Count >= limit) break;
                        if ((now - b.BlockedAt).TotalDays < _holdDays) continue;
                        if (!string.IsNullOrEmpty(b.SourceRoot) && !Directory.Exists(b.SourceRoot)) continue;
                        var entries = ReadBatchEntries(b.BatchId, limit - result.Files.Count);
                        if (entries.Count == 0)
                        {
                            DeleteBatchRow(b.BatchId);
                            continue;
                        }
                        ClaimBatchPaths(b.BatchId, entries);
                        var still = new List<string>();
                        var gone = new List<string>();
                        foreach (var e in entries)
                        {
                            if (File.Exists(e.Path)) gone.Add(e.Path);
                            else still.Add(e.Path);
                        }
                        result.Files.AddRange(still);
                        result.CancelledPaths.AddRange(gone);
                        result.Cancelled += gone.Count;
                        var tag = BatchTimestamp(b.BlockedAt);
                        if (still.Count > 0)
                        {
                            _log.Warn("Deferred deletion: batch " + tag + ": " + still.Count + " files still deleted after hold, syncing");
                            LogSample(still, tag);
                        }
                        else
                        {
                            _log.Info("Deferred deletion: batch " + tag + ": files reappeared, cancelling");
                        }
                    }
                    RefreshPendingFlag();
                }
                catch (Exception ex) { _log.Warn("Deferred deletion expiry check failed: " + ex.Message); }
                return result;
            }
        }

        // Sweep phase: deletes only still-claimed rows. The claimed = 1
        // predicate is the safety: a concurrently re-blocked path was reset
        // to claimed = 0 by RecordPending and survives the sweep. After
        // deleting, drops newly-empty batch rows. Returns confirmed count.
        public int ConfirmClaimed(IEnumerable<string> paths)
        {
            lock (_lock)
            {
                if (_disposed || _conn == null) return 0;
                try
                {
                    if (!File.Exists(_dbPath)) return 0;
                    var keys = paths.Select(NormalizeKey).Distinct(StringComparer.Ordinal).ToList();
                    if (keys.Count == 0) return 0;
                    var confirmed = 0;
                    foreach (var chunk in Chunk(keys))
                    {
                        using var cmd = _conn.CreateCommand();
                        var names = new string[chunk.Count];
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            names[i] = "@p" + i;
                            cmd.Parameters.AddWithValue(names[i], chunk[i]);
                        }
                        // Only generated @pN placeholder names are concatenated;
                        // every value is bound, so no user input reaches SQL.
                        // nosemgrep: csharp-sqli
                        cmd.CommandText = "DELETE FROM pending WHERE claimed = 1 AND path_key IN (" + string.Join(",", names) + ")";
                        confirmed += cmd.ExecuteNonQuery();
                    }
                    if (confirmed > 0)
                    {
                        foreach (var b in ReadBatches())
                        {
                            if (BatchCount(b.BatchId) == 0)
                                DeleteBatchRow(b.BatchId);
                        }
                    }
                    RefreshPendingFlag();
                    return confirmed;
                }
                catch (Exception ex) { _log.Warn("Deferred deletion confirm failed: " + ex.Message); return 0; }
            }
        }

        // Small-scale/test compat: warnings plus a full drain. Production
        // uses CheckWarnings + TakeExpiredChunk in a loop instead. Confirms
        // everything it drains so the store empties like the old
        // delete-at-claim behavior did.
        public DeferredAction Check()
        {
            var waiting = CheckWarnings() == DeferredActionType.Waiting;
            var proceed = new List<string>();
            var cancelled = 0;
            while (true)
            {
                var chunk = TakeExpiredChunk(ChunkSize);
                proceed.AddRange(chunk.Files);
                cancelled += chunk.Cancelled;
                var toConfirm = new List<string>(chunk.Files.Count + chunk.CancelledPaths.Count);
                toConfirm.AddRange(chunk.Files);
                toConfirm.AddRange(chunk.CancelledPaths);
                if (toConfirm.Count > 0)
                    ConfirmClaimed(toConfirm);
                if (chunk.Files.Count == 0) break;
            }
            if (proceed.Count > 0)
                return new DeferredAction { Type = DeferredActionType.Proceed, Files = proceed };
            if (waiting)
                return new DeferredAction { Type = DeferredActionType.Waiting };
            if (cancelled > 0)
                return new DeferredAction { Type = DeferredActionType.Cancel, Files = new List<string>() };
            return new DeferredAction { Type = DeferredActionType.None };
        }

        public bool HasPending
        {
            get { lock (_lock) { return _hasPending; } }
        }

        public int HoldDays => _holdDays;

        // Metadata-only snapshot for --deferred-list: one row per batch.
        // Never touches pending paths, never mutates.
        public List<BatchSummary> GetBatchSummaries()
        {
            var rows = new List<BatchSummary>();
            lock (_lock)
            {
                if (_disposed || !_hasPending || _conn == null) return rows;
                try
                {
                    if (!File.Exists(_dbPath)) return rows;
                    foreach (var b in ReadBatches())
                        rows.Add(new BatchSummary
                        {
                            BlockedAt = b.BlockedAt,
                            LastWarningAt = b.LastWarningAt,
                            Files = BatchCount(b.BatchId)
                        });
                }
                catch (Exception ex) { _log.Warn("Deferred deletion summary failed: " + ex.Message); }
            }
            return rows;
        }

        internal int PendingCount
        {
            get { lock (_lock) { return _conn == null ? 0 : CountInternal(); } }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                CloseConn();
            }
        }

        private void EnsureConn()
        {
            if (_conn != null) return;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _conn = SqliteBootstrap.Open(_dbPath, SqliteSchema.Deferred);
        }

        private void CloseConn()
        {
            if (_conn == null) return;
            try { _conn.Close(); } catch { }
            try { _conn.Dispose(); } catch { }
            _conn = null;
        }

        private void DeleteStoreFiles()
        {
            CloseConn();
            // Defense vs stale pooled handles: with Pooling=False there
            // should be none, but a delete must never resurrect a ghost.
            try { SqliteConnection.ClearAllPools(); } catch { }
            foreach (var ext in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                try
                {
                    var f = _dbPath + ext;
                    if (File.Exists(f)) File.Delete(f);
                }
                catch { }
            }
        }

        // Rebuild path: drop every hold through the open connection instead
        // of deleting the files around it — Windows refuses to delete a
        // file our own SQLite handle has open. A surviving IOException
        // means someone else (the running service) holds the store; it
        // propagates so the caller can say so instead of dumping a stack.
        public void ClearStore(string reason = "rebuild")
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DeferredDeletion));
                var had = _hasPending || File.Exists(_dbPath);
                CloseConn();
                try { SqliteConnection.ClearAllPools(); } catch { }
                foreach (var ext in new[] { string.Empty, "-wal", "-shm", "-journal" })
                {
                    var f = _dbPath + ext;
                    if (File.Exists(f)) File.Delete(f);
                }
                _hasPending = false;
                if (had) _log.Info("Deferred deletion: hold store cleared by " + reason);
            }
        }

        // The hold file is gone while we still hold rows: an external delete
        // means cancel (keep destination files), never proceed.
        private void ResetToEmpty(bool cancelled)
        {
            var had = _hasPending;
            DeleteStoreFiles();
            _hasPending = false;
            if (cancelled && had)
                _log.Info("Deferred deletion: hold file deleted externally, holds cancelled");
        }

        private void RefreshPendingFlag()
        {
            if (_conn == null)
            {
                _hasPending = false;
                return;
            }
            var n = CountInternal();
            _hasPending = n > 0;
            if (n == 0)
                DeleteStoreFiles();
        }

        private int CountInternal()
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending";
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }

        private int BatchCount(string batchId)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending WHERE batch_id = @b";
            cmd.Parameters.AddWithValue("@b", batchId);
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }

        private void DeleteBatchRow(string batchId)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM batches WHERE batch_id = @b";
            cmd.Parameters.AddWithValue("@b", batchId);
            cmd.ExecuteNonQuery();
        }

        private void DropBatchIfEmpty(string batchId, bool cancelledByRestore)
        {
            if (BatchCount(batchId) > 0) return;
            DeleteBatchRow(batchId);
            if (cancelledByRestore)
                _log.Debug("Deferred deletion: batch completed by restore: " + batchId);
        }

        private void TouchWarning(string batchId, DateTime now)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "UPDATE batches SET last_warning_at = @t WHERE batch_id = @b";
            cmd.Parameters.AddWithValue("@t", now.Ticks);
            cmd.Parameters.AddWithValue("@b", batchId);
            cmd.ExecuteNonQuery();
        }

        private List<BatchRow> ReadBatches()
        {
            var rows = new List<BatchRow>();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT batch_id, blocked_at, last_warning_at, source_root FROM batches ORDER BY blocked_at";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new BatchRow
                {
                    BatchId = reader.GetString(0),
                    BlockedAt = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                    LastWarningAt = new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                    SourceRoot = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
            return rows;
        }

        private List<string> ReadBatchPaths(string batchId, int limit)
        {
            var paths = new List<string>();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT path FROM pending WHERE batch_id = @b LIMIT @n";
            cmd.Parameters.AddWithValue("@b", batchId);
            cmd.Parameters.AddWithValue("@n", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
            return paths;
        }

        private sealed class BatchEntry
        {
            public string Key = string.Empty;
            public string Path = string.Empty;
        }

        // All rows in the batch (claimed or not): crash-replayed drains must
        // see already-claimed rows again, so claim is idempotent.
        private List<BatchEntry> ReadBatchEntries(string batchId, int limit)
        {
            var entries = new List<BatchEntry>();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT path_key, path FROM pending WHERE batch_id = @b LIMIT @n";
            cmd.Parameters.AddWithValue("@b", batchId);
            cmd.Parameters.AddWithValue("@n", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) entries.Add(new BatchEntry { Key = reader.GetString(0), Path = reader.GetString(1) });
            return entries;
        }

        private void ClaimBatchPaths(string batchId, List<BatchEntry> entries)
        {
            var keys = entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).ToList();
            foreach (var chunk in Chunk(keys))
            {
                using var cmd = _conn!.CreateCommand();
                var names = new string[chunk.Count];
                for (int i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@p" + i;
                    cmd.Parameters.AddWithValue(names[i], chunk[i]);
                }
                cmd.Parameters.AddWithValue("@b", batchId);
                // Only generated @pN placeholder names are concatenated;
                // every value is bound, so no user input reaches SQL.
                // nosemgrep: csharp-sqli
                cmd.CommandText = "UPDATE pending SET claimed = 1 WHERE batch_id = @b AND claimed = 0 AND path_key IN (" + string.Join(",", names) + ")";
                cmd.ExecuteNonQuery();
            }
        }

        private void DeletePaths(List<string> paths)
        {
            foreach (var chunk in Chunk(paths.Select(NormalizeKey)))
            {
                using var cmd = _conn!.CreateCommand();
                var names = new string[chunk.Count];
                for (int i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@p" + i;
                    cmd.Parameters.AddWithValue(names[i], chunk[i]);
                }
                // Only generated @pN placeholder names are concatenated;
                // every value is bound, so no user input reaches SQL.
                // nosemgrep: csharp-sqli
                cmd.CommandText = "DELETE FROM pending WHERE path_key IN (" + string.Join(",", names) + ")";
                cmd.ExecuteNonQuery();
            }
        }

        private static List<List<T>> Chunk<T>(IEnumerable<T> items, int size = 500)
        {
            var chunks = new List<List<T>>();
            var cur = new List<T>(size);
            foreach (var item in items)
            {
                cur.Add(item);
                if (cur.Count >= size)
                {
                    chunks.Add(cur);
                    cur = new List<T>(size);
                }
            }
            if (cur.Count > 0) chunks.Add(cur);
            return chunks;
        }

        private string UniqueBatchId(DateTime blockedAt)
        {
            var id = BatchTimestamp(blockedAt);
            var n = 2;
            while (BatchExists(id))
                id = BatchTimestamp(blockedAt) + "-" + (n++);
            return id;
        }

        private bool BatchExists(string batchId)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM batches WHERE batch_id = @b";
            cmd.Parameters.AddWithValue("@b", batchId);
            return cmd.ExecuteScalar() != null;
        }

        private bool HasLegacyFile()
        {
            if (string.IsNullOrEmpty(_legacyName)) return false;
            try { return File.Exists(Path.Combine(_dir, _legacyName)); }
            catch { return false; }
        }

        private bool HasJsonBatches()
        {
            try { return Directory.EnumerateFiles(_dir, _prefix + "-*.json").FirstOrDefault() != null; }
            catch { return false; }
        }

        // One-time adoption of the pre-SQLite per-batch files (plus the old
        // single-file name): entries keep their ORIGINAL BlockedAt clock,
        // dupes collapse to the earliest batch, then each JSON is removed.
        private void MigrateJsonBatches()
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(_dir, _prefix + "-*.json").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
            catch { return; }
            if (!string.IsNullOrEmpty(_legacyName))
            {
                var legacy = Path.Combine(_dir, _legacyName);
                if (File.Exists(legacy)) files.Add(legacy);
            }
            foreach (var path in files)
            {
                DeferredState? state;
                try
                {
                    var json = File.ReadAllText(path);
                    state = JsonSerializer.Deserialize(json, DeferredDeletionJsonContext.Default.DeferredState);
                }
                catch { _log.Warn("Deferred deletion state could not be loaded; preserving it for recovery: " + path); continue; }
                if (state == null || state.PendingFiles == null || state.PendingFiles.Count == 0)
                {
                    try { File.Delete(path); } catch { }
                    continue;
                }
                try
                {
                    ImportBatch(state);
                    try { File.Delete(path); } catch { }
                    _log.Info("Deferred deletion: migrated " + state.PendingFiles.Count + " held files to the hold database");
                }
                catch (Exception ex) { _log.Warn("Deferred deletion migration failed; preserving it for recovery: " + path + " (" + ex.Message + ")"); }
            }
        }

        private void ImportBatch(DeferredState state)
        {
            var blocked = state.BlockedAt == default ? DateTime.UtcNow : state.BlockedAt.ToUniversalTime();
            var warned = state.LastWarningAt == default ? DateTime.MinValue : state.LastWarningAt.ToUniversalTime();
            var batchId = UniqueBatchId(blocked);
            using (var tx = _conn!.BeginTransaction())
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO batches (batch_id, blocked_at, last_warning_at, source_root) VALUES (@b, @t, @w, @s)";
                    cmd.Parameters.AddWithValue("@b", batchId);
                    cmd.Parameters.AddWithValue("@t", blocked.Ticks);
                    cmd.Parameters.AddWithValue("@w", warned.Ticks);
                    cmd.Parameters.AddWithValue("@s", (object?)state.SourceRoot ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO pending (path_key, batch_id, path) VALUES (@k, @b, @p)";
                    var k = cmd.Parameters.Add("@k", SqliteType.Text);
                    var b = cmd.Parameters.Add("@b", SqliteType.Text);
                    var p = cmd.Parameters.Add("@p", SqliteType.Text);
                    b.Value = batchId;
                    foreach (var f in state.PendingFiles!)
                    {
                        k.Value = NormalizeKey(f);
                        p.Value = f;
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        private void LogSample(List<string> files, string batchId)
        {
            var shown = files.Take(20).ToList();
            var msg = string.Join(Environment.NewLine, shown.Select(f => "  " + f));
            if (files.Count > shown.Count)
                msg += Environment.NewLine + "  ... and " + (files.Count - shown.Count) + " more";
            msg += Environment.NewLine + "Hold database: " + _dbPath + " (batch " + batchId + ")";
            _log.Debug(msg);
        }

        private sealed class BatchRow
        {
            public string BatchId = string.Empty;
            public DateTime BlockedAt;
            public DateTime LastWarningAt;
            public string? SourceRoot;
        }

        internal class DeferredState
        {
            public DateTime BlockedAt { get; set; }
            public DateTime LastWarningAt { get; set; }
            public List<string>? PendingFiles { get; set; }
            public string? SourceRoot { get; set; }
        }
    }

    public sealed class ExpiredChunk
    {
        public List<string> Files { get; } = new List<string>();
        public int Cancelled { get; set; }
        // Reappeared paths claimed in this chunk (File.Exists at claim).
        // Needed so the sweeper can confirm them: their dest delete is a
        // no-op, but the hold row must still go. Kept in sync with
        // Cancelled by TakeExpiredChunk.
        public List<string> CancelledPaths { get; } = new List<string>();
    }

    public sealed class BatchSummary
    {
        public DateTime BlockedAt;
        public DateTime LastWarningAt;
        public int Files;
    }

    public class DeferredAction
    {
        public DeferredActionType Type { get; set; }
        public List<string>? Files { get; set; }
    }

    public enum DeferredActionType
    {
        None,
        Waiting,
        Proceed,
        Cancel
    }
}
