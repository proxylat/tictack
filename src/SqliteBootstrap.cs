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

    // Thrown when PRAGMA quick_check reports corruption. The file has
    // already been quarantined (moved aside, never deleted: a hold ledger
    // or skip-history is user data until a human says otherwise), so the
    // caller can reopen fresh or continue degraded. Not a SqliteException,
    // so the transient-retry loops below do not catch it.
    internal sealed class CorruptDatabaseException : Exception
    {
        public string DbPath { get; }
        public string QuarantinePath { get; }
        public CorruptDatabaseException(string dbPath, string quarantinePath)
            : base("SQLite database corrupt, quarantined to " + quarantinePath + ": " + dbPath)
        {
            DbPath = dbPath;
            QuarantinePath = quarantinePath;
        }
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
                mtime INTEGER NOT NULL,
                file_id TEXT,
                ctime INTEGER NOT NULL DEFAULT 0,
                hash TEXT
            )";
            // NOTE: idx_state_fileid is created by EnsureStateColumns below,
            // not here — old 3-column DBs must gain the column first or the
            // index build fails and migration never runs.

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
                path TEXT NOT NULL,
                claimed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_pending_batch ON pending(batch_id);
            CREATE INDEX IF NOT EXISTS idx_pending_unclaimed ON pending(batch_id) WHERE claimed = 0";

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
                    if (IsCorrupt(conn))
                    {
                        conn.Dispose();
                        conn = null;
                        throw Quarantined(dbPath);
                    }
                    return conn;
                }
                catch (SqliteException ex) when (IsCorruption(ex))
                {
                    conn?.Dispose();
                    throw Quarantined(dbPath);
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

                    conn = new SqliteConnection(ConnectionString(dbPath, schema));
                    try
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        await ConfigureAsync(conn, schema).ConfigureAwait(false);
                        if (await IsCorruptAsync(conn).ConfigureAwait(false))
                        {
                            conn.Dispose();
                            conn = null;
                            throw Quarantined(dbPath);
                        }
                        return conn;
                    }
                    catch (SqliteException ex) when (IsCorruption(ex))
                    {
                        conn?.Dispose();
                        throw Quarantined(dbPath);
                    }
                    catch (SqliteException) when (attempt < maxAttempts)
                    {
                        conn?.Dispose();
                        await Task.Delay(100 * attempt).ConfigureAwait(false);
                    }
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
            // WAL's default sync level is NORMAL (ack before bytes reach
            // stable storage): a power loss can roll back a state upsert
            // the file copy already relied on. FULL makes every commit
            // wait for disk. busy_timeout waits on locks instead of
            // throwing SQLITE_BUSY at the first contention (CLI vs
            // service). Both are per-connection: re-applied on every
            // open, and opens are startup-only so the cost is once.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA synchronous = FULL";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout = 5000";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql(schema);
                cmd.ExecuteNonQuery();
            }
            if (schema == SqliteSchema.State)
                EnsureStateColumns(conn);
            if (schema == SqliteSchema.Deferred)
                EnsureDeferredColumns(conn);
        }

        // Filesystem-bug screen (file-consistency: the FS can corrupt a db
        // that SQLite then happily answers queries from). quick_check reads
        // index structure, not row data: seconds even at 2M rows, once per
        // open, and opens are startup-only. On failure the file is moved
        // aside with its sidecars — never deleted — and the caller rebuilds
        // or degrades: state resyncs, holds cancel (files kept), jobs rerun.
        private static bool IsCorrupt(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            cmd.CommandTimeout = 120;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!"ok".Equals(reader.GetString(0), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static async Task<bool> IsCorruptAsync(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            cmd.CommandTimeout = 120;
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!"ok".Equals(reader.GetString(0), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26): the file is bad, not
        // the moment. Everything else (BUSY, IOERR, LOCKED) is transient and
        // keeps the existing bounded retry — quarantining on a transient
        // would move a healthy store aside from under a concurrent owner.
        // This also covers Configure-time failures: a zeroed schema page
        // throws on the first PRAGMA, before any check could run.
        private static bool IsCorruption(SqliteException ex) =>
            ex.SqliteErrorCode is 11 or 26;

        private static CorruptDatabaseException Quarantined(string dbPath)
        {
            // Pooled state/job handles would survive and keep pointing at
            // the unlinked inode; clear them before the moves.
            try { SqliteConnection.ClearAllPools(); } catch { }
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var quarantine = dbPath + ".corrupt-" + stamp;
            try
            {
                if (File.Exists(dbPath)) File.Move(dbPath, quarantine);
                foreach (var ext in new[] { "-wal", "-shm", "-journal" })
                {
                    var side = dbPath + ext;
                    if (File.Exists(side)) File.Move(side, quarantine + ext);
                }
            }
            catch { }
            return new CorruptDatabaseException(dbPath, quarantine);
        }

        // Old state DBs have the 3-column table (path/size/mtime). New
        // columns arrive via ALTER TABLE, never a rebuild: the rows are the
        // user's skip-known history, losing them means re-hashing everything.
        private static void EnsureStateColumns(SqliteConnection conn)
        {
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(state)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) cols.Add(reader.GetString(1));
            }
            if (!cols.Contains("file_id"))
                Exec(conn, "ALTER TABLE state ADD COLUMN file_id TEXT");
            if (!cols.Contains("ctime"))
                Exec(conn, "ALTER TABLE state ADD COLUMN ctime INTEGER NOT NULL DEFAULT 0");
            if (!cols.Contains("hash"))
                Exec(conn, "ALTER TABLE state ADD COLUMN hash TEXT");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_state_fileid ON state(file_id)");
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // Old hold stores predate the claimed column (two-phase drain).
        // ALTER TABLE, never a rebuild: the rows are the user's holds.
        private static void EnsureDeferredColumns(SqliteConnection conn)
        {
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(pending)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) cols.Add(reader.GetString(1));
            }
            if (!cols.Contains("claimed"))
                Exec(conn, "ALTER TABLE pending ADD COLUMN claimed INTEGER NOT NULL DEFAULT 0");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_pending_unclaimed ON pending(batch_id) WHERE claimed = 0");
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
            // Same durability contract as the sync path above: FULL sync +
            // 5s busy wait, re-applied per connection on every open.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA synchronous = FULL";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout = 5000";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql(schema);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (schema == SqliteSchema.State)
                await EnsureStateColumnsAsync(conn).ConfigureAwait(false);
            if (schema == SqliteSchema.Deferred)
                await EnsureDeferredColumnsAsync(conn).ConfigureAwait(false);
        }

        private static async Task EnsureStateColumnsAsync(SqliteConnection conn)
        {
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(state)";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) cols.Add(reader.GetString(1));
            }
            if (!cols.Contains("file_id"))
                await ExecAsync(conn, "ALTER TABLE state ADD COLUMN file_id TEXT").ConfigureAwait(false);
            if (!cols.Contains("ctime"))
                await ExecAsync(conn, "ALTER TABLE state ADD COLUMN ctime INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            if (!cols.Contains("hash"))
                await ExecAsync(conn, "ALTER TABLE state ADD COLUMN hash TEXT").ConfigureAwait(false);
            await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_state_fileid ON state(file_id)").ConfigureAwait(false);
        }

        private static async Task EnsureDeferredColumnsAsync(SqliteConnection conn)
        {
            var cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(pending)";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) cols.Add(reader.GetString(1));
            }
            if (!cols.Contains("claimed"))
                await ExecAsync(conn, "ALTER TABLE pending ADD COLUMN claimed INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_pending_unclaimed ON pending(batch_id) WHERE claimed = 0").ConfigureAwait(false);
        }

        private static async Task ExecAsync(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
