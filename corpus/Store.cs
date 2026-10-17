using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    // §12.8: thrown when skip-if-unchanged finds zero new chunks. Carries the
    // gen the skip line names. Store already logs the exact line
    // (`unchanged since gen N, skipping`) at INFO before throwing, so the CLI
    // maps this to exit 0 WITHOUT logging again (else the line appears twice).
    public sealed class StoredSkippedException : Exception
    {
        public int Generation { get; }

        public StoredSkippedException(int generation)
            : base($"unchanged since gen {generation}, skipping")
        {
            Generation = generation;
        }
    }

    // Store orchestrator: chunk → id → dedup → pack → index → manifest.
    // One PackWriter per Image call; the ChunkIndex stays open for the
    // Store lifetime so repeated images share one connection.
    public sealed class Store : IDisposable
    {
        private readonly string _packsDir;
        private readonly string _manifestsDir;
        private readonly string _layoutsDir;
        private readonly string _landingDir;
        private readonly int _chunkMib;
        private readonly int _zstdLevel;
        private readonly ILogger _log;
        private readonly byte[] _key;
        private readonly string? _encryptHook;
        private readonly ChunkIndex _index;
        private bool _disposed;

        // §12.4 bounded parallel-encode window: collect (digest, data)
        // pairs, Drain parallel-encodes (order-preserving) and pack-adds
        // sequentially. Auto-drains at ~core count / 64 MiB so in-flight
        // RAM never grows with volume size; the caller's 500-row index
        // flush cadence is unchanged (Drain BEFORE Upsert+Clear — the
        // clear must never strand un-packed chunks uncovered by the
        // intra-image dedup set).
        private sealed class EncodeWindow
        {
            private readonly PackWriter _pack;
            private readonly List<ChunkRow> _pending;
            private readonly List<string>? _paths;
            private readonly int _level;
            private readonly int _maxCount;
            private readonly List<(byte[] Digest, byte[] Data, string? Path)> _work = new();
            private long _bytes;

            public EncodeWindow(PackWriter pack, List<ChunkRow> pending, int level, List<string>? paths = null)
            {
                _pack = pack;
                _pending = pending;
                _paths = paths;
                _level = level;
                _maxCount = Math.Clamp(Environment.ProcessorCount, 2, 16);
            }

            public void Add(byte[] digest, byte[] data, string? path = null)
            {
                _work.Add((digest, data, path));
                _bytes += data.Length;
                if (_work.Count >= _maxCount || _bytes >= 64L * 1024 * 1024)
                    Drain();
            }

            public void Drain()
            {
                if (_work.Count == 0)
                    return;
                var plains = new List<byte[]>(_work.Count);
                foreach (var w in _work)
                    plains.Add(w.Data);
                var encoded = Compression.EncodeBatch(plains, _level);
                for (var i = 0; i < _work.Count; i++)
                {
                    var (packName, offset) = _pack.Add(_work[i].Digest, encoded[i].Payload, _work[i].Data.Length, encoded[i].Codec);
                    _pending.Add(new ChunkRow(_work[i].Digest, _work[i].Data.Length, encoded[i].Payload.Length, packName, offset));
                    if (_paths != null)
                        _paths.Add(_work[i].Path!);
                }
                _work.Clear();
                _bytes = 0;
            }
        }

        // §12.10 image progress: restore-shaped % + MB/s log lines at 2 Hz
        // (`image 42% (156.3 MB/s)`). Mirrors RestoreOrchestrator's
        // private reporter (same throttle, same math, image-worded) — no
        // shared abstraction fell out. No \r/TTY bars ever: logs are the
        // surface (file logs + service context). Synchronous delivery
        // keeps progress monotonic.
        private sealed class ImageProgress : IProgress<long>
        {
            private readonly ILogger _log;
            private readonly long _total;
            private readonly Stopwatch _sw;
            private long _lastMs = -1000;

            public ImageProgress(ILogger log, long total)
            {
                _log = log;
                _total = total;
                _sw = Stopwatch.StartNew();
            }

            public void Report(long bytes)
            {
                var ms = _sw.ElapsedMilliseconds;
                if (ms - _lastMs < 500 && bytes < _total)
                    return;
                _lastMs = ms;
                var pct = _total <= 0 ? 100 : bytes * 100 / _total;
                var mbps = _sw.Elapsed.TotalSeconds > 0 ? bytes / _sw.Elapsed.TotalSeconds / (1024 * 1024) : 0;
                _log.Info($"image {pct}% ({mbps:F1} MB/s)");
            }
        }

        // Disk aggregate: per-partition local bytes shifted into
        // disk-cumulative, so N partitions emit one monotonic series
        // (mirrors RestoreOrchestrator.OffsetForwarder).
        private sealed class OffsetProgress : IProgress<long>
        {
            private readonly IProgress<long> _inner;
            private readonly long _base;

            public OffsetProgress(IProgress<long> inner, long completedBefore)
            {
                _inner = inner;
                _base = completedBefore;
            }

            public void Report(long bytes) => _inner.Report(_base + bytes);
        }

        // Planned data bytes for full/landing captures: volume length
        // minus hole bytes (sparse sources report data bytes only, so
        // the denominator must exclude holes to end at 100%).
        private static long PlannedDataBytes(long volumeLength, int chunkBytes, bool[]? holeMap)
        {
            if (holeMap == null)
                return volumeLength;
            var holes = 0L;
            for (var i = 0; i < holeMap.Length; i++)
            {
                if (holeMap[i])
                    holes += Math.Min((long)chunkBytes, volumeLength - i * (long)chunkBytes);
            }
            return volumeLength - holes;
        }

        public Store(string storeRoot, string dbPath, int chunkMib, int zstdLevel, ILogger log, string? encryptHook = null)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(log);
            _packsDir = Path.Combine(storeRoot, "packs");
            _manifestsDir = Path.Combine(storeRoot, "manifests");
            _layoutsDir = Path.Combine(storeRoot, "layouts");
            _landingDir = Path.Combine(storeRoot, "landing");
            Directory.CreateDirectory(_packsDir);
            Directory.CreateDirectory(_manifestsDir);
            _chunkMib = chunkMib;
            _zstdLevel = zstdLevel;
            _log = log;
            _key = ChunkId.EnsureKey(storeRoot);
            _encryptHook = string.IsNullOrWhiteSpace(encryptHook) ? null : encryptHook;
            _index = new ChunkIndex(dbPath);
        }

        public SnapshotManifest Image(IBlockSource source, string name) =>
            Image(source, name, new ImageOptions());

        // §12.8 skipIfUnchanged: --force-full wins (explicit full means full);
        // no parent gen never skips (first image always runs). Empty plan →
        // skip before ANY bulk read; non-empty plan → read+hash, zero new
        // chunks → the manifest write is withheld. Throws
        // StoredSkippedException (exit 0 at the CLI) instead of returning.
        public SnapshotManifest Image(IBlockSource source, string name, ImageOptions opts, bool skipIfUnchanged = false, IProgress<long>? progress = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(opts);
            var generation = ManifestStore.NextGeneration(_manifestsDir);
            var existing = ManifestStore.List(_manifestsDir);
            SnapshotManifest? latest = null;
            if (existing.Count > 0)
            {
                var (gen, snap) = existing[existing.Count - 1];
                latest = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), _key);
            }
            var prevDigest = latest?.Hmac ?? "";
            SnapshotManifest? parent = null;
            if (!opts.ForceFull && (opts.Incremental || opts.ParentGeneration > 0))
            {
                int? wantGen = opts.ParentGeneration > 0 ? opts.ParentGeneration : latest?.Generation;
                if (wantGen == null)
                {
                    _log.Info($"no parent snapshot, full image ('{name}' will be gen {generation})");
                }
                else if (latest != null && wantGen.Value == latest.Generation)
                {
                    parent = CheckParent(latest, source);
                }
                else
                {
                    string? parentName = null;
                    foreach (var (gen, snap) in existing)
                    {
                        if (gen == wantGen.Value)
                        {
                            parentName = snap;
                            break;
                        }
                    }
                    if (parentName == null)
                    {
                        if (opts.ParentGeneration > 0)
                            throw new InvalidDataException($"--parent gen {wantGen.Value} not found in {_manifestsDir}");
                        _log.Info($"parent gen {wantGen.Value} missing, full image");
                    }
                    else
                    {
                        // HMAC-checked; corrupt → throw (never image onto a corrupt parent).
                        parent = CheckParent(ManifestStore.Load(Path.Combine(_manifestsDir, $"{wantGen.Value:D6}-{parentName}.json"), _key), source);
                    }
                }
            }
            if (parent != null)
            {
                parent = CheckRelocation(parent, source, opts);
                parent = parent != null ? CheckExclusionSet(parent, opts, source) : null;
            }
            if (parent != null)
            {
                var plan = (opts.ChangeSource ?? DefaultChangeSource()).Plan(parent, source, _chunkMib * 1024 * 1024, _log);
                if (plan != null)
                {
                    // Empty plan = skip before ANY bulk read (the plan itself
                    // is MFT/journal metadata only, never volume bytes).
                    if (skipIfUnchanged && !opts.ForceFull && plan.DirtyChunks.Count == 0)
                        SkipUnchanged(parent.Generation);
                    return ImageIncremental(source, name, generation, prevDigest, parent, plan, opts, skipIfUnchanged && !opts.ForceFull, progress);
                }
            }
            return ImageFull(source, name, generation, prevDigest, opts,
                skipIfUnchanged && !opts.ForceFull && existing.Count > 0 ? existing[existing.Count - 1].Generation : null, progress);
        }

        private static IChangeSource DefaultChangeSource() => new MftDigestChangeSource();

        // Item 1-minimal guard: fingerprint mismatch proves defrag/optimize
        // relocated data without USN entries → reused digests would point at
        // stale locations. Fall back to full re-read (loud), never wrong data.
        private SnapshotManifest? CheckRelocation(SnapshotManifest parent, IBlockSource source, ImageOptions opts)
        {
            if (CapturePolicy.FingerprintMismatch(parent.LayoutFingerprint, CurrentFingerprint(source, opts)))
            {
                _log.Warn($"full image: layout fingerprint changed since gen {parent.Generation} (defrag/relocation?), re-reading all chunks");
                return null;
            }
            return parent;
        }

        // Exclusion-set change → full: clean chunks still carry the old set's
        // bytes, so reuse would contradict the child's skip record.
        private SnapshotManifest? CheckExclusionSet(SnapshotManifest parent, ImageOptions opts, IBlockSource source)
        {
            var current = CurrentExclusionNames(opts, source);
            if (CapturePolicy.ExclusionSetChanged(parent.SkippedExclusions, current.Count > 0 ? current : null))
            {
                _log.Info($"full image: exclusion set changed since gen {parent.Generation}, re-reading all chunks");
                return null;
            }
            return parent;
        }

        private static IExclusionResolver? ResolveExclusions(IBlockSource source, ImageOptions opts) =>
            opts.Exclusions ?? source as IExclusionResolver;

        private static IReadOnlyList<string> CurrentExclusionNames(ImageOptions opts, IBlockSource source) =>
            ResolveExclusions(source, opts)?.ExclusionNames ?? (IReadOnlyList<string>)Array.Empty<string>();

        // CLI --exclude (repeatable) merges over the config policy:
        // config-effective names ∪ CLI names, $LogFile stripped again,
        // case-insensitive dedup. Explicit config never regains defaults
        // through the union.
        internal static IReadOnlyList<string> MergeExclusionNames(IReadOnlyList<string>? configured, IReadOnlyList<string> cli)
        {
            ArgumentNullException.ThrowIfNull(cli);
            var merged = new List<string>(CapturePolicy.EffectiveSkip(configured));
            var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
            foreach (var e in CapturePolicy.EffectiveSkip(cli))
            {
                if (seen.Add(e))
                    merged.Add(e);
            }
            return merged;
        }

        private static string? CurrentFingerprint(IBlockSource source, ImageOptions opts) =>
            opts.Layout?.LayoutFingerprint() ?? (source as ILayoutFingerprintProvider)?.LayoutFingerprint();

        // MFT baseline for future digest incrementals (item 13): best-effort
        // group digests over the source's $MFT. Non-MFT sources (files, most
        // tests) yield nulls → the manifest omits the fields and the next
        // incremental uses the USN hint instead. Never throws.
        private static (List<string>? Digests, int RecordBytes) TryMftBaseline(IBlockSource source, int chunkBytes, ILogger log)
        {
            if (source is not IMftVolume vol)
                return (null, 0);
            try
            {
                return (MftDigest.ComputeGroups(vol, chunkBytes), vol.BytesPerRecord);
            }
            catch (InvalidDataException ex)
            {
                log.Info($"full image recorded without MFT digests ({ex.Message})");
                return (null, 0);
            }
        }

        // Parent usability gate: resize / re-chunk / grid drift → log the
        // reason and return null (caller falls back to full re-read).
        private SnapshotManifest? CheckParent(SnapshotManifest parent, IBlockSource source)
        {
            if (parent.VolumeLength != source.Length)
            {
                _log.Info($"full image: parent gen {parent.Generation} volume length {parent.VolumeLength} != source length {source.Length}");
                return null;
            }
            if (parent.ChunkMib != _chunkMib)
            {
                _log.Info($"full image: parent gen {parent.Generation} chunk {parent.ChunkMib} MiB != current {_chunkMib} MiB");
                return null;
            }
            var grid = GridCount(source.Length, _chunkMib);
            if (parent.Chunks.Count != grid)
            {
                _log.Info($"full image: parent gen {parent.Generation} has {parent.Chunks.Count} chunks, current grid is {grid}");
                return null;
            }
            return parent;
        }

        private static int GridCount(long volumeLength, int chunkMib)
        {
            var chunkBytes = (long)chunkMib * 1024 * 1024;
            return (int)((volumeLength + chunkBytes - 1) / chunkBytes);
        }

        private void SkipUnchanged(int generation)
        {
            _log.Info($"unchanged since gen {generation}, skipping");
            throw new StoredSkippedException(generation);
        }

        private SnapshotManifest ImageFull(IBlockSource source, string name, int generation, string prevDigest, ImageOptions opts, int? skipSince, IProgress<long>? progress = null)
        {
            var entries = new List<ManifestEntry>(GridCount(source.Length, _chunkMib));
            var pending = new List<ChunkRow>();
            // Digests packed but not yet flushed: TryGet can't see them, so
            // without this set intra-image duplicates pack twice and the
            // index ends up pointing at the last copy. Cleared on every
            // flush (≤500 entries — never grows with volume size).
            var packedThisImage = new HashSet<string>(StringComparer.Ordinal);
            var newChunks = 0;
            var totalBytes = 0L;
            var holes = 0;
            var chunkBytes = _chunkMib * 1024 * 1024;
            var excl = ResolveExclusions(source, opts);
            var exclBpc = excl?.BytesPerCluster ?? 0;
            var exclRuns = exclBpc > 0 ? excl!.ExcludedRuns() : null;
            using (var pack = new PackWriter(_packsDir, encryptHook: _encryptHook))
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes);
                var enumerable = holeMap != null
                    ? Chunker.ReadSparse(stream, chunkBytes, i => i < holeMap.Length && holeMap[i])
                    : Chunker.Read(stream, chunkBytes);
                // Denominator = planned bytes (whole volume; holes excluded
                // above). A caller-supplied progress (disk aggregate) owns
                // the throttle; direct calls get their own reporter.
                var planned = PlannedDataBytes(source.Length, chunkBytes, holeMap);
                var reporter = progress ?? new ImageProgress(_log, planned);
                var done = 0L;
                var window = new EncodeWindow(pack, pending, _zstdLevel);
                foreach (var chunk in enumerable)
                {
                    if (chunk.IsHole)
                    {
                        var holeSize = Math.Min((long)chunkBytes, source.Length - chunk.Offset);
                        entries.Add(ManifestEntry.Hole(holeSize));
                        totalBytes += holeSize;
                        holes++;
                        continue;
                    }
                    if (exclRuns != null && exclRuns.Count > 0)
                        CapturePolicy.ZeroExcludedRanges(chunk.Data, chunk.Offset, exclRuns, exclBpc);
                    var digest = ChunkId.ComputeId(chunk.Data, _key);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, chunk.Data.Length));
                    totalBytes += chunk.Data.Length;
                    done += chunk.Data.Length;
                    reporter.Report(done);
                    if (packedThisImage.Contains(hex) || _index.TryGet(digest, out _))
                        continue;
                    packedThisImage.Add(hex);
                    window.Add(digest, chunk.Data);
                    newChunks++;
                    if (pending.Count >= 500)
                    {
                        window.Drain();
                        _index.UpsertBatch(pending);
                        pending.Clear();
                        packedThisImage.Clear();
                    }
                }
                window.Drain();
                _index.UpsertBatch(pending);
                pending.Clear();
                // The loop's last data chunk already reported 100% (final
                // reports bypass the throttle); top up only when
                // throttling or holes left the series short — and always
                // for empty (all-hole) volumes.
                if (planned <= 0 || done < planned)
                    reporter.Report(planned);
            }

            // §12.8: full re-read with zero new chunks = unchanged (covers the
            // null-plan fallback path, where no empty-plan short-circuit can
            // fire). The manifest write is withheld — the gen stays free.
            // Zero-Add PackWriter seals nothing (Dispose no-ops on no stream).
            if (skipSince != null && newChunks == 0)
                SkipUnchanged(skipSince.Value);

            var (mftDigests, mftRecordBytes) = TryMftBaseline(source, chunkBytes, _log);
            var manifest = ManifestStore.Build(
                generation, name, source.Length, _chunkMib, _zstdLevel,
                ChunkId.KeyId(_key), prevDigest, entries,
                changeCursor: UsnChangeSource.TryCurrentCursor(source, _log),
                layoutFingerprint: CurrentFingerprint(source, opts),
                skippedExclusions: excl != null && excl.ExclusionNames.Count > 0 ? new List<string>(excl.ExclusionNames) : null,
                mftChunkDigests: mftDigests, mftRecordBytes: mftRecordBytes,
                bitLockerState: (source as IBitLockerVolume)?.BitLockerState,
                bitLockerKeyIds: (source as IBitLockerVolume)?.BitLockerKeyIds);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            if (manifest.SkippedExclusions != null)
                _log.Warn($"excluded extents restored as zeros: {string.Join(", ", manifest.SkippedExclusions)}");
            _log.Info($"imaged '{name}' gen {generation}: {entries.Count} chunks ({holes} holes), {newChunks} new, {totalBytes} bytes");
            return manifest;
        }

        // Incremental capture: clean chunks reuse the parent's digests with
        // ZERO reads; only dirty chunks are seek-read, hashed, and packed.
        // The child manifest is self-contained (full chunk list) — export
        // and standalone verify never touch the parent.
        private SnapshotManifest ImageIncremental(IBlockSource source, string name, int generation, string prevDigest, SnapshotManifest parent, IncrementalPlan plan, ImageOptions opts, bool skip, IProgress<long>? progress = null)
        {
            var chunkBytes = _chunkMib * 1024 * 1024;
            var grid = GridCount(source.Length, _chunkMib);
            var entries = new List<ManifestEntry>(parent.Chunks.Count);
            var pending = new List<ChunkRow>();
            var packedThisImage = new HashSet<string>(StringComparer.Ordinal);
            var excl = ResolveExclusions(source, opts);
            var exclBpc = excl?.BytesPerCluster ?? 0;
            var exclRuns = exclBpc > 0 ? excl!.ExcludedRuns() : null;
            var reread = 0;
            var newChunks = 0;
            using (var pack = new PackWriter(_packsDir, encryptHook: _encryptHook))
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes);
                // Denominator = digest-plan total: dirty non-hole chunk
                // bytes (clean chunks cost zero reads, holes win over
                // dirty — neither advances the numerator).
                var planned = 0L;
                for (var i = 0; i < grid; i++)
                {
                    if (!plan.DirtyChunks.Contains(i))
                        continue;
                    if (holeMap != null && i < holeMap.Length && holeMap[i])
                        continue;
                    planned += Math.Min((long)chunkBytes, source.Length - i * (long)chunkBytes);
                }
                var reporter = progress ?? new ImageProgress(_log, planned);
                var done = 0L;
                var window = new EncodeWindow(pack, pending, _zstdLevel);
                for (var i = 0; i < grid; i++)
                {
                    if (holeMap != null && i < holeMap.Length && holeMap[i])
                    {
                        entries.Add(ManifestEntry.Hole(Math.Min((long)chunkBytes, source.Length - i * (long)chunkBytes)));
                        continue; // hole wins over dirty — no read either way
                    }
                    if (!plan.DirtyChunks.Contains(i))
                    {
                        entries.Add(parent.Chunks[i]); // clean: reuse digest, ZERO reads
                        continue;
                    }
                    var data = ReadChunkAt(stream, i, chunkBytes, source.Length);
                    reread++;
                    if (exclRuns != null && exclRuns.Count > 0)
                        CapturePolicy.ZeroExcludedRanges(data, i * (long)chunkBytes, exclRuns, exclBpc);
                    var digest = ChunkId.ComputeId(data, _key);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, data.Length));
                    done += data.Length;
                    reporter.Report(done);
                    if (packedThisImage.Contains(hex) || _index.TryGet(digest, out _))
                        continue;
                    packedThisImage.Add(hex);
                    window.Add(digest, data);
                    newChunks++;
                    if (pending.Count >= 500)
                    {
                        window.Drain();
                        _index.UpsertBatch(pending);
                        pending.Clear();
                        packedThisImage.Clear();
                    }
                }
                window.Drain();
                _index.UpsertBatch(pending);
                pending.Clear();
                if (planned <= 0 || done < planned)
                    reporter.Report(planned);
            }

            // §12.8: dirty-but-deduped (every re-read chunk already indexed) =
            // unchanged. Withhold the manifest write — the gen stays free.
            if (skip && newChunks == 0)
                SkipUnchanged(parent.Generation);

            var manifest = ManifestStore.Build(
                generation, name, source.Length, _chunkMib, _zstdLevel,
                ChunkId.KeyId(_key), prevDigest, entries, parent.Generation, plan.NewCursor,
                layoutFingerprint: CurrentFingerprint(source, opts),
                skippedExclusions: excl != null && excl.ExclusionNames.Count > 0 ? new List<string>(excl.ExclusionNames) : null,
                mftChunkDigests: plan.NewMftDigests, mftRecordBytes: plan.NewMftRecordBytes,
                bitLockerState: (source as IBitLockerVolume)?.BitLockerState,
                bitLockerKeyIds: (source as IBitLockerVolume)?.BitLockerKeyIds);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            _log.Info($"incremental gen {generation} from parent {parent.Generation}: {reread}/{grid} chunks re-read, {newChunks} new");
            return manifest;
        }

        private static byte[] ReadChunkAt(Stream stream, long index, int chunkBytes, long volumeLength)
        {
            stream.Seek(index * (long)chunkBytes, SeekOrigin.Begin);
            var want = (int)Math.Min((long)chunkBytes, volumeLength - index * (long)chunkBytes);
            var data = new byte[want];
            var filled = 0;
            int n;
            while (filled < want && (n = stream.Read(data, filled, want - filled)) > 0)
                filled += n;
            if (filled != want)
                throw new IOException($"short read at chunk {index}: {filled} of {want}");
            return data;
        }

        // Whole-disk image (spec §1.2/§5): one ordinary manifest per
        // non-skipped partition (own generation, own ParentGeneration chain
        // via the existing Image path with an explicit per-partition parent)
        // plus one sealed layout sidecar in layouts/. Real-disk enumeration
        // (GPT+VSS+ESP) arrives via DiskImager.CapturePlan; without it there
        // is nothing to enumerate — fail loud, never an empty layout.
        public DiskLayout ImageDisk(int diskNumber, string name, DiskImageOptions opts, bool skipIfUnchanged = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(opts);
            var capture = DiskImager.CapturePlan?.Invoke(diskNumber, _log)
                ?? throw new InvalidOperationException($"disk {diskNumber}: no capture backend (elevated Windows GPT path not wired; Linux disk verbs exit 1)");
            DiskImager.RefuseIfLocked(diskNumber, capture.Partitions, _log);
            return ImageDisk(diskNumber, name, opts, capture, skipIfUnchanged);
        }

        internal DiskLayout ImageDisk(int diskNumber, string name, DiskImageOptions opts, DiskCapturePlan plan, bool skipIfUnchanged = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(plan);
            // §12.8 disk: skip only when EVERY partition plan is empty, else a
            // normal run (per-partition Image calls below run with skip OFF —
            // an empty single-partition plan must not abort the whole disk).
            if (skipIfUnchanged && !opts.ForceFull && DiskPlansAllEmpty(diskNumber, plan, opts, out var skipGen))
                SkipUnchanged(skipGen);
            var layout = new DiskLayout
            {
                SchemaVersion = 1,
                DiskNumber = diskNumber,
                DiskGuid = plan.DiskGuid,
                DiskSizeBytes = plan.DiskSizeBytes,
                SectorSizeBytes = plan.SectorSizeBytes,
                Style = "gpt",
            };
            var maxGen = 0;
            // §12.10 disk: one aggregated progress series across
            // partitions (no per-partition spam). Total = imagable
            // partition bytes; each partition reports local bytes shifted
            // into disk-cumulative, then snaps to its full length when
            // done (incremental partitions only re-read dirty bytes, so
            // without the snap the series could never reach 100%).
            var diskTotal = 0L;
            foreach (var part in plan.Partitions)
            {
                if (!part.Skipped && part.Source != null)
                    diskTotal += part.Source.Length;
            }
            ImageProgress? diskProgress = diskTotal > 0 ? new ImageProgress(_log, diskTotal) : null;
            var diskDone = 0L;
            try
            {
            foreach (var part in plan.Partitions)
            {
                var key = DiskImager.PartitionKey(diskNumber, part.PartitionNumber, part.UniqueGuid);
                var record = new PartitionRecord
                {
                    PartitionNumber = part.PartitionNumber,
                    Name = part.Name,
                    TypeGuid = part.TypeGuid,
                    UniqueGuid = part.UniqueGuid,
                    FirstLba = part.FirstLba,
                    LastLba = part.LastLba,
                    OffsetBytes = part.OffsetBytes,
                    LengthBytes = part.LengthBytes,
                    Filesystem = part.Filesystem,
                    Skipped = part.Skipped,
                    SkipReason = part.SkipReason,
                    SnapshotGeneration = 0,
                    PartitionKey = key,
                };
                layout.Partitions.Add(record);
                if (part.Skipped || part.Source == null)
                    continue;
                var parent = ResolveDiskParent(key, part.Filesystem, part.PartitionNumber, opts);
                var imageOpts = parent != null
                    ? new ImageOptions(ParentGeneration: parent.Generation, Incremental: true, ChangeSource: part.ChangeSource)
                    : new ImageOptions(ChangeSource: part.ChangeSource);
                var manifest = Image(part.Source, name, imageOpts,
                    progress: diskProgress != null ? new OffsetProgress(diskProgress, diskDone) : null);
                // Stamp partition identity post-hoc: the Image path is shared
                // with volume snapshots so its options carry no partition
                // fields. Same gen+name → same file, overwritten sealed.
                manifest.PartitionKey = key;
                manifest.DiskNumber = diskNumber;
                manifest.PartitionNumber = part.PartitionNumber;
                ManifestStore.Save(_manifestsDir, manifest, _key);
                record.SnapshotGeneration = manifest.Generation;
                if (manifest.Generation > maxGen)
                    maxGen = manifest.Generation;
                diskDone += part.Source.Length;
                diskProgress?.Report(diskDone);
            }
            }
            finally
            {
                // Mirror the volume path's `using`: per-partition sources
                // hold a shared VSS set + BitLocker scopes that only
                // complete/resume on release. Dispose-all (not
                // per-iteration) so a mid-disk failure still tears down
                // partitions never imaged; Dispose is idempotent.
                foreach (var part in plan.Partitions)
                    part.Source?.Dispose();
            }
            layout.Generation = maxGen;
            LayoutStore.Save(_layoutsDir, layout, _key);
            _log.Info($"imaged disk {diskNumber} '{name}': {layout.Partitions.Count} partitions, layout gen {maxGen}");
            return layout;
        }

        // Shared by the disk loop and the §12.8 all-empty pre-check: newest
        // manifest for this partition key (explicit --parent gen or latest),
        // ReFS forced back to full. Behavior-identical to the inline code it
        // replaces (same loads, same Warn line).
        private SnapshotManifest? ResolveDiskParent(string key, string filesystem, int partitionNumber, DiskImageOptions opts)
        {
            SnapshotManifest? parent = null;
            if (!opts.ForceFull)
            {
                if (opts.ParentLayoutGen > 0)
                {
                    // --parent: newest manifest for this key at/below the
                    // layout gen; a key the old layout never had images
                    // full for that partition.
                    foreach (var (gen, snap) in ManifestStore.List(_manifestsDir))
                    {
                        if (gen > opts.ParentLayoutGen)
                            continue;
                        var candidate = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), _key);
                        if (string.Equals(candidate.PartitionKey, key, StringComparison.Ordinal)
                            && (parent == null || candidate.Generation > parent.Generation))
                            parent = candidate;
                    }
                }
                else if (opts.Incremental)
                {
                    parent = FindPartitionParent(key);
                }
            }
            // Item 3: ReFS is incremental-ineligible (USN/MFT path is
            // NTFS-only) → refuse loud, image full for that partition.
            if (parent != null && FsTypePolicy.DecideFilesystem(filesystem) == CaptureDecision.FullOnlyLoud)
            {
                _log.Warn($"full image: partition {partitionNumber} filesystem '{filesystem}' is incremental-ineligible (ReFS), re-reading all chunks");
                parent = null;
            }
            return parent;
        }

        // §12.8 disk gate: true only when every imagable partition has a
        // parent AND a computable empty plan. Any parent-less partition, null
        // plan (full re-read), or dirty chunk forces a normal run. skipGen =
        // latest manifest gen for the skip line. No imagable partitions, or
        // an imageless store, never skips.
        private bool DiskPlansAllEmpty(int diskNumber, DiskCapturePlan plan, DiskImageOptions opts, out int skipGen)
        {
            skipGen = 0;
            var imagable = 0;
            var latest = ManifestStore.List(_manifestsDir);
            if (latest.Count == 0)
                return false;
            skipGen = latest[latest.Count - 1].Generation;
            var chunkBytes = _chunkMib * 1024 * 1024;
            foreach (var part in plan.Partitions)
            {
                if (part.Skipped || part.Source == null)
                    continue;
                imagable++;
                var key = DiskImager.PartitionKey(diskNumber, part.PartitionNumber, part.UniqueGuid);
                var parent = ResolveDiskParent(key, part.Filesystem, part.PartitionNumber, opts);
                if (parent == null)
                    return false;
                var partPlan = (part.ChangeSource ?? DefaultChangeSource()).Plan(parent, part.Source, chunkBytes, _log);
                if (partPlan == null || partPlan.DirtyChunks.Count > 0)
                    return false;
            }
            return imagable > 0;
        }
        // corrupt → throw like Image today. Resized partitions fall back to
        // full via the existing CheckParent length gate (no code here); ESP
        // (ChangeCursor null) falls back via a null Plan (no code here).
        // Newest manifest with equal PartitionKey. Loads are HMAC-checked,
        // corrupt → throw like Image today. Resized partitions fall back to
        // full via the existing CheckParent length gate (no code here); ESP
        // (ChangeCursor null) falls back via a null Plan (no code here).
        private SnapshotManifest? FindPartitionParent(string partitionKey)
        {
            SnapshotManifest? best = null;
            foreach (var (gen, snap) in ManifestStore.List(_manifestsDir))
            {
                var candidate = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), _key);
                if (string.Equals(candidate.PartitionKey, partitionKey, StringComparison.Ordinal)
                    && (best == null || candidate.Generation > best.Generation))
                    best = candidate;
            }
            return best;
        }

        // Landing-zone fast capture (item 16 DEFAULT ingest path): hash + land
        // raw chunk files with zero compression/dedup/index IO, seal the
        // manifest immediately. Snapshots export pre-optimize via landing
        // read-through; OptimizeLanding folds chunks into packs on idle.
        // Full-capture only (incremental+landing composes at the next rung).
        // Landing files are plaintext even for encrypted stores (same trust
        // boundary as the store dir); digests still bind the encryption key.
        // shortcut: no fsync on landing writes (speed over crash-proofing —
        // a torn chunk fails the digest check loud and re-images from source).
        // §12.8 skipIfUnchanged: digest plan up front (MFT read only, cheap);
        // empty → skip before bulk streaming. No plan (non-MFT sources) →
        // stream, then withhold when zero new chunks (index-hit every chunk).
        public SnapshotManifest ImageFast(IBlockSource source, string name, ImageOptions? opts = null, bool skipIfUnchanged = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(name);
            opts ??= new ImageOptions();
            if (opts.Incremental || opts.ParentGeneration > 0)
                throw new InvalidOperationException("ImageFast is full-capture only (incremental+landing is a later rung)");
            var generation = ManifestStore.NextGeneration(_manifestsDir);
            var existing = ManifestStore.List(_manifestsDir);
            var prevDigest = "";
            SnapshotManifest? skipParent = null;
            if (existing.Count > 0)
            {
                var (gen, snap) = existing[existing.Count - 1];
                var latest = ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{snap}.json"), _key);
                prevDigest = latest.Hmac;
                if (skipIfUnchanged && !opts.ForceFull)
                    skipParent = latest;
            }
            var chunkBytes = _chunkMib * 1024 * 1024;
            if (skipParent != null)
            {
                // §12.8 landing short-circuit: the MFT-digest plan costs an
                // MFT read only. Empty → skip before ANY bulk streaming —
                // without this the flag saves nothing on the default path.
                var skipPlan = (opts.ChangeSource ?? DefaultChangeSource()).Plan(skipParent, source, chunkBytes, _log);
                if (skipPlan != null && skipPlan.DirtyChunks.Count == 0)
                    SkipUnchanged(skipParent.Generation);
            }
            Directory.CreateDirectory(_landingDir);
            // §12.13 automatic resume: a matching fingerprint reuses
            // hash-verified landed chunks; a mismatch discards them with
            // an INF line naming the cause — never a silent mix.
            var resumedHexes = BeginLandingResume(CurrentLandingFingerprint(source, opts));
            var entries = new List<ManifestEntry>(GridCount(source.Length, _chunkMib));
            var excl = ResolveExclusions(source, opts);
            var exclBpc = excl?.BytesPerCluster ?? 0;
            var exclRuns = exclBpc > 0 ? excl!.ExcludedRuns() : null;
            var holes = 0;
            var landed = 0;
            var newChunks = 0;
            var packedThisImage = new HashSet<string>(StringComparer.Ordinal);
            var landedThisRun = new List<string>();
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes);
                var enumerable = holeMap != null
                    ? Chunker.ReadSparse(stream, chunkBytes, i => i < holeMap.Length && holeMap[i])
                    : Chunker.Read(stream, chunkBytes);
                // §12.10 denominator = volume used bytes: allocated for
                // sparse sources, plain length otherwise. Resumed chunks
                // still stream through here, so they advance the series.
                var planned = PlannedDataBytes(source.Length, chunkBytes, holeMap);
                var reporter = new ImageProgress(_log, planned);
                var done = 0L;
                foreach (var chunk in enumerable)
                {
                    if (chunk.IsHole)
                    {
                        var holeSize = Math.Min((long)chunkBytes, source.Length - chunk.Offset);
                        entries.Add(ManifestEntry.Hole(holeSize));
                        holes++;
                        continue;
                    }
                    if (exclRuns != null && exclRuns.Count > 0)
                        CapturePolicy.ZeroExcludedRanges(chunk.Data, chunk.Offset, exclRuns, exclBpc);
                    var digest = ChunkId.ComputeId(chunk.Data, _key);
                    var hex = ChunkId.ToHex(digest);
                    entries.Add(new ManifestEntry(hex, chunk.Data.Length));
                    done += chunk.Data.Length;
                    reporter.Report(done);
                    // §12.8 zero-new accounting: intra-image dupes and index
                    // hits are not new. (LandChunk's own skip-if-present is
                    // landing-state, not store-state — post-optimize reruns
                    // land fresh files for indexed bytes.)
                    if (!packedThisImage.Contains(hex) && !_index.TryGet(digest, out _))
                    {
                        packedThisImage.Add(hex);
                        newChunks++;
                    }
                    // Resumed chunks were hash-verified at Begin this run
                    // (crash wrote them, paranoia re-read them), so only
                    // new hexes reach the disk. LandChunk's own
                    // skip-if-present then covers intra-run duplicates only.
                    if (!resumedHexes.Contains(hex) && LandChunk(hex, chunk.Data))
                    {
                        landed++;
                        landedThisRun.Add(Path.Combine(_landingDir, hex + ".chunk"));
                    }
                }
                if (planned <= 0 || done < planned)
                    reporter.Report(planned);
            }
            if (skipParent != null && (newChunks == 0 || entries.SequenceEqual(skipParent.Chunks)))
            {
                // Withhold: no manifest, and the just-landed duplicates go
                // back. Manifest-compare covers pre-optimize reruns (landing
                // bytes aren't indexed until optimize folds them); every
                // distinct hex is then index-resident (a first-seen hex
                // always counts as new), so no manifest loses its landing
                // read-through.
                foreach (var path in landedThisRun)
                    File.Delete(path);
                SkipUnchanged(skipParent.Generation);
            }
            var (mftDigests, mftRecordBytes) = TryMftBaseline(source, chunkBytes, _log);
            var manifest = ManifestStore.Build(
                generation, name, source.Length, _chunkMib, _zstdLevel,
                ChunkId.KeyId(_key), prevDigest, entries,
                changeCursor: UsnChangeSource.TryCurrentCursor(source, _log),
                layoutFingerprint: CurrentFingerprint(source, opts),
                skippedExclusions: excl != null && excl.ExclusionNames.Count > 0 ? new List<string>(excl.ExclusionNames) : null,
                mftChunkDigests: mftDigests, mftRecordBytes: mftRecordBytes,
                bitLockerState: (source as IBitLockerVolume)?.BitLockerState,
                bitLockerKeyIds: (source as IBitLockerVolume)?.BitLockerKeyIds);
            ManifestStore.Save(_manifestsDir, manifest, _key);
            if (manifest.SkippedExclusions != null)
                _log.Warn($"excluded extents restored as zeros: {string.Join(", ", manifest.SkippedExclusions)}");
            _log.Info($"fast-imaged '{name}' gen {generation}: {entries.Count} chunks ({holes} holes), {landed} landed (optimize pending)");
            return manifest;
        }

        // Crash-safe loose write: skip-if-present (intra-landing dedup, no
        // index IO), else tmp + rename. Same bytes per digest, so a raced
        // overwrite is harmless.
        private bool LandChunk(string hex, byte[] data)
        {
            var path = Path.Combine(_landingDir, hex + ".chunk");
            if (File.Exists(path))
                return false;
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
            return true;
        }

        // §12.13 capture resume. The fingerprint binds the landed chunks
        // to the run that produced them: volume id, length, chunk grid,
        // exclusion set, plan digest. Content keys alone can't mix runs
        // (different bytes hash different), but without the fingerprint a
        // restart couldn't tell resume from stale — so stale discards
        // loud and resume logs its count. Recorded beside the chunks,
        // temp+rename like every other crash-safe write here.
        private const string LandingFingerprintName = "resume.json";

        private sealed class LandingFingerprint
        {
            public string? VolumeId { get; set; }
            public long Length { get; set; }
            public int ChunkMib { get; set; }
            public int Grid { get; set; }
            public List<string>? Exclusions { get; set; }
            public string? PlanDigest { get; set; }
        }

        private LandingFingerprint CurrentLandingFingerprint(IBlockSource source, ImageOptions opts)
        {
            var exclusions = new List<string>(CurrentExclusionNames(opts, source));
            exclusions.Sort(StringComparer.Ordinal);
            return new LandingFingerprint
            {
                VolumeId = CurrentFingerprint(source, opts),
                Length = source.Length,
                ChunkMib = _chunkMib,
                Grid = GridCount(source.Length, _chunkMib),
                Exclusions = exclusions,
                // ImageFast is full-capture only (it throws on
                // incremental), so there is no digest plan to bind —
                // the field reserves the incremental+landing rung.
                PlanDigest = "full",
            };
        }

        // Crash wrote it, paranoia reads it: a landed chunk is trusted
        // only when its bytes re-hash to the filename digest.
        private bool VerifyLandedChunk(string path, string hex)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            return string.Equals(ChunkId.ToHex(ChunkId.ComputeId(data, _key)), hex, StringComparison.Ordinal);
        }

        // First difference wins — the discard line names exactly one cause.
        private static string? LandingMismatchCause(LandingFingerprint previous, LandingFingerprint current)
        {
            if (!string.Equals(previous.VolumeId, current.VolumeId, StringComparison.Ordinal))
                return $"volume id changed ({previous.VolumeId ?? "(none)"} != {current.VolumeId ?? "(none)"})";
            if (previous.Length != current.Length)
                return $"volume length changed ({previous.Length} != {current.Length} bytes)";
            if (previous.ChunkMib != current.ChunkMib)
                return $"chunk size changed ({previous.ChunkMib} MiB != {current.ChunkMib} MiB)";
            if (previous.Grid != current.Grid)
                return $"chunk grid changed ({previous.Grid} != {current.Grid} chunks)";
            if (CapturePolicy.ExclusionSetChanged(previous.Exclusions, current.Exclusions))
                return $"exclusion set changed ({JoinNames(previous.Exclusions)} != {JoinNames(current.Exclusions)})";
            if (!string.Equals(previous.PlanDigest, current.PlanDigest, StringComparison.Ordinal))
                return $"plan changed ({previous.PlanDigest ?? "(none)"} != {current.PlanDigest ?? "(none)"})";
            return null;
        }

        private static string JoinNames(List<string>? names) =>
            names != null ? string.Join(",", names) : "";

        private void WriteLandingFingerprint(LandingFingerprint fingerprint)
        {
            var path = Path.Combine(_landingDir, LandingFingerprintName);
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(fingerprint));
            File.Move(tmp, path, true);
        }

        // Resume gate for ImageFast. Returns the verified already-landed
        // hexes (torn files are deleted; the loop re-lands them from
        // source). No fingerprint + empty landing = silent first run;
        // anything else stale discards + names its cause at INFO.
        private HashSet<string> BeginLandingResume(LandingFingerprint current)
        {
            var verified = new HashSet<string>(StringComparer.Ordinal);
            var fpPath = Path.Combine(_landingDir, LandingFingerprintName);
            LandingFingerprint? previous = null;
            var corrupt = false;
            if (File.Exists(fpPath))
            {
                try
                {
                    previous = JsonSerializer.Deserialize<LandingFingerprint>(File.ReadAllText(fpPath));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    corrupt = true;
                }
                if (!corrupt && previous == null)
                    corrupt = true;
            }
            var landed = new List<string>(Directory.EnumerateFiles(_landingDir, "*.chunk"));
            landed.Sort(StringComparer.Ordinal);
            if (previous == null && !corrupt && landed.Count == 0)
            {
                WriteLandingFingerprint(current);
                return verified;
            }
            string? cause = corrupt ? "unreadable landing fingerprint"
                : previous == null ? "no landing fingerprint (re-imaging landed chunks from scratch)"
                : LandingMismatchCause(previous, current);
            if (cause != null)
            {
                foreach (var path in landed)
                    File.Delete(path);
                _log.Info($"landing resume discarded ({cause}), starting fresh");
                WriteLandingFingerprint(current);
                return verified;
            }
            foreach (var path in landed)
            {
                var hex = Path.GetFileNameWithoutExtension(path);
                if (VerifyLandedChunk(path, hex))
                    verified.Add(hex);
                else
                    File.Delete(path);
            }
            _log.Info($"resuming: {verified.Count}/{current.Grid} chunks already landed");
            return verified;
        }

        // Idle optimizer: fold landing chunks into deduped packs. liveHex
        // (from GarbageCollector.LiveDigestHex) lets pruned/crashed-orphan
        // chunks drop without packing; null packs everything (the next GC
        // sweeps orphans via its mark pass).
        public (int Packed, int Deduped, int Dropped) OptimizeLanding(ISet<string>? liveHex = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Directory.Exists(_landingDir))
                return (0, 0, 0);
            // Crash orphans first: a torn LandChunk tmp never referenced anything.
            foreach (var tmp in Directory.EnumerateFiles(_landingDir, "*.tmp"))
                File.Delete(tmp);
            var paths = new List<string>(Directory.EnumerateFiles(_landingDir, "*.chunk"));
            paths.Sort(StringComparer.Ordinal);
            var packed = 0;
            var deduped = 0;
            var dropped = 0;
            var pending = new List<ChunkRow>();
            var pendingPaths = new List<string>();
            using (var pack = new PackWriter(_packsDir, encryptHook: _encryptHook))
            {
                var window = new EncodeWindow(pack, pending, _zstdLevel, pendingPaths);
                foreach (var path in paths)
                {
                    var hex = Path.GetFileNameWithoutExtension(path);
                    if (liveHex != null && !liveHex.Contains(hex))
                    {
                        File.Delete(path);
                        dropped++;
                        continue;
                    }
                    var data = File.ReadAllBytes(path);
                    var digest = ChunkId.ComputeId(data, _key);
                    if (!string.Equals(ChunkId.ToHex(digest), hex, StringComparison.Ordinal))
                        throw new InvalidDataException($"landing chunk {path} failed digest check (torn write? re-image the snapshot)");
                    if (_index.TryGet(digest, out _))
                    {
                        File.Delete(path);
                        deduped++;
                        continue;
                    }
                    window.Add(digest, data, path);
                    packed++;
                    if (pending.Count >= 500)
                    {
                        window.Drain();
                        FlushOptimizeBatch(pending, pendingPaths);
                    }
                }
                window.Drain();
                FlushOptimizeBatch(pending, pendingPaths);
            }
            // The fingerprint describes landed chunks; a fully-drained
            // landing has none, so drop it (preserves the empty-landing
            // invariant and avoids a stale resume claim on next capture).
            if (!Directory.EnumerateFiles(_landingDir, "*.chunk").Any())
            {
                var fingerprint = Path.Combine(_landingDir, LandingFingerprintName);
                if (File.Exists(fingerprint))
                    File.Delete(fingerprint);
            }
            _log.Info($"optimize: {packed} packed, {deduped} deduped, {dropped} dropped");
            return (packed, deduped, dropped);
        }

        private void FlushOptimizeBatch(List<ChunkRow> pending, List<string> paths)
        {
            if (pending.Count == 0) return;
            _index.UpsertBatch(pending);
            pending.Clear();
            // Delete only after the index points at the pack: a crash before
            // re-runs the fold idempotently (the index check dedups).
            foreach (var p in paths)
                File.Delete(p);
            paths.Clear();
        }

        public int PendingLandingCount()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Directory.Exists(_landingDir)) return 0;
            var n = 0;
            foreach (var _ in Directory.EnumerateFiles(_landingDir, "*.chunk"))
                n++;
            return n;
        }

        // Landing read-through: pre-optimize chunks live here as raw
        // plaintext (encryption applies at pack time). Null = not landed.
        private byte[]? TryReadLanding(string hex)
        {
            var path = Path.Combine(_landingDir, hex + ".chunk");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void Export(int generation, string outPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manifest = GetManifest(generation);
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            ExportToStream(generation, output);
            output.SetLength(manifest.VolumeLength);
            _log.Info($"exported snapshot {generation} ({manifest.Chunks.Count} chunks) to {outPath}");
        }

        // VHDX edge render: same decoded bytes as Export, written into a
        // dynamic VHDX Content stream. Zero chunks seek past (no allocation);
        // no SetLength — capacity was fixed at create, tail reads as zero.
        public void ExportVhdx(int generation, string outPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var manifest = GetManifest(generation);
            VhdxExporter.Export(outPath, manifest.VolumeLength, content => ExportToStream(generation, content));
            _log.Info($"exported snapshot {generation} ({manifest.Chunks.Count} chunks) to VHDX {outPath}");
        }

        // Manifest read for restore preflight/verify (HMAC-checked).
        public SnapshotManifest GetManifest(int generation) => LoadGeneration(generation);

        // Streamed export for restore: the same decoded bytes as Export,
        // chunk by chunk, with cumulative logical-byte progress (holes
        // included, so the final report equals VolumeLength). Holes and
        // zero chunks are SEEKED, never written: free space is don't-care
        // (the FS never reads it expecting values), and writing 17GB of
        // zeros would balloon thin targets (dynamic VHDX allocates on
        // zero writes) for zero fidelity gain. Verify compensates:
        // data ranges hash-fail loud, stale gap bytes warn-and-pass
        // (reused targets keep old free-space bytes — cosmetic,
        // boot-safe). Fresh targets read zeros and pass strictly.
        public void ExportToStream(int generation, Stream output, IProgress<long>? progress = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(output);
            WriteSnapshotToStream(LoadGeneration(generation), output, progress);
        }

        private void WriteSnapshotToStream(SnapshotManifest manifest, Stream output, IProgress<long>? progress = null)
        {
            var reported = 0L;
            foreach (var entry in manifest.Chunks)
            {
                if (entry.IsHole)
                {
                    output.Seek(entry.Size, SeekOrigin.Current);
                    reported += entry.Size; progress?.Report(reported); continue;
                }
                var digest = Convert.FromHexString(entry.DigestHex);
                byte[] plain;
                if (_index.TryGet(digest, out var row))
                {
                    var blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                    plain = Compression.Decode(blob.Payload, blob.Codec);
                }
                else if (TryReadLanding(entry.DigestHex) is byte[] landed)
                {
                    // Pre-optimize landing read-through (raw plaintext).
                    if (landed.Length != entry.Size)
                        throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {manifest.Generation} landing size mismatch ({landed.Length} != {entry.Size})");
                    plain = landed;
                }
                else
                {
                    throw new InvalidDataException($"chunk {entry.DigestHex} of snapshot {manifest.Generation} missing from index");
                }
                // Zero chunks decode (proving pack readability) but seek:
                // same don't-care rule as holes — verify hashes them as
                // data, so a dirty target fails strict here, not silent.
                if (IsAllZero(plain))
                {
                    output.Seek(plain.Length, SeekOrigin.Current);
                }
                else
                {
                    output.Write(plain, 0, plain.Length);
                }
                reported += plain.Length;
                progress?.Report(reported);
            }
        }

        // Full re-read: index → pack → decode → id compare. Never throws;
        // every failure path logs and returns false for CLI exit codes.
        // chain=true additionally walks ParentGeneration links to genesis
        // (HMAC loads + PrevDigest linkage; chunk bytes re-read for the
        // target alone — ancestors are provenance, not data).
        public bool Verify(int generation, bool chain = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var manifest = LoadGeneration(generation);
                var failed = false;
                var logged = 0;
                for (var i = 0; i < manifest.Chunks.Count; i++)
                {
                    var entry = manifest.Chunks[i];
                    if (entry.IsHole)
                    {
                        if (entry.DigestHex != "" || entry.Size <= 0 || entry.Size > manifest.ChunkMib * 1024 * 1024)
                        { failed = true; if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: corrupt hole entry"); }
                        continue;
                    }
                    var digest = Convert.FromHexString(entry.DigestHex);
                    if (!_index.TryGet(digest, out var row))
                    {
                        // Pre-optimize landing read-through: plaintext chunk,
                        // same key-bound digest check as packed data.
                        var landed = TryReadLanding(entry.DigestHex);
                        var ok = landed != null && landed.Length == entry.Size
                            && CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(landed, _key), digest);
                        if (ok)
                            continue;
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: missing from index");
                        continue;
                    }
                    byte[] plain;
                    try
                    {
                        var blob = PackReader.ReadBlob(Path.Combine(_packsDir, row.Pack), row.PackOffset, (int)row.StoredBytes);
                        plain = Compression.Decode(blob.Payload, blob.Codec);
                    }
                    // Broad catch: zstd throws plain System.Exception on corrupt
                    // frames, IO/pack faults throw their own — all mean failure.
                    catch (Exception ex)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: unreadable ({ex.Message})");
                        continue;
                    }
                    var verified = plain.Length == entry.Size
                        && CryptographicOperations.FixedTimeEquals(ChunkId.ComputeId(plain, _key), digest);
                    if (!verified)
                    {
                        failed = true;
                        if (logged++ < 5) _log.Error($"snapshot {generation} chunk {i}: content mismatch");
                    }
                }
                var total = 0L;
                foreach (var e in manifest.Chunks) total += e.Size;
                if (total != manifest.VolumeLength)
                {
                    failed = true;
                    _log.Error($"snapshot {generation} verify failed: size sum {total} != volume length {manifest.VolumeLength}");
                }
                if (chain && !VerifyChainLinks(generation))
                    failed = true;
                if (!failed)
                    _log.Info($"snapshot {generation} verified: {manifest.Chunks.Count} chunks OK");
                return !failed;
            }
            catch (Exception ex)
            {
                _log.Error($"snapshot {generation} verify failed", ex);
                return false;
            }
        }

        public List<(int Generation, string Name)> ListSnapshots()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ManifestStore.List(_manifestsDir);
        }

        // Chain provenance: walk ParentGeneration to genesis, HMAC-loading
        // each link and checking PrevDigest linkage. A gap fails loud
        // (provenance is unverifiable); the target's standalone result is
        // unaffected — it was computed from the target manifest alone.
        private bool VerifyChainLinks(int generation)
        {
            var known = new HashSet<int>();
            foreach (var (gen, _) in ManifestStore.List(_manifestsDir))
                known.Add(gen);
            var current = LoadGeneration(generation);
            while (current.ParentGeneration > 0)
            {
                var parentGen = current.ParentGeneration;
                if (!known.Contains(parentGen))
                {
                    _log.Error($"snapshot {generation} chain: provenance gap (parent gen {parentGen} of gen {current.Generation} missing; standalone verify of the target still passes)");
                    return false;
                }
                SnapshotManifest parent;
                try
                {
                    parent = LoadGeneration(parentGen);
                }
                catch (InvalidDataException ex)
                {
                    _log.Error($"snapshot {generation} chain: gen {parentGen} unreadable ({ex.Message})");
                    return false;
                }
                // PrevDigest seals ADJACENT generations (each manifest carries the
                // previous HMAC); an explicit older --parent skips gens, so
                // linkage is enforced only across adjacent links. Every
                // visited manifest is still HMAC-authenticated by the load.
                if (current.Generation == parentGen + 1 && current.PrevDigest != parent.Hmac)
                {
                    _log.Error($"snapshot {generation} chain: gen {current.Generation} PrevDigest does not match parent gen {parentGen} HMAC");
                    return false;
                }
                current = parent;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _index.Dispose();
        }

        private SnapshotManifest LoadGeneration(int generation)
        {
            foreach (var (gen, name) in ManifestStore.List(_manifestsDir))
            {
                if (gen == generation)
                    return ManifestStore.Load(Path.Combine(_manifestsDir, $"{gen:D6}-{name}.json"), _key);
            }
            throw new InvalidDataException($"snapshot generation {generation} not found in {_manifestsDir}");
        }

        private static bool IsAllZero(byte[] data)
        {
            foreach (var b in data)
            {
                if (b != 0) return false;
            }
            return true;
        }
    }
}
