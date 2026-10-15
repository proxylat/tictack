using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    public sealed record RestoreOptions(
        int Generation,
        string Target,
        string? StorePath = null,
        string? IndexPath = null,
        string? ConfirmToken = null,
        bool AllowBootDisk = false,
        bool SkipVerify = false,
        bool DryRun = false);

    public sealed record DiskRestoreOptions(
        int LayoutGen,
        string Target,
        string? StorePath = null,
        string? IndexPath = null,
        string? ConfirmToken = null,
        bool AllowBootDisk = false,
        bool SkipVerify = false,
        bool DryRun = false,
        // Item 5: orchestrated layout-recreate + bootloader reinstall (opt-in;
        // default false keeps the reviewed-manual printout path byte-identical).
        bool RepairBoot = false,
        IBootRepairRunner? BootRunner = null);

    // PE restore orchestration: preflight -> safety gate -> stream -> full
    // verify. Returns 0/1 matching the Verify exit posture. Never mints
    // keys: the id.key check runs before the injected Store is touched, and
    // the CLI checks again before constructing one (no Store ctor change).
    public sealed class RestoreOrchestrator
    {
        private readonly Store _store;
        private readonly ILogger _log;
        private readonly Func<string, IBlockTarget> _targets;

        public RestoreOrchestrator(Store store, ILogger log, Func<string, IBlockTarget> targetFactory)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(log);
            ArgumentNullException.ThrowIfNull(targetFactory);
            _store = store;
            _log = log;
            _targets = targetFactory;
        }

        public static bool HasIdentityKey(string storeRoot) =>
            !string.IsNullOrEmpty(storeRoot) && File.Exists(Path.Combine(storeRoot, "id.key"));

        // Default factory: disk number | \\.\PhysicalDriveN | file path.
        public static IBlockTarget OpenTarget(string spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            if (int.TryParse(spec, out var number))
                return new RawDiskTarget($@"\\.\PhysicalDrive{number}");
            if (spec.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                return new RawDiskTarget(spec);
            return new FileBlockTarget(spec);
        }

        public int Restore(RestoreOptions opts, IProgress<long>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(opts);
            // 1. No-key-mint pre-check, before the store is even touched.
            if (!string.IsNullOrEmpty(opts.StorePath) && !HasIdentityKey(opts.StorePath))
            {
                _log.Error($"refusing restore: no id.key in {opts.StorePath} (restore never creates one — check --store)");
                return 1;
            }
            SnapshotManifest manifest;
            try
            {
                manifest = _store.GetManifest(opts.Generation);
            }
            catch (Exception ex)
            {
                _log.Error($"cannot load snapshot {opts.Generation}", ex);
                return 1;
            }
            using var target = _targets(opts.Target);
            // 2. Preflight before any write: capacity, then locked BitLocker.
            if (target.Capacity < manifest.VolumeLength)
            {
                _log.Error($"refusing restore: target {target.DisplayName} holds {target.Capacity} bytes, snapshot {manifest.Generation} needs {manifest.VolumeLength} (no bytes written)");
                return 1;
            }
            if (RefuseLockedBitLocker(target))
                return 1;
            // 3. Safety gate: the token is REQUIRED always (even --dry-run);
            // --allow-boot-disk still needs it.
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            var supplied = opts.ConfirmToken;
            if (string.IsNullOrEmpty(supplied) && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Write($"Type '{expected}' to confirm restore to {target.DisplayName}: ");
                supplied = Console.ReadLine()?.Trim();
            }
            _log.Info($"restore confirm token: '{supplied}'");
            if (!BootDiskGuard.Confirm(supplied, expected))
            {
                _log.Error($"refusing restore: --confirm-type must equal '{expected}'");
                return 1;
            }
            if (!opts.AllowBootDisk && BootDiskGuard.IsBootDisk(target, out var boot))
            {
                _log.Error($"refusing restore: {target.DisplayName} is the boot disk ({boot}) — re-run with --allow-boot-disk and --confirm-type '{expected}'");
                return 1;
            }
            _log.Info($"restore snapshot {manifest.Generation} ({manifest.Chunks.Count} chunks, {manifest.VolumeLength} bytes) -> {target.DisplayName} [{expected}]");
            if (opts.DryRun)
            {
                _log.Info("dry run: plan only, no bytes written");
                return 0;
            }
            // 4. Stream chunk-by-chunk; the log gets % + MB/s at 2 Hz while
            // every cumulative report is forwarded to progress untouched.
            // A mid-stream failure (bad pack, dead device) returns 1 here —
            // it never reaches verify, so the message says so.
            var sw = Stopwatch.StartNew();
            try
            {
                using (var stream = target.OpenWrite())
                    _store.ExportToStream(opts.Generation, stream, new ThrottledProgress(_log, manifest.VolumeLength, sw, progress), fillGaps: true);
                if (target is FileBlockTarget file)
                    FixFileLength(file.Path, manifest.VolumeLength);
            }
            catch (Exception ex)
            {
                _log.Error("restore streaming failed before any offsets could verify", ex);
                return 1;
            }
            // 5. Verify-after-write: FULL re-read, per manifest entry in
            // order — target bytes hashed with ChunkId vs the digest
            // (holes: expect zeros). A corrupt pack decodes to wrong bytes
            // that stream fine; this is where that surfaces.
            if (opts.SkipVerify)
            {
                _log.Warn("skipping post-restore verify (--skip-verify)");
            }
            else
            {
                List<long> bad;
                try
                {
                    bad = VerifyTarget(target, manifest, opts.StorePath);
                }
                catch (Exception ex)
                {
                    _log.Error("restore verify failed to re-read the target", ex);
                    return 1;
                }
                if (bad.Count > 0)
                {
                    for (var i = 0; i < Math.Min(5, bad.Count); i++)
                        _log.Error($"restore verify mismatch at offset {bad[i]}");
                    _log.Error($"restore verify failed: {bad.Count} corrupt ranges (re-run restore; do not boot this target)");
                    return 1;
                }
            }
            sw.Stop();
            _log.Info($"restored snapshot {manifest.Generation}: {manifest.VolumeLength} bytes written and verified in {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        // Whole-disk restore (disk-spec §6): the existing 5 phases per
        // extent at its recorded offset. Skipped (MSR) extents are never
        // written and never verified (their contents are undefined by
        // design — writing zeros would fake a fidelity capture never had).
        // v1 does NOT partition: the target table must already exist (see
        // DiskpartScript for the reviewed manual prep); boot repair stays a
        // printed bcdboot line, no FAT32 parsing.
        public int RestoreDisk(DiskRestoreOptions opts, IProgress<long>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(opts);
            // 1. No-key-mint pre-check, before the store is even touched.
            if (!string.IsNullOrEmpty(opts.StorePath) && !HasIdentityKey(opts.StorePath))
            {
                _log.Error($"refusing disk restore: no id.key in {opts.StorePath} (restore never creates one — check --store)");
                return 1;
            }
            if (string.IsNullOrEmpty(opts.StorePath))
            {
                _log.Error("refusing disk restore: no store path (check --store)");
                return 1;
            }
            DiskLayout layout;
            try
            {
                layout = LoadLayout(opts.StorePath, opts.LayoutGen);
            }
            catch (Exception ex)
            {
                _log.Error($"cannot load disk layout {opts.LayoutGen}", ex);
                return 1;
            }
            var extents = new List<PartitionRecord>();
            foreach (var p in layout.Partitions)
            {
                if (!p.Skipped)
                    extents.Add(p);
            }
            using var target = _targets(opts.Target);
            // 2. Preflight before any write: max extent end, then locked BitLocker.
            var need = 0L;
            foreach (var p in extents)
                need = Math.Max(need, p.OffsetBytes + p.LengthBytes);
            if (target.Capacity < need)
            {
                _log.Error($"refusing disk restore: target {target.DisplayName} holds {target.Capacity} bytes, layout {opts.LayoutGen} needs {need} (no bytes written)");
                return 1;
            }
            if (RefuseLockedBitLocker(target))
                return 1;
            // 3. Safety gate: the token is REQUIRED always (even --dry-run).
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            var supplied = opts.ConfirmToken;
            if (string.IsNullOrEmpty(supplied) && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Write($"Type '{expected}' to confirm disk restore to {target.DisplayName}: ");
                supplied = Console.ReadLine()?.Trim();
            }
            _log.Info($"disk restore confirm token: '{supplied}'");
            if (!BootDiskGuard.Confirm(supplied, expected))
            {
                _log.Error($"refusing disk restore: --confirm-type must equal '{expected}'");
                return 1;
            }
            if (!opts.AllowBootDisk && BootDiskGuard.IsBootDisk(target, out var boot))
            {
                _log.Error($"refusing disk restore: {target.DisplayName} is the boot disk ({boot}) — re-run with --allow-boot-disk and --confirm-type '{expected}'");
                return 1;
            }
            _log.Info($"restore disk layout {layout.Generation} ({extents.Count} partitions, {layout.DiskSizeBytes} bytes) -> {target.DisplayName} [{expected}]");
            if (opts.DryRun)
            {
                var script = DiskpartScript.Generate(layout);
                _log.Info("dry run: plan only, no bytes written");
                _log.Info(script.TrimEnd());
                if (opts.RepairBoot)
                    LogRepairPlan(layout);
                else
                    PrintBootRepair(layout);
                File.WriteAllText(DiskpartPath(target), script);
                return 0;
            }
            // 4. RepairBoot ordering: the partition table MUST be recreated
            // BEFORE bytes stream (diskpart clean wipes the old table; the
            // no-format script rewrites entries only, never payload ranges).
            IBootRepairRunner? repairRunner = null;
            if (opts.RepairBoot)
            {
                repairRunner = opts.BootRunner ?? new WindowsBootRepairRunner(_log);
                var applyRc = BootRepairOrchestrator.ApplyPhase(layout, repairRunner, _log);
                if (applyRc != 0) return applyRc;
            }
            // 5. Grow file targets once (never truncate: gaps keep prefill);
            // raw targets already passed the fit preflight.
            if (target is FileBlockTarget grown)
                GrowFile(grown.Path, layout.DiskSizeBytes);
            // 6. Stream partition-by-partition in on-disk order through
            // extent windows, one cumulative ThrottledProgress over the disk
            // total (sync forwarding keeps progress monotonic).
            var sw = Stopwatch.StartNew();
            var throttle = new ThrottledProgress(_log, layout.DiskSizeBytes, sw, progress);
            var done = 0L;
            var manifests = new List<(PartitionRecord Part, SnapshotManifest Manifest)>();
            try
            {
                foreach (var part in extents)
                {
                    SnapshotManifest manifest;
                    try
                    {
                        manifest = _store.GetManifest(part.SnapshotGeneration);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"disk restore failed: layout {opts.LayoutGen} partition {part.PartitionNumber} needs snapshot {part.SnapshotGeneration}", ex);
                        return 1;
                    }
                    using var window = new OffsetBlockTarget(target, part.OffsetBytes);
                    using (var stream = window.OpenWrite())
                        _store.ExportToStream(part.SnapshotGeneration, stream, new OffsetForwarder(throttle, done), fillGaps: true);
                    manifests.Add((part, manifest));
                    done += manifest.VolumeLength;
                }
            }
            catch (Exception ex)
            {
                _log.Error("disk restore streaming failed before any offsets could verify", ex);
                return 1;
            }
            // 7. Post-restore extent-vs-layout compare, then FULL re-read
            // verify per extent unless --skip-verify.
            foreach (var (part, manifest) in manifests)
            {
                if (manifest.VolumeLength != part.LengthBytes)
                    _log.Warn($"partition {part.PartitionNumber}: layout length {part.LengthBytes} != snapshot {manifest.Generation} length {manifest.VolumeLength}");
                else
                    _log.Info($"partition {part.PartitionNumber}: {part.LengthBytes} bytes at offset {part.OffsetBytes} <- snapshot {manifest.Generation}");
            }
            if (opts.SkipVerify)
            {
                _log.Warn("skipping post-restore verify (--skip-verify)");
            }
            else
            {
                var bad = new List<long>();
                try
                {
                    foreach (var (part, manifest) in manifests)
                    {
                        using var window = new OffsetBlockTarget(target, part.OffsetBytes);
                        bad.AddRange(VerifyTarget(window, manifest, opts.StorePath));
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("disk restore verify failed to re-read the target", ex);
                    return 1;
                }
                if (bad.Count > 0)
                {
                    for (var i = 0; i < Math.Min(5, bad.Count); i++)
                        _log.Error($"disk restore verify mismatch at offset {bad[i]}");
                    _log.Error($"disk restore verify failed: {bad.Count} corrupt ranges (re-run restore; do not boot this target)");
                    return 1;
                }
            }
            sw.Stop();
            _log.Info($"restored disk layout {layout.Generation}: {extents.Count} partitions written and verified in {sw.Elapsed.TotalSeconds:F1}s");
            // 8. Bootloader only after verify passes (never boot a target
            // whose bytes didn't verify). The null runner means RepairBoot
            // was false — ApplyPhase above always sets it otherwise.
            if (opts.RepairBoot)
                return BootRepairOrchestrator.BootloaderPhase(layout, repairRunner!, _log);
            PrintBootRepair(layout);
            return 0;
        }

        // Item 5 dry-run: checks run for real (a layout that would not repair
        // fails the plan), the script apply stays unwritten.
        private void LogRepairPlan(DiskLayout layout)
        {
            var blocked = BootRepairOrchestrator.CheckLayout(layout);
            if (blocked != null)
            {
                _log.Error("dry run: " + blocked);
                return;
            }
            _log.Info(BootRepairOrchestrator.Plan(layout).TrimEnd());
            _log.Info("dry run: boot repair planned, nothing applied");
        }

        // Layout lookup by generation: the file name carries the source disk
        // number ({gen:D6}-disk{N}.json) which the restore target may not
        // share, so match on the generation prefix and require exactly one.
        private static DiskLayout LoadLayout(string storeRoot, int layoutGen)
        {
            var layoutsDir = Path.Combine(storeRoot, "layouts");
            var matches = Directory.Exists(layoutsDir)
                ? Directory.GetFiles(layoutsDir, $"{layoutGen:D6}-disk*.json")
                : Array.Empty<string>();
            if (matches.Length == 0)
                throw new InvalidDataException($"disk layout {layoutGen} not found in {layoutsDir}");
            if (matches.Length > 1)
                throw new InvalidDataException($"disk layout {layoutGen} is ambiguous in {layoutsDir} ({matches.Length} matches)");
            return LayoutStore.Load(matches[0], File.ReadAllBytes(Path.Combine(storeRoot, "id.key")));
        }

        // Grow-only (FixFileLength reuse for the extend; never truncate —
        // gaps outside extents keep whatever the target already held).
        private static void GrowFile(string path, long diskSize)
        {
            if (!File.Exists(path))
            {
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                created.SetLength(diskSize);
                return;
            }
            if (new FileInfo(path).Length < diskSize)
                FixFileLength(path, diskSize); // extend zero-fills
        }

        // Boot repair stays a printed line (disk-spec §6): one bcdboot per
        // NTFS partition with its layout-derived size, letters fixed by hand.
        private void PrintBootRepair(DiskLayout layout)
        {
            foreach (var p in layout.Partitions)
            {
                if (p.Skipped || !string.Equals(p.Filesystem, "ntfs", StringComparison.OrdinalIgnoreCase))
                    continue;
                _log.Info($"boot repair for partition {p.PartitionNumber} ({p.LengthBytes} bytes): bcdboot W:\\Windows /s S: /f UEFI (fix drive letters first)");
            }
        }

        // <target>.diskpart.txt: beside file targets; raw device names are
        // not valid file names, so those land sanitized in the cwd.
        private static string DiskpartPath(IBlockTarget target)
        {
            if (target is FileBlockTarget file)
                return file.Path + ".diskpart.txt";
            var chars = (target.DisplayName + ".diskpart.txt").ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        // Best-effort locked-BitLocker refuse (Windows file targets only):
        // a locked volume fails writes anyway — refuse with a clear line
        // instead. Absent manage-bde (or any error) means proceed.
        private bool RefuseLockedBitLocker(IBlockTarget target)
        {
            if (!OperatingSystem.IsWindows() || target is not FileBlockTarget file)
                return false;
            try
            {
                var root = Path.GetPathRoot(System.IO.Path.GetFullPath(file.Path));
                if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsAsciiLetter(root[0]))
                    return false;
                var r = ProcessRunner.RunAsync($"manage-bde -status {root}", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    return false;
                if (r.Stdout.Contains("Locked", StringComparison.OrdinalIgnoreCase)
                    && !r.Stdout.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Error($"refusing restore: volume {root} is BitLocker-locked (unlock with manage-bde -unlock {root} first)");
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void FixFileLength(string path, long volumeLength)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (fs.Length != volumeLength)
                fs.SetLength(volumeLength); // extend zero-fills; truncate drops pre-existing tail
        }

        // Verify-after-write: FULL re-read, per manifest entry in order —
        // target bytes hashed with ChunkId vs the manifest digest (holes:
        // expect zeros). The store id.key supplies identity; the injected
        // Store is not involved, so this works for any target.
        private static List<long> VerifyTarget(IBlockTarget target, SnapshotManifest manifest, string? storeRoot)
        {
            if (string.IsNullOrEmpty(storeRoot) || !File.Exists(Path.Combine(storeRoot, "id.key")))
                throw new InvalidDataException("post-restore verify needs the store id.key");
            var idKey = File.ReadAllBytes(Path.Combine(storeRoot, "id.key"));
            var bad = new List<long>();
            // Per-extent offset: positions stay extent-relative, the window
            // base is added at seek/report time (the offset-0 path is the
            // BaseOffset==0 case — single-gen restores pass plain targets
            // and behave byte-identically to before).
            var baseOffset = target is IPartitionTarget window ? window.BaseOffset : 0L;
            using var read = OpenTargetRead(target);
            var buf = new byte[4 * 1024 * 1024];
            var position = 0L;
            foreach (var entry in manifest.Chunks)
            {
                if (entry.IsHole)
                {
                    if (!ReadZeros(read, position, entry.Size, buf, baseOffset))
                        bad.Add(baseOffset + position);
                }
                else if (!VerifyRange(read, position, entry, idKey, buf, baseOffset))
                {
                    bad.Add(baseOffset + position);
                }
                position += entry.Size;
            }
            return bad;
        }

        private static bool VerifyRange(Stream read, long position, ManifestEntry entry, byte[] idKey, byte[] buf, long baseOffset = 0)
        {
            var digest = Convert.FromHexString(entry.DigestHex);
            return CryptographicOperations.FixedTimeEquals(HashRange(read, position, entry.Size, idKey, buf, baseOffset), digest);
        }

        // ChunkId.ComputeId incrementally (O(1) memory): SHA-256 over
        // plaintext, then idKey — the same append order.
        // position is extent-relative; baseOffset shifts the seek so extent
        // windows re-read their own bytes (0 for single-gen restores).
        private static byte[] HashRange(Stream read, long position, long size, byte[] idKey, byte[] buf, long baseOffset = 0)
        {
            read.Seek(baseOffset + position, SeekOrigin.Begin);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var left = size;
            while (left > 0)
            {
                var n = read.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0)
                    throw new IOException($"short target read at offset {baseOffset + position + (size - left)}: target smaller than the snapshot");
                h.AppendData(buf, 0, n);
                left -= n;
            }
            h.AppendData(idKey);
            return h.GetHashAndReset();
        }

        private static bool ReadZeros(Stream read, long position, long size, byte[] buf, long baseOffset = 0)
        {
            read.Seek(baseOffset + position, SeekOrigin.Begin);
            var left = size;
            while (left > 0)
            {
                var n = read.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0)
                    break; // missing tail reads as zero
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != 0)
                        return false;
                }
                left -= n;
            }
            return true;
        }

        private static Stream OpenTargetRead(IBlockTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target is OffsetBlockTarget window)
                return OpenTargetRead(window.Inner); // offsets apply at seek time, not here
            if (target is FileBlockTarget file)
                return new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (target is RawDiskTarget raw)
                return raw.OpenRead();
            throw new InvalidOperationException($"cannot re-read target {target.DisplayName} for verify (unknown target kind)");
        }

        // Synchronous fan-out: forward every cumulative report, log % + MB/s
        // at most every 500 ms (plus the final report). Sync delivery keeps
        // progress monotonic — Progress<T> would hop threads and reorder.
        private sealed class OffsetForwarder : IProgress<long>
        {
            private readonly IProgress<long> _inner;
            private readonly long _base;

            public OffsetForwarder(IProgress<long> inner, long completedBefore)
            {
                _inner = inner;
                _base = completedBefore;
            }

            // Per-extent cumulative (from zero) shifted into disk-cumulative.
            public void Report(long bytes) => _inner.Report(_base + bytes);
        }

        private sealed class ThrottledProgress : IProgress<long>
        {
            private readonly ILogger _log;
            private readonly long _total;
            private readonly Stopwatch _sw;
            private readonly IProgress<long>? _fwd;
            private long _lastMs;

            public ThrottledProgress(ILogger log, long total, Stopwatch sw, IProgress<long>? fwd)
            {
                _log = log;
                _total = total;
                _sw = sw;
                _fwd = fwd;
                _lastMs = -1000;
            }

            public void Report(long bytes)
            {
                _fwd?.Report(bytes);
                var ms = _sw.ElapsedMilliseconds;
                if (ms - _lastMs < 500 && bytes < _total)
                    return;
                _lastMs = ms;
                var pct = _total <= 0 ? 100 : bytes * 100 / _total;
                var mbps = _sw.Elapsed.TotalSeconds > 0 ? bytes / _sw.Elapsed.TotalSeconds / (1024 * 1024) : 0;
                _log.Info($"restore {pct}% ({mbps:F1} MB/s)");
            }
        }
    }
}
