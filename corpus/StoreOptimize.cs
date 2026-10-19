using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TicTack;

namespace Corpus
{
    // Distinct failures so the CLI can map each to its own exit code.
    // Every message names the store path and the blocking reason.
    public class StoreOptimizeException : Exception
    {
        public StoreOptimizeException(string message) : base(message) { }
        public StoreOptimizeException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class StoreOptimizeHeadroomException : StoreOptimizeException
    {
        public StoreOptimizeHeadroomException(string message) : base(message) { }
        public StoreOptimizeHeadroomException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class StoreOptimizeVerifyException : StoreOptimizeException
    {
        public StoreOptimizeVerifyException(string message) : base(message) { }
        public StoreOptimizeVerifyException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class StoreOptimizeBudgetException : StoreOptimizeException
    {
        public StoreOptimizeBudgetException(string message) : base(message) { }
        public StoreOptimizeBudgetException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed record OptimizeResult(
        long BeforeBytes,
        long AfterBytes,
        long CompactFreedBytes,
        IReadOnlyList<int> PrunedGenerations,
        long PruneFreedBytes,
        string Report);

    // --optimize-to-fit: shrink the chunk store in place. Compact always
    // runs; budget prune only with an explicit byte budget. Generation
    // deletion rides the existing GC path exclusively — this class deletes
    // pack files only after their live chunks moved (swap) and never
    // deletes manifests or layouts itself.
    public sealed class StoreOptimizer
    {
        private readonly string _storeRoot;
        private readonly string _packsDir;
        private readonly string _manifestsDir;
        private readonly string _dbPath;
        private readonly CorpusRetentionConfig _retention;
        private readonly ILogger _log;
        private readonly int _chunkMib;
        private readonly int _zstdLevel;
        private readonly string? _encryptHook;
        private readonly IDeltaCodec? _deltaCodec;

        public StoreOptimizer(string storeRoot, string dbPath, CorpusRetentionConfig retention, ILogger log, int chunkMib, int zstdLevel, string? encryptHook = null, IDeltaCodec? deltaCodec = null)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(log);
            if (chunkMib < 1) throw new ArgumentOutOfRangeException(nameof(chunkMib));
            if (zstdLevel < 1) throw new ArgumentOutOfRangeException(nameof(zstdLevel));
            _storeRoot = storeRoot;
            _packsDir = Path.Combine(storeRoot, "packs");
            _manifestsDir = Path.Combine(storeRoot, "manifests");
            _dbPath = dbPath;
            _retention = retention;
            _log = log;
            _chunkMib = chunkMib;
            _zstdLevel = zstdLevel;
            _encryptHook = string.IsNullOrWhiteSpace(encryptHook) ? null : encryptHook;
            _deltaCodec = deltaCodec;
        }

        // Test seam: free bytes on the store volume (null = DriveInfo).
        internal Func<string, long>? FreeSpaceOverride { get; set; }

        public OptimizeResult Optimize(long? budgetBytes = null)
        {
            if (budgetBytes != null && budgetBytes.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(budgetBytes), "budget must be a positive byte count");
            var before = StoreSize();
            // Phase 1: headroom guard FIRST — refuses before any mutation.
            GuardHeadroom();
            // Phase 2: compact (orphan sweep via GC + dense rewrite) + verify.
            PreGcSweep();
            RewritePacks(LiveHexAll());
            var afterCompact = StoreSize();
            VerifyAll();
            // Phase 3: budget prune, oldest prunable first, only with --bytes.
            var pruned = new List<int>();
            if (budgetBytes != null)
                PruneToBudget(budgetBytes.Value, pruned);
            var after = StoreSize();
            if (pruned.Count > 0)
                VerifyAll();
            var report = pruned.Count == 0
                ? $"store {before} -> {after} (compact freed {before - afterCompact})"
                : $"store {before} -> {after} (compact freed {before - afterCompact}, pruned gens {string.Join(",", pruned)}: {afterCompact - after})";
            _log.Info(report);
            return new OptimizeResult(before, after, before - afterCompact, pruned, afterCompact - after, report);
        }

        // Largest sealed pack vs free space on the store volume. Read-only:
        // throws before anything is mutated when a rewrite cannot fit.
        private void GuardHeadroom()
        {
            string? largestPath = null;
            var largest = 0L;
            if (Directory.Exists(_packsDir))
            {
                foreach (var path in Directory.EnumerateFiles(_packsDir, "pack-*.pack"))
                {
                    if (path.EndsWith(".tmp", StringComparison.Ordinal))
                        continue;
                    var len = new FileInfo(path).Length;
                    if (largestPath == null || len > largest)
                    {
                        largestPath = path;
                        largest = len;
                    }
                }
            }
            if (largestPath == null)
                return; // nothing to rewrite
            var free = FreeSpaceOverride != null ? FreeSpaceOverride(_packsDir) : RealFreeSpace(_packsDir);
            if (free < largest)
                throw new StoreOptimizeHeadroomException(
                    $"optimize-to-fit refused: rewriting '{largestPath}' ({largest} bytes) needs that much free space, but only {free} bytes are free on the store volume (packs dir '{_packsDir}'); free space or prune first — nothing was touched");
        }

        private static long RealFreeSpace(string packsDir)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(packsDir));
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException($"optimize-to-fit: cannot resolve volume root for '{packsDir}'");
            return new DriveInfo(root).AvailableFreeSpace;
        }

        // Crash-orphan sweep through the existing GC path: KeepLast=max
        // drops no manifests/layouts; Execute only sweeps index rows no
        // manifest references and unlinks packs with no live chunks (a
        // sealed orphan from a crash between pack seal and index upsert
        // qualifies — cleaned on the next run per the compact protocol).
        private void PreGcSweep()
        {
            var sweep = new GarbageCollector(_storeRoot, _dbPath,
                new CorpusRetentionConfig { KeepLast = int.MaxValue }, _log);
            sweep.Execute(sweep.Plan());
        }

        // Live = union of non-hole digests over ALL manifests (not retention
        // roots — compact must not strand any surviving snapshot).
        private HashSet<string> LiveHexAll()
        {
            var key = ChunkId.EnsureKey(_storeRoot);
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (gen, snap) in ManifestStore.List(_manifestsDir))
            {
                var manifest = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), key);
                foreach (var entry in manifest.Chunks)
                {
                    if (!entry.IsHole && entry.DigestHex != "")
                        live.Add(entry.DigestHex);
                }
            }
            return live;
        }

        // Dense rewrite per pack with dead bytes: live blobs are read whole
        // first (a loud read failure precedes any mutation of that pack),
        // then sealed into a new pack beside the old (fsync+rename inside
        // PackWriter), the index rows move in one transaction, and only then
        // is the old pack deleted. Already-dense packs are skipped untouched.
        private void RewritePacks(HashSet<string> liveHex)
        {
            if (!Directory.Exists(_packsDir))
                return;
            var packs = new List<string>(Directory.EnumerateFiles(_packsDir, "pack-*.pack"));
            packs.RemoveAll(p => p.EndsWith(".tmp", StringComparison.Ordinal));
            packs.Sort(StringComparer.Ordinal);
            if (packs.Count == 0)
                return;
            using var conn = SqliteBootstrap.Open(_dbPath, SqliteSchema.Corpus);
            foreach (var oldPath in packs)
            {
                var rows = PackRows(conn, Path.GetFileName(oldPath));
                if (rows.Count == 0)
                    continue; // zero-row pack: the GC sweep above owns these
                var liveRows = new List<ChunkRow>(rows.Count);
                foreach (var row in rows)
                {
                    if (liveHex.Contains(ChunkId.ToHex(row.Digest)))
                        liveRows.Add(row);
                }
                if (liveRows.Count == 0)
                    continue; // fully dead pack: GC drops those, not us
                if (liveRows.Count == rows.Count && IsDense(oldPath, liveRows))
                    continue; // already dense — untouched
                var blobs = new List<(ChunkRow Row, PackBlob Blob)>(liveRows.Count);
                foreach (var row in liveRows)
                {
                    if (row.StoredBytes > int.MaxValue || row.SizeBytes > int.MaxValue)
                        throw new InvalidDataException($"optimize-to-fit: chunk {ChunkId.ToHex(row.Digest)} in '{oldPath}' has implausible sizes (stored {row.StoredBytes}, plain {row.SizeBytes})");
                    blobs.Add((row, PackReader.ReadBlob(oldPath, row.PackOffset, (int)row.StoredBytes)));
                }
                // Live bytes fit one pack: they are a subset of the old pack.
                // Dead-but-indexed rows (impossible right after the pre-sweep,
                // tolerated if ever seen) linger until the next GC re-marks —
                // nothing reads them (all reads go manifest → index).
                var moved = new List<ChunkRow>(blobs.Count);
                var oldLen = new FileInfo(oldPath).Length;
                using (var writer = new PackWriter(_packsDir, Math.Max(PackWriter.DefaultMaxPackBytes, oldLen), _encryptHook))
                {
                    foreach (var (row, blob) in blobs)
                    {
                        var (packName, offset) = writer.Add(row.Digest, blob.Payload, (int)row.SizeBytes, blob.Codec);
                        moved.Add(new ChunkRow(row.Digest, row.SizeBytes, row.StoredBytes, packName, offset));
                    }
                }
                using (var index = new ChunkIndex(_dbPath))
                    index.UpsertBatch(moved);
                File.Delete(oldPath);
                // Stale parity must not outlive its pack. The rewritten pack
                // carries no sidecar (the knob lives on Store, unreachable
                // here) — its chunks verify from bytes, unprotected, until a
                // future ECC-on image re-packs them.
                File.Delete(PackEcc.SidecarPath(oldPath));
            }
        }

        // A pack is dense only when its trailing index matches the live
        // rows exactly: same blob count, same blob-region size. Row-count
        // equality alone lies — a swept (dead) row leaves its bytes in the
        // file. A bad trailer fails loud (seal never finished or foreign
        // bytes); encrypted-pack bytes fail here the same way they fail the
        // normal read path.
        private static bool IsDense(string packPath, List<ChunkRow> liveRows)
        {
            var len = new FileInfo(packPath).Length;
            if (len < 16)
                throw new InvalidDataException($"optimize-to-fit: pack '{packPath}' is truncated ({len} bytes)");
            Span<byte> trailer = stackalloc byte[16];
            using (var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(-16, SeekOrigin.End);
                var filled = 0;
                while (filled < 16)
                {
                    var n = stream.Read(trailer.Slice(filled));
                    if (n == 0)
                        throw new InvalidDataException($"optimize-to-fit: pack '{packPath}' has a truncated trailer");
                    filled += n;
                }
            }
            var indexOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(trailer);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(8));
            if (trailer[12] != (byte)'C' || trailer[13] != (byte)'P' || trailer[14] != (byte)'K' || trailer[15] != (byte)'1')
                throw new InvalidDataException($"optimize-to-fit: pack '{packPath}' has a corrupt trailer (seal never finished or foreign bytes)");
            var liveBlobBytes = 0L;
            foreach (var row in liveRows)
                liveBlobBytes += PackWriter.BlobHeaderBytes + row.StoredBytes;
            return count == liveRows.Count && indexOffset == liveBlobBytes;
        }

        private static List<ChunkRow> PackRows(SqliteConnection conn, string packName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT digest, size_bytes, stored_bytes, pack_offset FROM chunks WHERE pack = $p ORDER BY pack_offset";
            cmd.Parameters.AddWithValue("$p", packName);
            var rows = new List<ChunkRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new ChunkRow((byte[])reader.GetValue(0), reader.GetInt64(1), reader.GetInt64(2), packName, reader.GetInt64(3)));
            return rows;
        }

        private void VerifyAll()
        {
            var failed = new List<int>();
            using (var store = new Store(_storeRoot, _dbPath, _chunkMib, _zstdLevel, _log, _encryptHook, _deltaCodec))
            {
                foreach (var (gen, _) in ManifestStore.List(_manifestsDir))
                {
                    if (!store.Verify(gen))
                        failed.Add(gen);
                }
            }
            if (failed.Count > 0)
                throw new StoreOptimizeVerifyException(
                    $"optimize-to-fit: verify FAILED after compact in '{_storeRoot}': gens {string.Join(",", failed)} corrupt (see log); store left as-is, re-image or restore before retrying");
        }

        // One probe at KeepLast=1 fixes the oldest-first droppable order
        // (retention semantics stay inside GC — never reimplemented here);
        // then each target drops via its own GC plan sized to take exactly
        // that gen, with a compact + size recheck between prunes so the loop
        // stops at the right generation instead of over-pruning.
        private void PruneToBudget(long budget, List<int> pruned)
        {
            var probe = new GarbageCollector(_storeRoot, _dbPath, RetentionWith(1), _log).Plan();
            var droppable = ManifestGens(probe.DropManifests);
            foreach (var target in droppable)
            {
                if (StoreSize() <= budget)
                    break;
                var remaining = RemainingGens();
                if (!remaining.Contains(target))
                    continue;
                var newer = 0;
                foreach (var gen in remaining)
                {
                    if (gen > target)
                        newer++;
                }
                if (newer < 1)
                    continue; // target is newest now (retention raced) — the final check reports
                var gc = new GarbageCollector(_storeRoot, _dbPath, RetentionWith(newer), _log);
                var plan = gc.Plan();
                var dropped = ManifestGens(plan.DropManifests);
                if (dropped.Count != 1 || dropped[0] != target)
                    continue; // retention flipped mid-run (ms race) — skip, the final check reports
                gc.Execute(plan);
                pruned.Add(target);
                RewritePacks(LiveHexAll());
            }
            var size = StoreSize();
            if (size > budget)
            {
                var remaining = RemainingGens();
                throw new StoreOptimizeBudgetException(
                    $"optimize-to-fit: budget {budget} bytes unreachable — store '{_storeRoot}' is {size} bytes after pruning gens [{string.Join(",", pruned)}]; blocked by {string.Join(", ", BlockingReasons(remaining))}");
            }
        }

        private List<int> RemainingGens()
        {
            var remaining = new List<int>();
            foreach (var (gen, _) in ManifestStore.List(_manifestsDir))
                remaining.Add(gen);
            return remaining;
        }

        private CorpusRetentionConfig RetentionWith(int keepLast) =>
            new() { KeepLast = keepLast, KeepWithinDays = _retention.KeepWithinDays, Pin = new List<int>(_retention.Pin ?? new List<int>()) };

        private static List<int> ManifestGens(IEnumerable<string> manifestPaths)
        {
            var gens = new List<int>();
            foreach (var path in manifestPaths)
            {
                var file = Path.GetFileName(path);
                if (file.Length > 7 && file[6] == '-' && int.TryParse(file.AsSpan(0, 6), out var gen))
                    gens.Add(gen);
                else
                    throw new InvalidDataException($"optimize-to-fit: cannot parse generation from manifest path '{path}'");
            }
            gens.Sort();
            return gens;
        }

        // Message text only — the prune decisions come from GC plans. Mirrors
        // the retention roots (pins, keep-within window, keep-last floor) so
        // the operator sees exactly what blocks the budget.
        private List<string> BlockingReasons(List<int> remainingSorted)
        {
            var names = new Dictionary<int, string>();
            foreach (var (gen, snap) in ManifestStore.List(_manifestsDir))
                names[gen] = snap;
            var key = ChunkId.EnsureKey(_storeRoot);
            var newest = remainingSorted.Count > 0 ? remainingSorted[remainingSorted.Count - 1] : 0;
            var pins = new HashSet<int>(_retention.Pin ?? new List<int>());
            var now = DateTime.UtcNow;
            var parts = new List<string>(remainingSorted.Count);
            foreach (var gen in remainingSorted)
            {
                var reasons = new List<string>(3);
                if (gen == newest)
                    reasons.Add("newest (keep-last floor)");
                if (pins.Contains(gen))
                    reasons.Add("pinned");
                if (_retention.KeepWithinDays > 0 && names.TryGetValue(gen, out var snap))
                {
                    var manifest = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), key);
                    if (!DateTime.TryParse(manifest.CreatedUtc, out var created))
                        reasons.Add("retained (unparseable date)");
                    else if ((now - created.ToUniversalTime()).TotalDays <= _retention.KeepWithinDays)
                        reasons.Add($"within keep_within_days={_retention.KeepWithinDays}");
                }
                if (reasons.Count == 0)
                    reasons.Add("not prunable this run");
                parts.Add($"gen {gen} ({string.Join("+", reasons)})");
            }
            return parts;
        }

        private long StoreSize()
        {
            if (!Directory.Exists(_storeRoot))
                return 0;
            var total = 0L;
            foreach (var path in Directory.EnumerateFiles(_storeRoot, "*", SearchOption.AllDirectories))
                total += new FileInfo(path).Length;
            return total;
        }
    }
}
