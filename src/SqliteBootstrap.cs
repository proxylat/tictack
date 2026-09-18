using System;
using System.IO;
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
        JobRuns
    }

    // One owner for the SQLite bootstrap both stores used to duplicate:
    // directory creation, connection string, WAL, cache size, CREATE TABLE.
    internal static class SqliteBootstrap
    {
        private const string StateSql = @"CREATE TABLE IF NOT EXISTS state (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL
            )";

        private const string JobRunsSql = @"CREATE TABLE IF NOT EXISTS job_runs (
                name TEXT PRIMARY KEY,
                last_run TEXT NOT NULL
            )";

        public static SqliteConnection Open(string dbPath, SqliteSchema schema)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();
            Configure(conn, schema);
            return conn;
        }

        public static async Task<SqliteConnection> OpenAsync(string dbPath, SqliteSchema schema)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var conn = new SqliteConnection("Data Source=" + dbPath);
            await conn.OpenAsync().ConfigureAwait(false);
            await ConfigureAsync(conn, schema).ConfigureAwait(false);
            return conn;
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
            _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
        };
    }
}
