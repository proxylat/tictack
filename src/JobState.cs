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
                    return DateTime.TryParse((string)r, out var d) ? d.Date : (DateTime?)null;
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
                    cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn?.Close(); } catch { }
            _conn?.Dispose();
        }
    }
}
