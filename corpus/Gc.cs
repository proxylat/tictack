using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
            // Keep-last-N by generation.
            var gens = new List<int>(manifests.Keys);
            gens.Sort();
            var roots = ComputeRoots(manifests, gens, notes);

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
            // §12.7: a delta counts as a reference to its base digest —
            // pruning a parent keeps bases while any retained delta
            // references them (extends whole-chain expiry to chunk pairs).
            var liveHex = new HashSet<string>(StringComparer.Ordinal);
            var deltaRefs = 0;
            foreach (var gen in gens)
            {
                if (!roots.Contains(gen))
                    continue;
                foreach (var entry in manifests[gen].Manifest.Chunks)
                {
                    if (!entry.IsHole && entry.DigestHex != "")
                        liveHex.Add(entry.DigestHex);
                    if (!entry.IsHole && !string.IsNullOrEmpty(entry.BaseDigestHex))
                    {
                        liveHex.Add(entry.BaseDigestHex);
                        deltaRefs++;
                    }
                }
            }
            if (deltaRefs > 0)
                notes.Add($"{deltaRefs} delta entries pin their base chunks (depth-1, pruned parents stay readable)");
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

        // Retention roots shared by Plan (liveness) and LiveDigestHex (idle
        // optimize filter): keep-last-N ∪ keep-within ∪ pins.
        private HashSet<int> ComputeRoots(Dictionary<int, (string Path, SnapshotManifest Manifest)> manifests, List<int> gens, List<string> notes)
        {
            var roots = new HashSet<int>();
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
            return roots;
        }

        // Retained-manifest digest union WITHOUT touching index marks: the
        // idle optimizer's filter (landing chunks outside this set drop
        // unpacked). HMAC-checked loads — a corrupt manifest throws before
        // any mutation, same as Plan.
        public HashSet<string> LiveDigestHex()
        {
            var key = ChunkId.EnsureKey(_storeRoot);
            var manifests = new Dictionary<int, (string Path, SnapshotManifest Manifest)>();
            foreach (var (gen, name) in ManifestStore.List(_manifestsDir))
            {
                var path = Path.Combine(_manifestsDir, $"{gen:D6}-{name}.json");
                manifests[gen] = (path, ManifestStore.Load(path, key));
            }
            var gens = new List<int>(manifests.Keys);
            gens.Sort();
            var roots = ComputeRoots(manifests, gens, new List<string>());
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gen in gens)
            {
                if (!roots.Contains(gen))
                    continue;
                foreach (var entry in manifests[gen].Manifest.Chunks)
                {
                    if (!entry.IsHole && entry.DigestHex != "")
                        live.Add(entry.DigestHex);
                    // §12.7 pin, same as Plan: deltas reference their bases.
                    if (!entry.IsHole && !string.IsNullOrEmpty(entry.BaseDigestHex))
                        live.Add(entry.BaseDigestHex);
                }
            }
            return live;
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

    // Scheduled cadence (TimerScheduler pattern, file stamps instead of
    // jobs.db — Corpus links no JobRunStore): frequent idle optimize, daily
    // GC. Manual --gc is untouched (still needs --confirm); the schedule is
    // operator pre-consent, so scheduled runs auto-execute.
    public sealed record GcSchedule(TimeSpan GcDailyAt, TimeSpan OptimizeEvery)
    {
        public static GcSchedule Default { get; } = new(new TimeSpan(2, 0, 0), TimeSpan.FromMinutes(15));
    }

    public sealed class GcScheduler : IDisposable
    {
        private readonly string _storeRoot;
        private readonly string _dbPath;
        private readonly CorpusRetentionConfig _retention;
        private readonly ILogger _log;
        private readonly int _chunkMib;
        private readonly int _zstdLevel;
        private readonly string? _encryptHook;
        private readonly GcSchedule _schedule;
        private readonly string _gcStamp;
        private readonly string _optStamp;
        private readonly object _runLock = new();
        private Timer? _timer;
        private bool _disposed;
        private bool _running;

        public GcScheduler(string storeRoot, string dbPath, CorpusRetentionConfig retention, ILogger log, int chunkMib, int zstdLevel, GcSchedule? schedule = null, string? encryptHook = null)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(log);
            if (chunkMib < 1) throw new ArgumentOutOfRangeException(nameof(chunkMib));
            if (zstdLevel < 1) throw new ArgumentOutOfRangeException(nameof(zstdLevel));
            _storeRoot = storeRoot;
            _dbPath = dbPath;
            _retention = retention;
            _log = log;
            _chunkMib = chunkMib;
            _zstdLevel = zstdLevel;
            _encryptHook = string.IsNullOrWhiteSpace(encryptHook) ? null : encryptHook;
            _schedule = schedule ?? GcSchedule.Default;
            var stateDir = Path.GetDirectoryName(dbPath) ?? storeRoot;
            _gcStamp = Path.Combine(stateDir, "gc-last-run");
            _optStamp = Path.Combine(stateDir, "optimize-last-run");
        }

        // Pure due-checks mirroring TimerScheduler.IsDue / IsDueInterval
        // (catch-up: a missed day stays due; same-day runs never re-fire).
        public static bool IsDueDaily(DateTime now, DateTime lastRun, TimeSpan timeOfDay)
        {
            var today = now.Date;
            if (lastRun >= today) return false;
            if (now.TimeOfDay >= timeOfDay) return true;
            if (lastRun < today.AddDays(-1)) return true;
            return false;
        }

        public static bool IsDueInterval(DateTime now, DateTime lastRunAt, TimeSpan interval) =>
            lastRunAt == DateTime.MinValue || now - lastRunAt >= interval;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer != null) return;
            SeedStamp(_gcStamp);
            SeedStamp(_optStamp);
            _log.Info($"gc scheduler started (gc daily at {_schedule.GcDailyAt}, optimize every {_schedule.OptimizeEvery})");
            _timer = new Timer(_ => Tick(DateTime.Now), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        // Fresh stamps seed to now (TimerScheduler fresh-store rule): a new
        // box waits for its first slot instead of collecting on startup.
        private void SeedStamp(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                    File.WriteAllText(path, DateTime.Now.ToString("o"));
            }
            catch (Exception ex)
            {
                _log.Debug("gc stamp seed failed for '" + path + "': " + ex.Message);
            }
        }

        // Missing/corrupt stamps read as due: the work is retention-bounded
        // (Plan decides), never destructive beyond policy.
        private static DateTime ReadStamp(string path)
        {
            try
            {
                if (File.Exists(path) && DateTime.TryParse(File.ReadAllText(path), out var at))
                    return at;
            }
            catch { }
            return DateTime.MinValue;
        }

        private void WriteStamp(string path, DateTime now)
        {
            try
            {
                File.WriteAllText(path, now.ToString("o"));
            }
            catch (Exception ex)
            {
                _log.Debug("gc stamp write failed for '" + path + "': " + ex.Message);
            }
        }

        // One tick: GC first (drops manifests per retention), then optimize
        // (folds landing; a GC run implies an optimize pass so newly-dead
        // landing files drop without packing). Overlap = skip, never stack.
        internal void Tick(DateTime now)
        {
            if (_disposed) return;
            lock (_runLock)
            {
                if (_running) return;
                _running = true;
            }
            try
            {
                var gcDue = IsDueDaily(now, ReadStamp(_gcStamp), _schedule.GcDailyAt);
                var optDue = IsDueInterval(now, ReadStamp(_optStamp), _schedule.OptimizeEvery);
                if (gcDue)
                    RunGc(now);
                if (optDue || gcDue)
                    RunOptimize(now);
            }
            catch (Exception ex)
            {
                _log.Error("scheduled gc tick failed", ex);
            }
            finally
            {
                lock (_runLock) { _running = false; }
            }
        }

        private void RunGc(DateTime now)
        {
            var gc = new GarbageCollector(_storeRoot, _dbPath, _retention, _log);
            var plan = gc.Plan();
            if (plan.DropManifests.Count == 0 && plan.DropPacks.Count == 0 && plan.DropLayouts.Count == 0)
                _log.Info("scheduled gc: nothing to collect");
            else
                gc.Execute(plan);
            WriteStamp(_gcStamp, now);
        }

        private void RunOptimize(DateTime now)
        {
            var gc = new GarbageCollector(_storeRoot, _dbPath, _retention, _log);
            using var store = new Store(_storeRoot, _dbPath, _chunkMib, _zstdLevel, _log, _encryptHook);
            if (store.PendingLandingCount() == 0)
            {
                WriteStamp(_optStamp, now);
                return;
            }
            var (packed, deduped, dropped) = store.OptimizeLanding(gc.LiveDigestHex());
            _log.Info($"scheduled optimize: {packed} packed, {deduped} deduped, {dropped} dropped");
            WriteStamp(_optStamp, now);
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
            }
            _timer = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
