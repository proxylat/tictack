using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TicTack
{
    // Which store is being bootstrapped. The schema is a compile-time constant
    // here rather than a caller-supplied SQL string, so no query text ever
    // reaches the command from a variable.
    internal enum SqliteSchema
    {
        State,
        JobRuns,
        Deferred
    }

    // One owner for the SQLite bootstrap both stores used to duplicate:
    // directory creation, connection string, WAL, cache size, CREATE TABLE.
    internal static class SqliteBootstrap
    {
        // The deferred hold store deletes its own files when the last hold
        // clears (and honors external deletion as cancel). Pooled handles
        // would survive Close and keep pointing at the unlinked inode, so a
        // later Open would reuse the ghost instead of creating the file.
        // Pooling stays on for the hot state/job stores; holds are rare ops.
        private static string ConnectionString(string dbPath, SqliteSchema schema) =>
            "Data Source=" + dbPath + (schema == SqliteSchema.Deferred ? ";Pooling=False" : string.Empty);
        private const string StateSql = @"CREATE TABLE IF NOT EXISTS state (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL
            )";

        private const string JobRunsSql = @"CREATE TABLE IF NOT EXISTS job_runs (
                name TEXT PRIMARY KEY,
                last_run TEXT NOT NULL
            )";

        // Deferred holds: one row per blocked batch plus one row per held
        // path. path_key is the dedup identity (upper-cased on Windows where
        // the filesystem is case-insensitive, verbatim on Linux), path keeps
        // the original spelling for logging and deletion. All timer and
        // warning decisions read the tiny batches table only; the pending
        // table is touched for inserts, indexed cancels, and bounded expiry
        // chunks, never snapshotted whole (2M-file scale rule).
        private const string DeferredSql = @"CREATE TABLE IF NOT EXISTS batches (
                batch_id TEXT PRIMARY KEY,
                blocked_at INTEGER NOT NULL,
                last_warning_at INTEGER NOT NULL,
                source_root TEXT
            );
            CREATE TABLE IF NOT EXISTS pending (
                path_key TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL REFERENCES batches(batch_id) ON DELETE CASCADE,
                path TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_pending_batch ON pending(batch_id)";

        // Startup recovery (crash-killed WAL, AV/Defender holding the -wal)
        // can fail transiently with SQLITE_IOERR. Bounded retry here keeps
        // a transient lock from crashing startup on strangers' machines.
        public static SqliteConnection Open(string dbPath, SqliteSchema schema)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                SqliteConnection? conn = null;
                try
                {
                    var dir = Path.GetDirectoryName(dbPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    conn = new SqliteConnection(ConnectionString(dbPath, schema));
                    conn.Open();
                    Configure(conn, schema);
                    return conn;
                }
                catch (SqliteException) when (attempt < maxAttempts)
                {
                    conn?.Dispose();
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        public static async Task<SqliteConnection> OpenAsync(string dbPath, SqliteSchema schema)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                SqliteConnection? conn = null;
                try
                {
                    var dir = Path.GetDirectoryName(dbPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    conn = new SqliteConnection("Data Source=" + dbPath);
                    await conn.OpenAsync().ConfigureAwait(false);
                    await ConfigureAsync(conn, schema).ConfigureAwait(false);
                    return conn;
                }
                catch (SqliteException) when (attempt < maxAttempts)
                {
                    conn?.Dispose();
                    await Task.Delay(100 * attempt).ConfigureAwait(false);
                }
            }
        }

        private static void Configure(SqliteConnection conn, SqliteSchema schema)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cache_size = -500";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql(schema);
                cmd.ExecuteNonQuery();
            }
        }

        private static async Task ConfigureAsync(SqliteConnection conn, SqliteSchema schema)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA cache_size = -500";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql(schema);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private static string SchemaSql(SqliteSchema schema) => schema switch
        {
            SqliteSchema.State => StateSql,
            SqliteSchema.JobRuns => JobRunsSql,
            SqliteSchema.Deferred => DeferredSql,
            _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
        };
    }
}
