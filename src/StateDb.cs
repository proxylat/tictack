using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace TicTack
{
    public sealed class StateDb : IDisposable
    {
        private SqliteConnection _conn = null!;
        private readonly string _dbPath;
        private readonly object _lock = new object();
        private bool _disposed;

        public StateDb(string dbPath)
        {
            _dbPath = dbPath;
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Reconnect();
        }

        void Reconnect()
        {
            if (_conn != null)
            {
                try { _conn.Close(); } catch { }
                _conn.Dispose();
            }
            _conn = new SqliteConnection("Data Source=" + _dbPath);
            _conn.Open();

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cache_size = -500";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS state (
                    path TEXT PRIMARY KEY,
                    size INTEGER NOT NULL,
                    mtime INTEGER NOT NULL
                )";
                cmd.ExecuteNonQuery();
            }
        }

        void EnsureConnected()
        {
            if (_disposed) return;
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1";
                    cmd.ExecuteScalar();
                }
            }
            catch
            {
                var root = Path.GetPathRoot(_dbPath);
                if (!string.IsNullOrEmpty(root) &&
                    !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase) && d.IsReady))
                    return;

                for (int i = 0; i < 5; i++)
                {
                    try { Reconnect(); return; }
                    catch
                    {
                        if (i < 4)
                            Thread.Sleep((int)Math.Pow(2, i) * 1000);
                    }
                }
            }
        }

        public Dictionary<string, (long size, long mtime)> LoadAll()
        {
            var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT path, size, mtime FROM state";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var path = reader.GetString(0);
                            var size = reader.GetInt64(1);
                            var mtime = reader.GetInt64(2);
                            result[path] = (size, mtime);
                        }
                    }
                }
            }
            return result;
        }

        public void Upsert(string path, long size, long mtime)
        {
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO state (path, size, mtime) VALUES (@p, @s, @m)";
                    cmd.Parameters.AddWithValue("@p", path);
                    cmd.Parameters.AddWithValue("@s", size);
                    cmd.Parameters.AddWithValue("@m", mtime);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void UpsertBatch(IEnumerable<(string path, long size, long mtime)> entries)
        {
            lock (_lock)
            {
                EnsureConnected();
                using (var tx = _conn.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO state (path, size, mtime) VALUES (@p, @s, @m)";
                    var path = cmd.Parameters.Add("@p", SqliteType.Text);
                    var size = cmd.Parameters.Add("@s", SqliteType.Integer);
                    var mtime = cmd.Parameters.Add("@m", SqliteType.Integer);
                    foreach (var entry in entries)
                    {
                        path.Value = entry.path;
                        size.Value = entry.size;
                        mtime.Value = entry.mtime;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        public void Delete(string path)
        {
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM state WHERE path = @p";
                    cmd.Parameters.AddWithValue("@p", path);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void UpdatePrefix(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(oldPrefix)) return;
            var sep = Path.DirectorySeparatorChar;
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE state SET path = @new || SUBSTR(path, LENGTH(@old) + 1) WHERE path LIKE @old || @sep || '%'";
                    cmd.Parameters.AddWithValue("@old", oldPrefix);
                    cmd.Parameters.AddWithValue("@new", newPrefix);
                    cmd.Parameters.AddWithValue("@sep", sep.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public long? GetSize(string path)
        {
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT size FROM state WHERE path = @p";
                    cmd.Parameters.AddWithValue("@p", path);
                    var r = cmd.ExecuteScalar();
                    return r == null || r is DBNull ? (long?)null : (long)r;
                }
            }
        }

        public long Count()
        {
            lock (_lock)
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM state";
                    return (long)(cmd.ExecuteScalar() ?? 0L);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_conn != null)
                {
                    _conn.Close();
                    _conn.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
