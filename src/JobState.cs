using System.Globalization;
using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace TicTack
{
    public sealed class JobRunStore : IDisposable
    {
        private SqliteConnection _conn = null!;
        private readonly string _dbPath;
        private readonly object _lock = new object();
        private bool _disposed;

        public JobRunStore(string dbPath)
        {
            _dbPath = dbPath;
            _conn = SqliteBootstrap.Open(dbPath, SqliteSchema.JobRuns);
        }

        public DateTime? GetLastRun(string name)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT last_run FROM job_runs WHERE name = @n";
                    cmd.Parameters.AddWithValue("@n", name);
                    var r = cmd.ExecuteScalar();
                    if (r == null || r is DBNull) return null;
                    return DateTime.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null;
                }
            }
        }

        public void SetLastRun(string name, DateTime date)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO job_runs (name, last_run) VALUES (@n, @d)";
                    cmd.Parameters.AddWithValue("@n", name);
                    cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Full-timestamp siblings for interval jobs: sub-daily cadences need
        // time-of-day precision the date-only stamps above cannot hold. Same
        // column (TEXT), longer format; the date readers still parse these
        // rows (they truncate to .Date), so mixed use is safe.
        public DateTime? GetLastRunAt(string name)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT last_run FROM job_runs WHERE name = @n";
                    cmd.Parameters.AddWithValue("@n", name);
                    var r = cmd.ExecuteScalar();
                    if (r == null || r is DBNull) return null;
                    return DateTime.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
                }
            }
        }

        public void SetLastRunAt(string name, DateTime stamp)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO job_runs (name, last_run) VALUES (@n, @d)";
                    cmd.Parameters.AddWithValue("@n", name);
                    cmd.Parameters.AddWithValue("@d", stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Integer counters sharing the same store (scrub shard index).
        // The table self-migrates: no bootstrap change needed.
        public int GetCounter(string name)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM counters WHERE name = @n";
                    cmd.Parameters.AddWithValue("@n", name);
                    var r = cmd.ExecuteScalar();
                    if (r == null || r is DBNull) return 0;
                    return (int)(long)r;
                }
            }
        }

        public void SetCounter(string name, int value)
        {
            lock (_lock)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO counters (name, value) VALUES (@n, @v)";
                    cmd.Parameters.AddWithValue("@n", name);
                    cmd.Parameters.AddWithValue("@v", value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void EnsurePendingWatchTable()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS pending_watch_files (job_name TEXT NOT NULL, watch_path TEXT NOT NULL, rel_path TEXT NOT NULL, PRIMARY KEY (job_name, watch_path, rel_path))";
            cmd.ExecuteNonQuery();
        }

        public void EnqueueWatchFiles(string jobName, string watchPath, System.Collections.Generic.IEnumerable<string> relPaths)
        {
            if (string.IsNullOrEmpty(jobName) || string.IsNullOrEmpty(watchPath)) return;
            lock (_lock)
            {
                EnsurePendingWatchTable();
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO pending_watch_files (job_name, watch_path, rel_path) VALUES (@j, @w, @r)";
                var pJ = cmd.Parameters.Add("@j", SqliteType.Text);
                var pW = cmd.Parameters.Add("@w", SqliteType.Text);
                var pR = cmd.Parameters.Add("@r", SqliteType.Text);
                pJ.Value = jobName;
                pW.Value = watchPath;
                foreach (var rel in relPaths)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    pR.Value = rel;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public void RemoveWatchFiles(string jobName, string watchPath, System.Collections.Generic.IEnumerable<string> relPaths)
        {
            if (string.IsNullOrEmpty(jobName) || string.IsNullOrEmpty(watchPath)) return;
            lock (_lock)
            {
                EnsurePendingWatchTable();
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM pending_watch_files WHERE job_name = @j AND watch_path = @w AND rel_path = @r";
                var pJ = cmd.Parameters.Add("@j", SqliteType.Text);
                var pW = cmd.Parameters.Add("@w", SqliteType.Text);
                var pR = cmd.Parameters.Add("@r", SqliteType.Text);
                pJ.Value = jobName;
                pW.Value = watchPath;
                foreach (var rel in relPaths)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    pR.Value = rel;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public System.Collections.Generic.IReadOnlyDictionary<string, string[]> GetPendingWatchFiles(string jobName)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var result = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(cmp);
            if (string.IsNullOrEmpty(jobName))
                return new System.Collections.Generic.Dictionary<string, string[]>(0, cmp);
            lock (_lock)
            {
                EnsurePendingWatchTable();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT watch_path, rel_path FROM pending_watch_files WHERE job_name = @j";
                cmd.Parameters.AddWithValue("@j", jobName);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var wp = reader.GetString(0);
                    var rp = reader.GetString(1);
                    if (!result.TryGetValue(wp, out var list))
                    {
                        list = new System.Collections.Generic.List<string>();
                        result[wp] = list;
                    }
                    list.Add(rp);
                }
            }
            var output = new System.Collections.Generic.Dictionary<string, string[]>(result.Count, result.Comparer);
            foreach (var kv in result) output[kv.Key] = kv.Value.ToArray();
            return output;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn?.Close(); } catch { }
            _conn?.Dispose();
        }
    }
}
