using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TicTack
{
    public sealed class StateDb : IDisposable
    {
        private SqliteConnection _conn = null!;
        private readonly string _dbPath;
        private readonly ILogger? _log;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public StateDb(string dbPath, ILogger? log = null)
        {
            _dbPath = dbPath;
            _log = log;
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
            _conn = SqliteBootstrap.Open(_dbPath, SqliteSchema.State);
        }

        void EnsureConnected()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StateDb));
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1";
                    cmd.ExecuteScalar();
                }
            }
            catch (Exception ex)
            {
                var root = Path.GetPathRoot(_dbPath);
                if (!string.IsNullOrEmpty(root) && !DriveGuard.IsReady(root))
                {
                    _log?.Warn("StateDb drive not ready, reconnect skipped: " + _dbPath);
                    return;
                }

                Exception? last = ex;
                for (int i = 0; i < 5; i++)
                {
                    try { Reconnect(); return; }
                    catch (Exception retryEx)
                    {
                        last = retryEx;
                        if (i < 4)
                            Thread.Sleep((int)Math.Pow(2, i) * 1000);
                    }
                }
                _log?.Warn("StateDb reconnect failed after 5 attempts: " + _dbPath + " (" + last?.Message + ")");
            }
        }

        async Task EnsureConnectedAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StateDb));
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1";
                    await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var root = Path.GetPathRoot(_dbPath);
                if (!string.IsNullOrEmpty(root) && !DriveGuard.IsReady(root))
                {
                    _log?.Warn("StateDb drive not ready, reconnect skipped: " + _dbPath);
                    return;
                }

                Exception? last = ex;
                for (int i = 0; i < 5; i++)
                {
                    try { await ReconnectAsync().ConfigureAwait(false); return; }
                    catch (Exception retryEx)
                    {
                        last = retryEx;
                        if (i < 4)
                            await Task.Delay((int)Math.Pow(2, i) * 1000).ConfigureAwait(false);
                    }
                }
                _log?.Warn("StateDb reconnect failed after 5 attempts: " + _dbPath + " (" + last?.Message + ")");
            }
        }

        void Enter()
        {
            _gate.Wait();
            if (_disposed)
            {
                _gate.Release();
                throw new ObjectDisposedException(nameof(StateDb));
            }
        }

        async Task EnterAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            if (_disposed)
            {
                _gate.Release();
                throw new ObjectDisposedException(nameof(StateDb));
            }
        }

        async Task ReconnectAsync()
        {
            if (_conn != null)
            {
                try { _conn.Close(); } catch { }
                _conn.Dispose();
            }
            _conn = await SqliteBootstrap.OpenAsync(_dbPath, SqliteSchema.State).ConfigureAwait(false);
        }

        public Dictionary<string, (long size, long mtime)> LoadAll()
        {
            Enter();
            try
            {
                EnsureConnected();
                var result = new Dictionary<string, (long, long)>(CountInternal(), StringComparer.OrdinalIgnoreCase);
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
                return result;
            }
            finally { _gate.Release(); }
        }

        public async Task<Dictionary<string, (long size, long mtime)>> LoadAllAsync()
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                var result = new Dictionary<string, (long, long)>(await CountInternalAsync().ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT path, size, mtime FROM state";
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                        }
                    }
                }
                return result;
            }
            finally { _gate.Release(); }
        }

        public void Upsert(string path, long size, long mtime)
        {
            Enter();
            try
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
            finally { _gate.Release(); }
        }

        public async Task UpsertAsync(string path, long size, long mtime)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO state (path, size, mtime) VALUES (@p, @s, @m)";
                    cmd.Parameters.AddWithValue("@p", path);
                    cmd.Parameters.AddWithValue("@s", size);
                    cmd.Parameters.AddWithValue("@m", mtime);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }

        public void UpsertBatch(IEnumerable<(string path, long size, long mtime)> entries)
        {
            Enter();
            try
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
            finally { _gate.Release(); }
        }

        public void Delete(string path)
        {
            Enter();
            try
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM state WHERE path = @p";
                    cmd.Parameters.AddWithValue("@p", path);
                    cmd.ExecuteNonQuery();
                }
            }
            finally { _gate.Release(); }
        }

        public async Task DeleteAsync(string path)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM state WHERE path = @p";
                    cmd.Parameters.AddWithValue("@p", path);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }

        public void UpdatePrefix(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(oldPrefix)) return;
            var sep = Path.DirectorySeparatorChar;
            Enter();
            try
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    // ESCAPE: a renamed folder like "My_Documents" must not let
                    // _ or % match unrelated rows.
                    cmd.CommandText = "UPDATE state SET path = @new || SUBSTR(path, LENGTH(@old) + 1) WHERE path LIKE @oldLike || @sep || '%' ESCAPE '\\'";
                    cmd.Parameters.AddWithValue("@old", oldPrefix);
                    cmd.Parameters.AddWithValue("@oldLike", EscapeLike(oldPrefix));
                    cmd.Parameters.AddWithValue("@new", newPrefix);
                    cmd.Parameters.AddWithValue("@sep", sep.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            finally { _gate.Release(); }
        }

        public async Task UpdatePrefixAsync(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(oldPrefix)) return;
            var sep = Path.DirectorySeparatorChar;
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                using (var cmd = _conn.CreateCommand())
                {
                    // ESCAPE: a renamed folder like "My_Documents" must not let
                    // _ or % match unrelated rows.
                    cmd.CommandText = "UPDATE state SET path = @new || SUBSTR(path, LENGTH(@old) + 1) WHERE path LIKE @oldLike || @sep || '%' ESCAPE '\\'";
                    cmd.Parameters.AddWithValue("@old", oldPrefix);
                    cmd.Parameters.AddWithValue("@oldLike", EscapeLike(oldPrefix));
                    cmd.Parameters.AddWithValue("@new", newPrefix);
                    cmd.Parameters.AddWithValue("@sep", sep.ToString());
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }

        public long? GetSize(string path)
        {
            Enter();
            try
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
            finally { _gate.Release(); }
        }

        public async Task<long?> GetSizeAsync(string path)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT size FROM state WHERE path = @p";
                    cmd.Parameters.AddWithValue("@p", path);
                    var r = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return r == null || r is DBNull ? (long?)null : (long)r;
                }
            }
            finally { _gate.Release(); }
        }

        public long Count()
        {
            Enter();
            try
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM state";
                    return (long)(cmd.ExecuteScalar() ?? 0L);
                }
            }
            finally { _gate.Release(); }
        }

        public async Task<long> CountAsync()
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(false);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM state";
                    return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
                }
            }
            finally { _gate.Release(); }
        }

        // Capacity seed only; cap it so an absurdly large state table cannot
        // overflow the int conversion or over-allocate the dictionary up front.
        private int CountInternal()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM state";
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                return count > 1_000_000 ? 1_000_000 : (int)count;
            }
        }

        private async Task<int> CountInternalAsync()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM state";
                var count = (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
                return count > 1_000_000 ? 1_000_000 : (int)count;
            }
        }

        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        public void Clear()
        {
            Enter();
            try
            {
                EnsureConnected();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM state";
                    cmd.ExecuteNonQuery();
                }
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            _gate.Wait();
            try
            {
                if (_disposed) return;
                _disposed = true;
                if (_conn != null)
                {
                    try { _conn.Close(); }
                    catch (Exception ex) { _log?.Warn("StateDb close failed: " + ex.Message); }
                    _conn.Dispose();
                }
            }
            finally { _gate.Release(); }
        }
    }
}
