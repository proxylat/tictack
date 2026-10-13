using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Mark-sweep GC over refcounts (self-healing: a stale mark is just
    // recomputed next run). Reachability = union of retained manifests;
    // incremental manifests are self-contained so parent links are NEVER
    // walked — pruning a parent only breaks --chain provenance, never data.
    // Disk layouts drop iff ALL referenced partition gens drop (a layout
    // survives while any referenced partition is restorable).
    public sealed record GcPlan(
        List<string> DropManifests,
        List<string> DropPacks,
        long DeadChunks,
        long DeadBytes,
        List<string> Notes,
        List<string> DropLayouts);

    public sealed class GarbageCollector
    {
        private readonly string _storeRoot;
        private readonly string _packsDir;
        private readonly string _manifestsDir;
        private readonly string _dbPath;
        private readonly CorpusRetentionConfig _retention;
        private readonly ILogger _log;

        public GarbageCollector(string storeRoot, string dbPath, CorpusRetentionConfig retention, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(log);
            _storeRoot = storeRoot;
            _packsDir = Path.Combine(storeRoot, "packs");
            _manifestsDir = Path.Combine(storeRoot, "manifests");
            _dbPath = dbPath;
            _retention = retention;
            _log = log;
        }

        public GcPlan Plan()
        {
            var listed = ManifestStore.List(_manifestsDir);
            // Load EVERYTHING before any mutation: a corrupt (HMAC-failing)
            // manifest throws here, leaving store + index untouched. Layouts
            // load here too (corrupt layout aborts the same clean way).
            var key = ChunkId.EnsureKey(_storeRoot);
            var manifests = new Dictionary<int, (string Path, SnapshotManifest Manifest)>();
            foreach (var (gen, name) in listed)
            {
                var path = Path.Combine(_manifestsDir, $"{gen:D6}-{name}.json");
                manifests[gen] = (path, ManifestStore.Load(path, key));
            }
            var layouts = new List<(string Path, DiskLayout Layout)>();
            var layoutsDir = Path.Combine(_storeRoot, "layouts");
            if (Directory.Exists(layoutsDir))
            {
                var layoutPaths = new List<string>(Directory.EnumerateFiles(layoutsDir, "*.json"));
                layoutPaths.Sort(StringComparer.Ordinal);
                foreach (var path in layoutPaths)
                    layouts.Add((path, LayoutStore.Load(path, key)));
            }

            var notes = new List<string>();
            var roots = new HashSet<int>();
            // Keep-last-N by generation.
            var gens = new List<int>(manifests.Keys);
            gens.Sort();
            for (var i = Math.Max(0, gens.Count - Math.Max(1, _retention.KeepLast)); i < gens.Count; i++)
                roots.Add(gens[i]);
            // Keep-within by CreatedUtc age; unparseable dates fail safe to
            // newest (retained). 0 = off.
            if (_retention.KeepWithinDays > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var (gen, (_, m)) in manifests)
                {
                    if (!DateTime.TryParse(m.CreatedUtc, out var created))
                    {
                        roots.Add(gen);
                        continue;
                    }
                    if ((now - created.ToUniversalTime()).TotalDays <= _retention.KeepWithinDays)
                        roots.Add(gen);
                }
            }
            // Explicit pins; unknown generations warn and are ignored.
            foreach (var pin in _retention.Pin ?? new List<int>())
            {
                if (manifests.ContainsKey(pin))
                    roots.Add(pin);
                else
                    notes.Add($"unknown pinned gen {pin} ignored");
            }

            var drops = new List<int>();
            foreach (var gen in gens)
            {
                if (!roots.Contains(gen))
                    drops.Add(gen);
            }
            // Layout rule: L (referencing gens G1..Gn, SnapshotGeneration > 0;
            // 0 = skipped/never, never restorable) drops iff every referenced
            // gen is dropped or already gone. Retention roots are manifest
            // gens only — the pool is shared between volume and disk snapshots.
            var dropSet = new HashSet<int>(drops);
            var dropLayouts = new List<string>();
            foreach (var (layoutPath, layout) in layouts)
            {
                var survives = false;
                foreach (var part in layout.Partitions)
                {
                    if (part.SnapshotGeneration > 0
                        && manifests.ContainsKey(part.SnapshotGeneration)
                        && !dropSet.Contains(part.SnapshotGeneration))
                    {
                        survives = true;
                        break;
                    }
                }
                if (!survives)
                    dropLayouts.Add(layoutPath);
            }
            // Pruning a parent of a retained child: data unaffected (child is
            // self-contained), only --chain provenance gaps.
            foreach (var drop in drops)
            {
                foreach (var gen in gens)
                {
                    if (roots.Contains(gen) && manifests[gen].Manifest.ParentGeneration == drop)
                    {
                        notes.Add($"pruning gen {drop} breaks --chain provenance for its children (data unaffected)");
                        break;
                    }
                }
            }

            // Live = union of non-hole digests over ROOT manifests only.
            // Digest strings need no key threading (identity binds the key).
            var liveHex = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gen in gens)
            {
                if (!roots.Contains(gen))
                    continue;
                foreach (var entry in manifests[gen].Manifest.Chunks)
                {
                    if (!entry.IsHole && entry.DigestHex != "")
                        liveHex.Add(entry.DigestHex);
                }
            }
            var live = new List<byte[]>(liveHex.Count);
            foreach (var hex in liveHex)
                live.Add(Convert.FromHexString(hex));

            // Marking is idempotent bookkeeping, not a mutation of user data.
            using (var index = new ChunkIndex(_dbPath))
            {
                index.SetAllMarks(0);
                index.MarkBatch(live);
                var (deadChunks, deadBytes) = index.UnmarkedStats();
                var livePacks = new HashSet<string>(index.LivePacks(), StringComparer.Ordinal);

                // Dead packs = pack-*.pack with no live chunk. *.tmp is never
                // touched (a crash-mid-seal tmp is PackWriter business, and
                // the pattern below can't match it anyway — belt + braces).
                var dropPacks = new List<string>();
                if (Directory.Exists(_packsDir))
                {
                    foreach (var path in Directory.EnumerateFiles(_packsDir, "pack-*.pack"))
                    {
                        if (path.EndsWith(".tmp", StringComparison.Ordinal))
                            continue;
                        if (!livePacks.Contains(Path.GetFileName(path)))
                            dropPacks.Add(path);
                    }
                }
                dropPacks.Sort(StringComparer.Ordinal);

                var dropManifests = new List<string>();
                foreach (var drop in drops)
                    dropManifests.Add(manifests[drop].Path);
                return new GcPlan(dropManifests, dropPacks, deadChunks, deadBytes, notes, dropLayouts);
            }
        }

        public void Execute(GcPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            // Crash-safe order: manifests + layouts first, then index rows,
            // then pack files. A crash between steps only orphans pack
            // bytes — never a live chunk. Single-writer assumption: no
            // concurrent image/GC.
            foreach (var path in plan.DropManifests)
                File.Delete(path);
            foreach (var path in plan.DropLayouts)
                File.Delete(path);
            using (var index = new ChunkIndex(_dbPath))
                index.SweepUnmarked();
            // shortcut: whole-pack delete only, no compaction (compaction
            // needs its own crash protocol; per-image packs make unique packs
            // go fully dead, so survivors stay rare).
            foreach (var path in plan.DropPacks)
                File.Delete(path);
            _log.Info($"gc: dropped {plan.DropManifests.Count} snapshots, {plan.DeadChunks} chunks, {plan.DeadBytes} bytes, {plan.DropPacks.Count} packs");
        }
    }
}
