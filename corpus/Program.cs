using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    // System imaging CLI (system-image-future.md). Verbs: image a block
    // source, export/verify/list snapshots, build WinPE recovery media,
    // restore a snapshot to a disk/file, list disks, validate config.
    static class Program
    {
        static int Main(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "config_corpus.yaml");
            string? verb = null;
            string? volume = null;
            string? name = null;
            string? snapshotArg = null;
            string? outPath = null;
            string? device = null;
            var incremental = false;
            string? parentArg = null;
            var forceFull = false;
            var chain = false;
            string? adkDir = null;
            string? includeStore = null;
            string? dbPath = null;
            var drivers = new List<string>();
            string? stagingDir = null;
            string? usbDrive = null;
            string? isoPath = null;
            var initialize = false;
            string? payloadDir = null;
            string? target = null;
            string? storeFlag = null;
            string? indexFlag = null;
            string? diskArg = null;
            string? layoutArg = null;
            string? confirmToken = null;
            var allowBootDisk = false;
            var skipVerify = false;
            var dryRun = false;
            var confirm = false;
            var inline = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config":
                        if (i + 1 < args.Length) cfgPath = args[++i];
                        break;
                    case "--help":
                    case "-h":
                    case "-?":
                        PrintHelp();
                        return 0;
                    case "--version":
                    case "-v":
                        PrintVersion();
                        return 0;
                    case "--validate":
                        verb = "validate";
                        break;
                    case "--image":
                        verb = "image";
                        break;
                    case "--image-disk":
                        verb = "image-disk";
                        break;
                    case "--disk-layout":
                        verb = "disk-layout";
                        break;
                    case "--restore-disk":
                        verb = "restore-disk";
                        break;
                    case "--export":
                        verb = "export";
                        break;
                    case "--export-vhdx":
                        verb = "export-vhdx";
                        break;
                    case "--verify":
                        verb = "verify";
                        break;
                    case "--list":
                        verb = "list";
                        break;
                    case "--gc":
                        verb = "gc";
                        break;
                    case "--confirm":
                        confirm = true;
                        break;
                    case "--inline":
                        inline = true;
                        break;
                    case "--vss-list":
                        verb = "vss-list";
                        break;
                    case "--vss-snap":
                        verb = "vss-snap";
                        break;
                    case "--vss-read":
                        verb = "vss-read";
                        break;
                    case "--vss-dump":
                        verb = "vss-dump";
                        break;
                    case "--bitmap-stats":
                        verb = "bitmap-stats";
                        break;
                    case "--bitmap-dump":
                        verb = "bitmap-dump";
                        break;
                    case "--device":
                        if (i + 1 < args.Length) device = args[++i];
                        break;
                    case "--volume":
                        if (i + 1 < args.Length) volume = args[++i];
                        break;
                    case "--name":
                        if (i + 1 < args.Length) name = args[++i];
                        break;
                    case "--snapshot":
                        if (i + 1 < args.Length) snapshotArg = args[++i];
                        break;
                    case "--out":
                        if (i + 1 < args.Length) outPath = args[++i];
                        break;
                    case "--incremental":
                        incremental = true;
                        break;
                    case "--parent":
                        if (i + 1 < args.Length) parentArg = args[++i];
                        break;
                    case "--full":
                        forceFull = true;
                        break;
                    case "--chain":
                        chain = true;
                        break;
                    case "--build-media":
                        verb = "build-media";
                        break;
                    case "--restore":
                        verb = "restore";
                        break;
                    case "--list-disks":
                        verb = "list-disks";
                        break;
                    case "--adk":
                        if (i + 1 < args.Length) adkDir = args[++i];
                        break;
                    case "--include-store":
                        if (i + 1 < args.Length) includeStore = args[++i];
                        break;
                    case "--db":
                        if (i + 1 < args.Length) dbPath = args[++i];
                        break;
                    case "--driver":
                        if (i + 1 < args.Length) drivers.Add(args[++i]);
                        break;
                    case "--staging-dir":
                        if (i + 1 < args.Length) stagingDir = args[++i];
                        break;
                    case "--usb":
                        if (i + 1 < args.Length) usbDrive = args[++i];
                        break;
                    case "--iso":
                        if (i + 1 < args.Length) isoPath = args[++i];
                        break;
                    case "--initialize":
                        initialize = true;
                        break;
                    case "--payload":
                        if (i + 1 < args.Length) payloadDir = args[++i];
                        break;
                    case "--target":
                        if (i + 1 < args.Length) target = args[++i];
                        break;
                    case "--disk":
                        if (i + 1 < args.Length) diskArg = args[++i];
                        break;
                    case "--layout":
                        if (i + 1 < args.Length) layoutArg = args[++i];
                        break;
                    case "--store":
                        if (i + 1 < args.Length) storeFlag = args[++i];
                        break;
                    case "--index":
                        if (i + 1 < args.Length) indexFlag = args[++i];
                        break;
                    case "--confirm-type":
                        if (i + 1 < args.Length) confirmToken = args[++i];
                        break;
                    case "--allow-boot-disk":
                        allowBootDisk = true;
                        break;
                    case "--skip-verify":
                        skipVerify = true;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + args[i]);
                        PrintHelp();
                        return 1;
                }
            }

            if (verb == null)
            {
                PrintHelp();
                return 1;
            }

            var cfg = CorpusConfig.Load(cfgPath);
            var log = CreateLogger(cfg, baseDir);
            try
            {
                // Probes are config-free diagnostics: they run before the
                // store-config gate so a bare exe can check VSS health.
                switch (verb)
                {
                    case "vss-list":
                        return VssList(baseDir);
                    case "list-disks":
                        return RunListDisks(log);
                    case "disk-layout":
                        return RunDiskLayout(diskArg, log);
                    case "vss-snap":
                    case "vss-read":
                    case "vss-dump":
                    case "bitmap-stats":
                        var probeVol = ProbeVolume(volume, log);
                        if (probeVol == null) return 1;
                        return RunProbe(verb, probeVol, log);
                    case "bitmap-dump":
                        return RunBitmapDump(volume, device, outPath, log);
                    default:
                        break;
                }

                if (cfg == null)
                {
                    if (!File.Exists(cfgPath))
                        log.Error("config not found: " + cfgPath + " (copy config_corpus.yaml.example to config_corpus.yaml beside Corpus.exe, or pass --config <path>)");
                    else
                        log.Error("config invalid (empty?): " + cfgPath);
                    return 1;
                }
                // Explicit --store overrides the baked config (recovery media
                // ships store_path '' by design; the operator points at the
                // store). Must precede Validate + index resolution below.
                if (!string.IsNullOrEmpty(storeFlag))
                    cfg.StorePath = storeFlag;
                if (!CorpusConfig.Validate(cfg, log))
                    return 1;
                if (confirm && verb != "gc")
                {
                    log.Error("--confirm is only valid with --gc");
                    return 1;
                }

                switch (verb)
                {
                    case "validate":
                        log.Info("store: " + cfg.StorePath);
                        log.Info("state root: " + CorpusConfig.StateRoot(cfg));
                        log.Info("chunk: " + cfg.ChunkMib + " MiB, zstd level " + cfg.Compression.Level);
                        log.Info("exclusions: " + string.Join(", ", CapturePolicy.EffectiveSkip(cfg.Exclusions?.Skip)));
                        log.Info("Corpus config valid");
                        return 0;
                    case "image":
                        if (string.IsNullOrEmpty(volume) || string.IsNullOrEmpty(name))
                        {
                            log.Error("--image requires --volume <path> --name <snapshot>");
                            return 1;
                        }
                        if (!inline)
                        {
                            // Default: fast landing capture (always full).
                            // Classic packed capture (+ incrementals) via --inline.
                            if (incremental || parentArg != null || forceFull)
                            {
                                log.Error("--incremental/--parent/--full need --inline (fast capture is always full)");
                                return 1;
                            }
                            using (var store = CreateStore(cfg, log))
                            using (var source = OpenSource(volume, log))
                            {
                                var m = store.ImageFast(source, name);
                                log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks (landing, optimize pending)");
                            }
                            return 0;
                        }
                        if (incremental && parentArg != null)
                        {
                            log.Error("--incremental and --parent are mutually exclusive");
                            return 1;
                        }
                        var parentGen = 0;
                        if (parentArg != null && !TryParent(parentArg, log, out parentGen))
                            return 1;
                        using (var store = CreateStore(cfg, log))
                        using (var source = OpenSource(volume, log))
                        {
                            // MFT-digest default (USN hint inside for
                            // pre-digest parents and non-MFT sources).
                            var m = store.Image(source, name, new ImageOptions(ParentGeneration: parentGen, Incremental: incremental, ForceFull: forceFull, ChangeSource: new MftDigestChangeSource()));
                            log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks");
                        }
                        return 0;
                    case "export":
                        if (!TrySnapshot(snapshotArg, log, out var exportGen) || string.IsNullOrEmpty(outPath))
                        {
                            log.Error("--export requires --snapshot <gen> --out <path>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            store.Export(exportGen, outPath);
                        return 0;
                    case "export-vhdx":
                        if (!TrySnapshot(snapshotArg, log, out var vhdxGen) || string.IsNullOrEmpty(outPath))
                        {
                            log.Error("--export-vhdx requires --snapshot <gen> --out <path>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            store.ExportVhdx(vhdxGen, outPath);
                        return 0;
                    case "verify":
                        if (!TrySnapshot(snapshotArg, log, out var verifyGen))
                        {
                            log.Error("--verify requires --snapshot <gen>");
                            return 1;
                        }
                        using (var store = CreateStore(cfg, log))
                            return store.Verify(verifyGen, chain) ? 0 : 1;
                    case "list":
                        using (var store = CreateStore(cfg, log))
                        {
                            foreach (var (gen, snap) in store.ListSnapshots())
                                Console.WriteLine(gen + " " + snap);
                        }
                        return 0;
                    case "build-media":
                        MediaBuilder.Build(new MediaBuildOptions(adkDir, includeStore, dbPath, drivers.ToArray(), stagingDir, usbDrive, isoPath, initialize, payloadDir), log);
                        return 0;
                    case "restore":
                        return RunRestore(cfg, baseDir, snapshotArg, target, storeFlag, indexFlag, confirmToken, allowBootDisk, skipVerify, dryRun, log);
                    case "image-disk":
                        return RunImageDisk(cfg, diskArg, volume, name, incremental, parentArg, forceFull, log);
                    case "restore-disk":
                        return RunRestoreDisk(cfg, baseDir, layoutArg, target, storeFlag, indexFlag, confirmToken, allowBootDisk, skipVerify, dryRun, log);
                    case "gc":
                        return RunGc(cfg, dryRun, confirm, log);
                    default:
                        return 1;
                }
            }
            catch (Exception ex)
            {
                log.Error("corpus " + verb + " failed", ex);
                return 1;
            }
            finally
            {
                (log as IDisposable)?.Dispose();
            }
        }

        private static ILogger CreateLogger(CorpusConfig? cfg, string baseDir)
        {
            var logPath = cfg != null && cfg.Logging != null && !string.IsNullOrEmpty(cfg.Logging.Path)
                ? cfg.Logging.Path
                : Path.Combine(baseDir, "corpus.log");
            var backupDir = cfg != null ? CorpusConfig.StateRoot(cfg) : baseDir;
            // Shared factory (primitive params, no TicTack config model):
            // size-rotated primary + time-bounded .bak + optional console.
            return LoggerFactory.Create(
                cfg != null && cfg.Logging != null ? cfg.Logging.Level : null,
                logPath, 10, 5, null,
                console: cfg == null || cfg.Logging == null || cfg.Logging.Console,
                eventLog: false,
                backupPath: Path.Combine(backupDir, "corpus.log.bak"));
        }

        private static Store CreateStore(CorpusConfig cfg, ILogger log)
        {
            return new Store(
                cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                cfg.ChunkMib, cfg.Compression.Level, log,
                cfg.Encrypt);
        }

        private static int RunListDisks(ILogger log)
        {
            var disks = DiskInventory.ListDisks();
            if (disks.Count == 0)
                log.Info("no physical disks enumerated (Linux returns empty; on Windows run elevated)");
            foreach (var d in disks)
                Console.WriteLine(d.Number + " " + d.DevicePath + " " + d.Size + (d.IsBoot ? " boot" : ""));
            return 0;
        }

        // --disk-layout: discovery verb (config-free, like --list-disks):
        // live GPT read, capture nothing, JSON to stdout. Linux → exit 1.
        private static int RunDiskLayout(string? diskArg, ILogger log)
        {
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, out var diskNumber) || diskNumber < 0)
            {
                log.Error("--disk-layout requires --disk <N> (non-negative disk number, got: " + diskArg + ")");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--disk-layout needs Windows");
                return 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(PartitionTable.ReadDisk(diskNumber, log)));
            return 0;
        }

        // Read-only media (DVD ISO-9660, write-protected USB): SQLite needs a
        // writable db (WAL/journal live beside it) even for pure reads, so a
        // media db copy is staged to temp. Missing db passes through — Store
        // fails loud with its own message, never a confusing copy error.
        private static string EnsureWritableIndex(string indexPath, ILogger log)
        {
            try
            {
                var dir = Path.GetDirectoryName(indexPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    var probe = Path.Combine(dir, ".corpus-write-probe");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                }
                return indexPath;
            }
            catch when (File.Exists(indexPath))
            {
                // The DVD copy carries the read-only attribute (ISO-9660) and
                // File.Copy preserves it: a second restore in one boot would
                // fail overwriting its own staged copy. Normalize + reset.
                var tmp = Path.Combine(Path.GetTempPath(), "corpus-restore-index.db");
                foreach (var p in new[] { tmp, tmp + "-wal", tmp + "-shm", tmp + "-journal" })
                {
                    if (File.Exists(p))
                    {
                        File.SetAttributes(p, FileAttributes.Normal);
                        File.Delete(p);
                    }
                }
                File.Copy(indexPath, tmp);
                File.SetAttributes(tmp, FileAttributes.Normal);
                log.Info($"index on read-only media, staged writable copy at {tmp}");
                return tmp;
            }
        }

        // --restore: --store wins, else the baked config; relative store
        // paths resolve beside the exe (the stick's store_path is 'store').
        // Index: --index, else the media db copy, else StateRoot. The id.key
        // check runs BEFORE new Store — restore must never mint a key.
        private static int RunRestore(CorpusConfig cfg, string baseDir, string? snapshotArg, string? target,
            string? storeFlag, string? indexFlag, string? confirmToken, bool allowBootDisk, bool skipVerify, bool dryRun, ILogger log)
        {
            if (!TrySnapshot(snapshotArg, log, out var gen) || string.IsNullOrEmpty(target))
            {
                log.Error("--restore requires --snapshot <gen> --target <disk#|\\\\.\\PhysicalDriveN|file>");
                return 1;
            }
            var storePath = !string.IsNullOrEmpty(storeFlag) ? storeFlag : cfg.StorePath;
            if (string.IsNullOrEmpty(storePath))
            {
                log.Error("--restore needs --store <path> (or store_path in config)");
                return 1;
            }
            if (!Path.IsPathRooted(storePath))
                storePath = Path.GetFullPath(Path.Combine(baseDir, storePath));
            if (!RestoreOrchestrator.HasIdentityKey(storePath))
            {
                log.Error($"no id.key in {storePath} (restore never creates one — check --store)");
                return 1;
            }
            var mediaDb = Path.Combine(storePath, "corpus-chunks.db");
            var indexPath = !string.IsNullOrEmpty(indexFlag) ? indexFlag
                : File.Exists(mediaDb) ? mediaDb
                : Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            indexPath = EnsureWritableIndex(indexPath, log);
            using var store = new Store(storePath, indexPath, cfg.ChunkMib, cfg.Compression.Level, log, cfg.Encrypt);
            var orch = new RestoreOrchestrator(store, log, RestoreOrchestrator.OpenTarget);
            return orch.Restore(new RestoreOptions(gen, target, storePath, indexPath, confirmToken, allowBootDisk, skipVerify, dryRun));
        }

        // --image-disk: whole-disk capture via the store lane (one manifest
        // per non-skipped partition + sealed layout sidecar). Volume verbs
        // are untouched: --volume with --disk is rejected, never merged.
        private static int RunImageDisk(CorpusConfig cfg, string? diskArg, string? volume, string? name,
            bool incremental, string? parentArg, bool forceFull, ILogger log)
        {
            if (string.IsNullOrEmpty(diskArg) || string.IsNullOrEmpty(name))
            {
                log.Error("--image-disk requires --disk <N> --name <snapshot>");
                return 1;
            }
            if (!string.IsNullOrEmpty(volume))
            {
                log.Error("--volume cannot be combined with --image-disk --disk (use --image for volumes)");
                return 1;
            }
            if (incremental && parentArg != null)
            {
                log.Error("--incremental and --parent are mutually exclusive");
                return 1;
            }
            if (!int.TryParse(diskArg, out var diskNumber) || diskNumber < 0)
            {
                log.Error("invalid --disk (want a non-negative disk number, got: " + diskArg + ")");
                return 1;
            }
            var parentGen = 0;
            if (parentArg != null && !TryParent(parentArg, log, out parentGen))
                return 1;
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--image-disk needs Windows");
                return 1;
            }
            using (var store = CreateStore(cfg, log))
            {
                var layout = store.ImageDisk(diskNumber, name,
                    new DiskImageOptions(Incremental: incremental, ForceFull: forceFull, ParentLayoutGen: parentGen));
                log.Info($"disk image {layout.Generation:D6}-disk{diskNumber}: {layout.Partitions.Count} partitions");
            }
            return 0;
        }

        // --restore-disk: whole-disk restore via RestoreDisk (one manifest
        // per non-skipped extent at its recorded offset). Store/index/token
        // posture mirrors --restore; validation errors stay byte-identical
        // to the stub this replaced (Cli_RestoreDisk_RequiresLayout pins them).
        private static int RunRestoreDisk(CorpusConfig cfg, string baseDir, string? layoutArg, string? target,
            string? storeFlag, string? indexFlag, string? confirmToken, bool allowBootDisk, bool skipVerify, bool dryRun, ILogger log)
        {
            if (string.IsNullOrEmpty(layoutArg) || string.IsNullOrEmpty(target))
            {
                log.Error("--restore-disk requires --layout <gen> --target <disk#|\\\\.\\PhysicalDriveN|file>");
                return 1;
            }
            if (!int.TryParse(layoutArg, out var layoutGen) || layoutGen <= 0)
            {
                log.Error("invalid --layout (want a positive layout generation number, got: " + layoutArg + ")");
                return 1;
            }
            var storePath = !string.IsNullOrEmpty(storeFlag) ? storeFlag : cfg.StorePath;
            if (string.IsNullOrEmpty(storePath))
            {
                log.Error("--restore-disk needs --store <path> (or store_path in config)");
                return 1;
            }
            if (!Path.IsPathRooted(storePath))
                storePath = Path.GetFullPath(Path.Combine(baseDir, storePath));
            if (!RestoreOrchestrator.HasIdentityKey(storePath))
            {
                log.Error($"no id.key in {storePath} (restore never creates one — check --store)");
                return 1;
            }
            var mediaDb = Path.Combine(storePath, "corpus-chunks.db");
            var indexPath = !string.IsNullOrEmpty(indexFlag) ? indexFlag
                : File.Exists(mediaDb) ? mediaDb
                : Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            indexPath = EnsureWritableIndex(indexPath, log);
            using var store = new Store(storePath, indexPath, cfg.ChunkMib, cfg.Compression.Level, log, cfg.Encrypt);
            var orch = new RestoreOrchestrator(store, log, RestoreOrchestrator.OpenTarget);
            return orch.RestoreDisk(new DiskRestoreOptions(layoutGen, target, storePath, indexPath, confirmToken, allowBootDisk, skipVerify, dryRun));
        }

        // --gc: manual only, never automatic. Bare prints the plan and
        // asks for --confirm (exit 1); --dry-run plans without deleting
        // (exit 0); --confirm executes (exit 0).
        private static int RunGc(CorpusConfig cfg, bool dryRun, bool confirm, ILogger log)
        {
            if (dryRun && confirm)
            {
                log.Error("--dry-run and --confirm are mutually exclusive");
                return 1;
            }
            var gc = new GarbageCollector(
                cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                cfg.Retention ?? new CorpusRetentionConfig(), log);
            var plan = gc.Plan();
            if (plan.DropManifests.Count == 0 && plan.DropPacks.Count == 0 && plan.DropLayouts.Count == 0)
            {
                Console.WriteLine("nothing to collect");
                log.Info("nothing to collect");
                return 0;
            }
            Console.WriteLine($"gc plan: drop {plan.DropManifests.Count} snapshots, {plan.DeadChunks} chunks, {plan.DeadBytes} bytes, {plan.DropPacks.Count} packs");
            foreach (var m in plan.DropManifests)
                Console.WriteLine("  drop manifest: " + m);
            foreach (var l in plan.DropLayouts)
                Console.WriteLine("  drop layout: " + l);
            foreach (var p in plan.DropPacks)
                Console.WriteLine("  drop pack: " + p);
            foreach (var n in plan.Notes)
            {
                Console.WriteLine("  note: " + n);
                log.Warn(n);
            }
            if (confirm)
            {
                gc.Execute(plan);
                // Mirrors the Execute summary: plan lines print to stdout,
                // so the result must too (log-only is invisible console:false).
                Console.WriteLine($"gc: dropped {plan.DropManifests.Count} snapshots, {plan.DeadChunks} chunks, {plan.DeadBytes} bytes, {plan.DropPacks.Count} packs");
                return 0;
            }
            if (dryRun)
            {
                Console.WriteLine("marks only, no deletes");
                return 0;
            }
            Console.WriteLine("re-run with --confirm");
            return 1;
        }

        // Drive-letter-colon ("T:") images the live volume through VSS;
        // anything else is a file path for FileBlockSource.
        private static IBlockSource OpenSource(string volume, ILogger log) =>
            volume.Length == 2 && volume[1] == ':' && char.IsAsciiLetter(volume[0])
                ? new VssBlockSource(volume, log)
                : new FileBlockSource(volume);

        private static string? ProbeVolume(string? volume, ILogger log)
        {
            if (string.IsNullOrEmpty(volume))
            {
                log.Error("needs --volume <drive, e.g. T:>");
                return null;
            }
            return volume;
        }

        private static int VssList(string baseDir)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--vss-list needs Windows");
                return 1;
            }
            var r = ProcessRunner.RunAsync("vssadmin list writers", baseDir, true).GetAwaiter().GetResult();
            Console.WriteLine(r.Stdout);
            if (r.TimedOut) Console.Error.WriteLine("vssadmin timed out");
            if (!string.IsNullOrEmpty(r.Stderr)) Console.Error.WriteLine(r.Stderr);
            return r.TimedOut ? 1 : r.ExitCode;
        }

        private static int RunProbe(string verb, string volume, ILogger log)
        {
            // Probes never Complete: AbortBackup is the honest teardown for
            // reads that capture nothing (see VssSnapshot).
            switch (verb)
            {
                case "vss-snap":
                    using (var s = VssSnapshot.Create(volume, log))
                        log.Info("device: " + s.DeviceObject);
                    return 0;
                case "vss-read":
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var dev = s.DeviceObject.TrimEnd('\\');
                        using var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        // True device length (not MaxValue): the segment
                        // reader sizes fills from it, and an overlong Length
                        // would overread past EOF into a short-read throw.
                        // Callback unobserved (probes never Complete).
                        using var fs = new VssReadStream(raw, VssBlockSource.DeviceLength(volume, dev), () => { });
                        var buf = new byte[64 * 1024];
                        var filled = 0;
                        int n;
                        while (filled < buf.Length && (n = fs.Read(buf, filled, buf.Length - filled)) > 0)
                            filled += n;
                        using var sha = SHA256.Create();
                        log.Info($"read {filled} bytes: sha256={Convert.ToHexString(sha.ComputeHash(buf, 0, filled))} head={Convert.ToHexString(buf, 0, Math.Min(8, filled))}");
                    }
                    return 0;
                case "vss-dump":
                    // Transport-vs-isolation probe: ONE snapshot, read TWICE
                    // end to end (same chunker shape as imaging). Differing
                    // hashes = unreliable snapshot reads; matching hashes
                    // that differ from live = VSS isolation failure.
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var dev = s.DeviceObject.TrimEnd('\\');
                        var vlen = VssBlockSource.DeviceLength(volume, dev);
                        string h1, h2;
                        using (var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var fs = new VssReadStream(raw, vlen, () => { }))
                            h1 = HashStream(fs);
                        using (var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var fs = new VssReadStream(raw, vlen, () => { }))
                            h2 = HashStream(fs);
                        log.Info($"vss-dump {volume} pass1={h1} pass2={h2} stable={h1 == h2}");
                    }
                    return 0;
                case "bitmap-stats":
                    using (var s = VssSnapshot.Create(volume, log))
                    {
                        var bpc = VolumeBitmap.BytesPerCluster(volume + "\\");
                        var total = VolumeBitmap.TotalBytes(volume + "\\");
                        var runs = VolumeBitmap.ReadRuns(s.DeviceObject.TrimEnd('\\'), total / bpc);
                        long used = 0;
                        foreach (var r in runs) used += r.Count;
                        log.Info($"{volume} {used}/{total / bpc} clusters used ({used * bpc}/{total} bytes) in {runs.Count} runs");
                        for (var i = 0; i < Math.Min(5, runs.Count); i++)
                            log.Info($"  run {i}: LCN {runs[i].Start} +{runs[i].Count}");
                    }
                    return 0;
                default:
                    return 1;
            }
        }

        // Runs dump for harness-side masked compares. --volume gives geometry
        // (identical on live root and its shadows); --device reads an existing
        // frozen device instead of snapshotting (zero skew vs a hashed shadow).
        private static int RunBitmapDump(string? volume, string? device, string? outPath, ILogger log)
        {
            if (string.IsNullOrEmpty(outPath))
            {
                log.Error("--bitmap-dump requires --out <path>");
                return 1;
            }
            var vol = ProbeVolume(volume, log);
            if (vol == null) return 1;
            var bpc = VolumeBitmap.BytesPerCluster(vol + "\\");
            var total = VolumeBitmap.TotalBytes(vol + "\\");
            List<(long Start, long Count)> runs;
            if (!string.IsNullOrEmpty(device))
            {
                runs = VolumeBitmap.ReadRuns(device, total / bpc);
            }
            else
            {
                using var s = VssSnapshot.Create(vol, log);
                runs = VolumeBitmap.ReadRuns(s.DeviceObject.TrimEnd('\\'), total / bpc);
            }
            VolumeBitmap.WriteRuns(outPath, bpc, total / bpc, runs);
            log.Info($"bitmap-dump {vol}: {runs.Count} runs -> {outPath}");
            return 0;
        }

        private static string HashStream(Stream s)
        {
            using var sha = SHA256.Create();
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }

        private static bool TrySnapshot(string? arg, ILogger log, out int generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out generation) || generation <= 0)
            {
                log.Error("invalid --snapshot (want a positive generation number, got: " + arg + ")");
                return false;
            }
            return true;
        }

        private static bool TryParent(string? arg, ILogger log, out int generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out generation) || generation <= 0)
            {
                log.Error("invalid --parent (want a positive generation number, got: " + arg + ")");
                return false;
            }
            return true;
        }

        static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Console.WriteLine("Corpus " + (info ?? asm.GetName().Version?.ToString() ?? "unknown"));
        }

        static void PrintHelp()
        {
            Console.WriteLine("Corpus — system imaging");
            Console.WriteLine();
            Console.WriteLine("  --image --volume <path|X:> --name <snap>   Fast landing capture (default, always full; run --gc/optimize to fold)");
            Console.WriteLine("      --inline [--incremental | --parent <gen>] [--full]   Classic packed capture instead of landing");
            Console.WriteLine("  --export --snapshot <gen> --out <path>  Export a snapshot to a file");
            Console.WriteLine("  --export-vhdx --snapshot <gen> --out <path>  Export a snapshot to dynamic VHDX");
            Console.WriteLine("  --vss-list                   Relay 'vssadmin list writers' (Windows)");
            Console.WriteLine("  --vss-snap --volume <X:>     Create+delete a VSS snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-read --volume <X:>     Read a 64 KiB extent via snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-dump --volume <X:>     Hash one snapshot twice (read-stability probe)");
            Console.WriteLine("  --bitmap-stats --volume <X:> Used-cluster stats over a snapshot (Windows, elevated)");
            Console.WriteLine("  --bitmap-dump --volume <X:> [--device <dev>] --out <file>  Dump allocated runs (Windows, elevated)");
            Console.WriteLine("  --verify --snapshot <gen> [--chain]              Verify a snapshot (exit 1 on failure; --chain also walks parent links to genesis, a gap fails loud even if the target alone verifies)");
            Console.WriteLine("  --list                                 List snapshots (\"gen name\" lines)");
            Console.WriteLine("  --gc [--dry-run | --confirm]          Garbage-collect pruned snapshots (manual only, never automatic)");
            Console.WriteLine("      retention: keep_last/keep_within_days/pin from config; exports outside the store are untouched");
            Console.WriteLine("      retention pool is shared between volume and disk snapshots (use two stores for isolation)");
            Console.WriteLine("  --build-media --staging-dir <dir> | --usb <drive> | --iso <file>  Build WinPE recovery media (exactly one sink)");
            Console.WriteLine("      [--adk <dir>] [--include-store <store>] [--db <db>] [--driver <infdir>]... [--initialize]");
            Console.WriteLine("      [--payload <dir>]  Skip re-publish (ADK box without the SDK: publish dir)");
            Console.WriteLine("  --list-disks                           List physical disks (Windows; empty on Linux)");
            Console.WriteLine("  --image-disk --disk <N> --name <snap> [--incremental | --parent <layoutGen>] [--full]  Image every non-skipped partition of PhysicalDriveN (elevated Windows)");
            Console.WriteLine("      --volume cannot be combined with --disk");
            Console.WriteLine("  --disk-layout --disk <N>  Print DiskLayout JSON to stdout, capture nothing (Windows, elevated)");
            Console.WriteLine("  --restore-disk --layout <D> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]");
            Console.WriteLine("      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run]  Restore every non-skipped extent at its recorded offset");
            Console.WriteLine("  --restore --snapshot <gen> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]");
            Console.WriteLine("      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run]  Restore a snapshot");
            Console.WriteLine("  --validate             Check config_corpus.yaml and exit");
            Console.WriteLine("  --config <path>        Config file (default: config_corpus.yaml beside the exe)");
            Console.WriteLine("  --version              Print version and exit");
            Console.WriteLine("  --help                 This text");
            Console.WriteLine();
            Console.WriteLine("  incrementals reuse the parent's clean chunks without re-reading; any doubt → full re-image");
            Console.WriteLine("  per-pack encryption is an external shell hook (encrypt: in config), never builtin crypto");
            Console.WriteLine("  restore streams chunk-by-chunk, then re-reads the whole target to verify:");
            Console.WriteLine("    wall time is ~2x sequential device speed; --confirm-type is required always");
            Console.WriteLine("    (even --dry-run); a refused run names the want-TOKEN to re-run with");
        }
    }
}
