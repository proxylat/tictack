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
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _conn = new SqliteConnection("Data Source=" + _dbPath);
            _conn.Open();

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS job_runs (
                    name TEXT PRIMARY KEY,
                    last_run TEXT NOT NULL
                )";
                cmd.ExecuteNonQuery();
            }
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn?.Close(); } catch { }
            _conn?.Dispose();
        }
    }
}
