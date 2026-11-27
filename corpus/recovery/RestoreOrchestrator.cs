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
        IBootRepairRunner? BootRunner = null,
        // Subset restore: partition numbers to create+restore (null/empty =
        // all). Skipped-structural partitions (MSR) auto-include in the
        // layout recreate — they carry no bytes. Lets a 176GB disk's OS
        // partitions restore to a 70GB target without touching data.
        IReadOnlyList<int>? Partitions = null);

    // PE restore orchestration: preflight -> safety gate -> stream -> full
    // verify. Returns 0/1 matching the Verify exit posture. Never mints
    // keys: the id.key check runs before the injected Store is touched, and
    // the CLI checks again before constructing one (no Store ctor change).
    public sealed class RestoreOrchestrator
    {
        // Restic-style console progress wiring (set from cfg at construction
        // sites). Null = classic 2 Hz % lines everywhere.
        internal LiveProgressOptions? ProgressOpts { get; set; }

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
            if (int.TryParse(spec, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return new RawDiskTarget($@"\\.\PhysicalDrive{number}");
            if (spec.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                return new RawDiskTarget(spec);
            return new FileBlockTarget(spec);
        }

        // Disk number behind a target spec (same grammar as OpenTarget):
        // "2" or "\\.\PhysicalDrive2" -> 2, anything else (file) -> null.
        // Repair needs the number for `select disk`; file targets refuse.
        internal static int? TryParseTargetDisk(string spec)
        {
            if (string.IsNullOrEmpty(spec))
                return null;
            if (int.TryParse(spec, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0)
                return number;
            const string marker = @"\\.\PhysicalDrive";
            if (spec.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(spec.Substring(marker.Length), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var drive) && drive >= 0)
                return drive;
            return null;
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
            if (!ConfirmAndGateTarget(target, opts.ConfirmToken, opts.AllowBootDisk, "restore"))
                return 1;
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            _log.Info($"restore snapshot {manifest.Generation} ({manifest.Chunks.Count} chunks, {manifest.VolumeLength} bytes) -> {target.DisplayName} [{expected}]");
            if (opts.DryRun)
            {
                _log.Info("dry run: plan only, no bytes written");
                return 0;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using (var stream = target.OpenWrite())
                    _store.ExportToStream(opts.Generation, stream, LiveProgress.Create(_log, ProgressOpts, "restore", manifest.VolumeLength, null, progress) ?? new ThrottledProgress(_log, manifest.VolumeLength, sw, progress));
                if (target is FileBlockTarget file)
                    FixFileLength(file.Path, manifest.VolumeLength);
            }
            catch (Exception ex)
            {
                _log.Error("restore streaming failed before any offsets could verify", ex);
                return 1;
            }
            if (!VerifyRestoredVolume(target, manifest, opts))
                return 1;
            sw.Stop();
            _log.Info($"restored snapshot {manifest.Generation}: {manifest.VolumeLength} bytes written and verified in {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        private bool ConfirmAndGateTarget(IBlockTarget target, string? confirmToken, bool allowBootDisk, string label)
        {
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            var supplied = confirmToken;
            if (string.IsNullOrEmpty(supplied) && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.Write($"Type '{expected}' to confirm {label} to {target.DisplayName}: ");
                supplied = Console.ReadLine()?.Trim();
            }
            _log.Info($"{label} confirm token: '{supplied}'");
            if (!BootDiskGuard.Confirm(supplied, expected))
            {
                _log.Error($"refusing {label}: --confirm-type must equal '{expected}'");
                return false;
            }
            if (!allowBootDisk && BootDiskGuard.IsBootDisk(target, out var boot))
            {
                _log.Error($"refusing {label}: {target.DisplayName} is the boot disk ({boot}) — re-run with --allow-boot-disk and --confirm-type '{expected}'");
                return false;
            }
            return true;
        }

        private bool VerifyRestoredVolume(IBlockTarget target, SnapshotManifest manifest, RestoreOptions opts)
        {
            if (opts.SkipVerify)
            {
                _log.Warn("skipping post-restore verify (--skip-verify)");
                return true;
            }
            List<long> dataBad;
            List<long> gapBad;
            try
            {
                (dataBad, gapBad) = VerifyTarget(target, manifest, opts.StorePath);
            }
            catch (Exception ex)
            {
                _log.Error("restore verify failed to re-read the target", ex);
                return false;
            }
            if (dataBad.Count > 0)
            {
                for (var i = 0; i < Math.Min(5, dataBad.Count); i++)
                    _log.Error($"restore verify mismatch at offset {dataBad[i]}");
                _log.Error($"restore verify failed: {dataBad.Count} corrupt ranges (re-run restore; do not boot this target)");
                return false;
            }
            if (gapBad.Count > 0)
                _log.Warn($"restore verify: {gapBad.Count} free-space range(s) hold stale bytes (reused target — cosmetic, boot-safe; first at offset {gapBad[0]})");
            return true;
        }

        // Whole-disk restore (disk-spec §6): phased restore per extent at
        // its recorded offset. Skipped (MSR) extents are never
        // written and never verified (their contents are undefined by
        // design — writing zeros would fake a fidelity capture never had).
        // --repair-boot recreates the table orchestrated (ApplyPhase) and
        // reinstalls the bootloader post-verify (BootloaderPhase); without
        // it the target table must already exist and boot repair stays a
        // printed bcdboot line.
        // RepairBoot MUST recreate the partition table before streaming (diskpart clean wipes
        // the old table); volume locks hold across stream+verify and release before bcdboot.
        public int RestoreDisk(DiskRestoreOptions opts, IProgress<long>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(opts);
            if (!TryLoadDiskRestoreLayout(opts, out var targetDisk, out var layout))
                return 1;
            var repairDisk = targetDisk ?? -1;
            var work = FilterLayoutPartitions(layout!, opts);
            if (work == null)
                return 1;
            var extents = CollectStreamableExtents(work);
            using var target = _targets(opts.Target);
            if (!PreflightDiskTarget(target, extents, opts))
                return 1;
            var expected = BootDiskGuard.TargetToken(target.DisplayName, target.Capacity);
            _log.Info($"restore disk layout {work.Generation} ({extents.Count} partitions, {work.DiskSizeBytes} bytes) -> {target.DisplayName} [{expected}]");
            if (opts.DryRun)
                return ExecuteDiskDryRun(work, target, targetDisk, opts.RepairBoot);

            IBootRepairRunner? repairRunner = null;
            if (opts.RepairBoot)
            {
                repairRunner = opts.BootRunner ?? new WindowsBootRepairRunner(_log);
                var applyRc = BootRepairOrchestrator.ApplyPhase(work, repairRunner, _log, repairDisk);
                if (applyRc != 0) return applyRc;
            }
            using var locks = VolumeLockSet.Acquire(targetDisk, _log);
            if (target is FileBlockTarget grown)
                GrowFile(grown.Path, work.DiskSizeBytes);
            var sw = Stopwatch.StartNew();
            var manifests = StreamDiskExtents(target, extents, opts, sw, progress);
            if (manifests == null || !VerifyDiskExtents(target, manifests, opts))
                return 1;
            sw.Stop();
            _log.Info($"restored disk layout {work.Generation}: {extents.Count} partitions written and verified in {sw.Elapsed.TotalSeconds:F1}s");
            locks.Dispose();
            return opts.RepairBoot
                ? BootRepairOrchestrator.BootloaderPhase(work, repairRunner!, _log, repairDisk)
                : PrintAndReturnZero(work);
        }

        private bool TryLoadDiskRestoreLayout(DiskRestoreOptions opts, out int? targetDisk, out DiskLayout? layout)
        {
            targetDisk = null;
            layout = null;
            if (!string.IsNullOrEmpty(opts.StorePath) && !HasIdentityKey(opts.StorePath))
            {
                _log.Error($"refusing disk restore: no id.key in {opts.StorePath} (restore never creates one — check --store)");
                return false;
            }
            if (string.IsNullOrEmpty(opts.StorePath))
            {
                _log.Error("refusing disk restore: no store path (check --store)");
                return false;
            }
            targetDisk = TryParseTargetDisk(opts.Target);
            if (opts.RepairBoot && targetDisk == null)
            {
                _log.Error($"refusing disk restore: --repair-boot needs a disk target (got file '{opts.Target}' — files have no partition table to recreate)");
                return false;
            }
            try { layout = LoadLayout(opts.StorePath, opts.LayoutGen); return true; }
            catch (Exception ex) { _log.Error($"cannot load disk layout {opts.LayoutGen}", ex); return false; }
        }

        private int PrintAndReturnZero(DiskLayout work) { PrintBootRepair(work); return 0; }

        private static List<PartitionRecord> CollectStreamableExtents(DiskLayout work)
        {
            var extents = new List<PartitionRecord>();
            foreach (var p in work.Partitions)
            {
                if (!p.Skipped) extents.Add(p);
            }
            return extents;
        }

        private bool PreflightDiskTarget(IBlockTarget target, List<PartitionRecord> extents, DiskRestoreOptions opts)
        {
            var need = 0L;
            foreach (var p in extents)
                need = Math.Max(need, p.OffsetBytes + p.LengthBytes);
            if (target.Capacity < need)
            {
                _log.Error($"refusing disk restore: target {target.DisplayName} holds {target.Capacity} bytes, layout {opts.LayoutGen} needs {need} (no bytes written)");
                return false;
            }
            if (RefuseLockedBitLocker(target))
                return false;
            return ConfirmAndGateTarget(target, opts.ConfirmToken, opts.AllowBootDisk, "disk restore");
        }

        private DiskLayout? FilterLayoutPartitions(DiskLayout layout, DiskRestoreOptions opts)
        {
            if (opts.Partitions == null || opts.Partitions.Count == 0)
                return layout;
            var known = new List<int>();
            foreach (var p in layout.Partitions)
                known.Add(p.PartitionNumber);
            var unknown = new List<int>();
            foreach (var n in opts.Partitions)
            {
                if (!known.Contains(n) && !unknown.Contains(n))
                    unknown.Add(n);
            }
            if (unknown.Count > 0)
            {
                unknown.Sort();
                known.Sort();
                _log.Error($"refusing disk restore: layout {opts.LayoutGen} has no partition(s) {string.Join(",", unknown)} (has: {string.Join(",", known)})");
                return null;
            }
            var want = new HashSet<int>(opts.Partitions);
            var kept = new List<PartitionRecord>();
            var auto = new List<int>();
            foreach (var p in layout.Partitions)
            {
                if (p.Skipped) { kept.Add(p); auto.Add(p.PartitionNumber); }
                else if (want.Contains(p.PartitionNumber)) kept.Add(p);
            }
            var hasData = false;
            var selEnd = 0L;
            foreach (var p in kept)
            {
                if (!p.Skipped) hasData = true;
                selEnd = Math.Max(selEnd, p.OffsetBytes + p.LengthBytes);
            }
            if (!hasData)
            {
                _log.Error($"refusing disk restore: --partitions {string.Join(",", opts.Partitions)} selects no data (all skipped-structural)");
                return null;
            }
            var sel = new List<int>(want);
            sel.Sort();
            var msg = $"restore disk layout {layout.Generation}: partitions {string.Join(",", sel)} selected";
            if (auto.Count > 0)
                msg += $" (partition(s) {string.Join(",", auto)} auto-included, skipped-structural)";
            _log.Info(msg);
            return new DiskLayout
            {
                SchemaVersion = layout.SchemaVersion, Generation = layout.Generation,
                DiskNumber = layout.DiskNumber, DiskGuid = layout.DiskGuid,
                DiskSizeBytes = selEnd, SectorSizeBytes = layout.SectorSizeBytes,
                Style = layout.Style, Partitions = kept, Hmac = layout.Hmac,
            };
        }

        private int ExecuteDiskDryRun(DiskLayout work, IBlockTarget target, int? targetDisk, bool repairBoot)
        {
            var script = DiskpartScript.Generate(work, targetDisk ?? work.DiskNumber);
            _log.Info("dry run: plan only, no bytes written");
            _log.Info(script.TrimEnd());
            if (repairBoot) LogRepairPlan(work, targetDisk ?? work.DiskNumber);
            else PrintBootRepair(work);
            File.WriteAllText(DiskpartPath(target), script);
            return 0;
        }

        private List<(PartitionRecord Part, SnapshotManifest Manifest)>? StreamDiskExtents(
            IBlockTarget target, List<PartitionRecord> extents, DiskRestoreOptions opts, Stopwatch sw, IProgress<long>? progress)
        {
            var progressTotal = 0L;
            foreach (var p in extents)
                progressTotal += p.LengthBytes;
            var throttle = LiveProgress.Create(_log, ProgressOpts, "restore", progressTotal, null, progress) ?? new ThrottledProgress(_log, progressTotal, sw, progress);
            var done = 0L;
            var manifests = new List<(PartitionRecord Part, SnapshotManifest Manifest)>();
            try
            {
                foreach (var part in extents)
                {
                    SnapshotManifest manifest;
                    try { manifest = _store.GetManifest(part.SnapshotGeneration); }
                    catch (Exception ex)
                    {
                        _log.Error($"disk restore failed: layout {opts.LayoutGen} partition {part.PartitionNumber} needs snapshot {part.SnapshotGeneration}", ex);
                        return null;
                    }
                    using var window = new OffsetBlockTarget(target, part.OffsetBytes);
                    using (var stream = window.OpenWrite())
                        _store.ExportToStream(part.SnapshotGeneration, stream, new OffsetForwarder(throttle, done));
                    manifests.Add((part, manifest));
                    done += manifest.VolumeLength;
                }
                return manifests;
            }
            catch (Exception ex)
            {
                _log.Error("disk restore streaming failed before any offsets could verify", ex);
                return null;
            }
        }

        private bool VerifyDiskExtents(IBlockTarget target, List<(PartitionRecord Part, SnapshotManifest Manifest)> manifests, DiskRestoreOptions opts)
        {
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
                return true;
            }
            var dataBad = new List<long>();
            var gapBad = new List<long>();
            try
            {
                foreach (var (part, manifest) in manifests)
                {
                    using var window = new OffsetBlockTarget(target, part.OffsetBytes);
                    var (d, g) = VerifyTarget(window, manifest, opts.StorePath);
                    dataBad.AddRange(d);
                    gapBad.AddRange(g);
                }
            }
            catch (Exception ex)
            {
                _log.Error("disk restore verify failed to re-read the target", ex);
                return false;
            }
            if (dataBad.Count > 0)
            {
                for (var i = 0; i < Math.Min(5, dataBad.Count); i++)
                    _log.Error($"disk restore verify mismatch at offset {dataBad[i]}");
                _log.Error($"disk restore verify failed: {dataBad.Count} corrupt ranges (re-run restore; do not boot this target)");
                return false;
            }
            if (gapBad.Count > 0)
                _log.Warn($"disk restore verify: {gapBad.Count} free-space range(s) hold stale bytes (reused target — cosmetic, boot-safe; first at offset {gapBad[0]})");
            return true;
        }

        // Item 5 dry-run: checks run for real (a layout that would not repair
        // fails the plan), the script apply stays unwritten.
        private void LogRepairPlan(DiskLayout layout, int targetDisk)
        {
            var blocked = BootRepairOrchestrator.CheckLayout(layout);
            if (blocked != null)
            {
                _log.Error("dry run: " + blocked);
                return;
            }
            _log.Info(BootRepairOrchestrator.Plan(layout, targetDisk).TrimEnd());
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
        // data bytes hashed with ChunkId vs the digest, holes expected
        // zero. Split result: data mismatches are corruption (fail loud,
        // do not boot); gap mismatches are stale free-space bytes on a
        // reused target (warn-and-pass — the FS never reads free space
        // expecting values, so boot-safe by construction). The store
        // id.key supplies identity; the injected Store is not involved,
        // so this works for any target.
        private static (List<long> Data, List<long> Gaps) VerifyTarget(IBlockTarget target, SnapshotManifest manifest, string? storeRoot)
        {
            if (string.IsNullOrEmpty(storeRoot) || !File.Exists(Path.Combine(storeRoot, "id.key")))
                throw new InvalidDataException("post-restore verify needs the store id.key");
            var idKey = File.ReadAllBytes(Path.Combine(storeRoot, "id.key"));
            var dataBad = new List<long>();
            var gapBad = new List<long>();
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
                        gapBad.Add(baseOffset + position);
                }
                else if (!VerifyRange(read, position, entry, idKey, buf, baseOffset))
                {
                    dataBad.Add(baseOffset + position);
                }
                position += entry.Size;
            }
            return (dataBad, gapBad);
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
