using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TicTack;

namespace Corpus
{
    public readonly record struct ChunkRow(byte[] Digest, long SizeBytes, long StoredBytes, string Pack, long PackOffset);

    // SQLite-backed chunk location index. Upserts are batched in one
    // transaction (one fsync per batch, not per chunk); GC marks live rows
    // then sweeps the rest, so crash between mark and sweep only orphans
    // pack bytes — never a live chunk.
    public sealed class ChunkIndex : IDisposable
    {
        private readonly SqliteConnection _conn;
        private bool _disposed;

        public ChunkIndex(string dbPath)
        {
            _conn = SqliteBootstrap.Open(dbPath, SqliteSchema.Corpus);
        }

        public void UpsertBatch(List<ChunkRow> rows)
        {
            if (rows.Count == 0) return;
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO chunks (digest, size_bytes, stored_bytes, pack, pack_offset) VALUES ($d, $s, $st, $p, $o)";
            var pDigest = cmd.Parameters.Add("$d", SqliteType.Blob);
            var pSize = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pStored = cmd.Parameters.Add("$st", SqliteType.Integer);
            var pPack = cmd.Parameters.Add("$p", SqliteType.Text);
            var pOffset = cmd.Parameters.Add("$o", SqliteType.Integer);
            foreach (var r in rows)
            {
                pDigest.Value = r.Digest;
                pSize.Value = r.SizeBytes;
                pStored.Value = r.StoredBytes;
                pPack.Value = r.Pack;
                pOffset.Value = r.PackOffset;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        public bool TryGet(byte[] digest, out ChunkRow row)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT size_bytes, stored_bytes, pack, pack_offset FROM chunks WHERE digest = $d";
            cmd.Parameters.AddWithValue("$d", digest);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                row = new ChunkRow(digest, 0, 0, "", 0);
                return false;
            }
            row = new ChunkRow(digest, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
            return true;
        }

        public long Count()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            return cmd.ExecuteScalar() is long n ? n : 0;
        }

        public void SetAllMarks(int mark)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE chunks SET mark = $m";
            cmd.Parameters.AddWithValue("$m", mark);
            cmd.ExecuteNonQuery();
        }

        public long SweepUnmarked()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM chunks WHERE mark = 0";
            return cmd.ExecuteNonQuery();
        }

        // GC mark: single transaction, input walked in 500s (bounded
        // statement burst per inner loop, one commit for the whole batch).
        public void MarkBatch(List<byte[]> digests)
        {
            if (digests.Count == 0) return;
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE chunks SET mark = 1 WHERE digest = $d";
            var pDigest = cmd.Parameters.Add("$d", SqliteType.Blob);
            for (var start = 0; start < digests.Count; start += 500)
            {
                var end = Math.Min(start + 500, digests.Count);
                for (var i = start; i < end; i++)
                {
                    pDigest.Value = digests[i];
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }

        public List<string> LivePacks()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT pack FROM chunks WHERE mark = 1";
            var packs = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                packs.Add(reader.GetString(0));
            return packs;
        }

        public (long Count, long StoredBytes) UnmarkedStats()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(stored_bytes), 0) FROM chunks WHERE mark = 0";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
