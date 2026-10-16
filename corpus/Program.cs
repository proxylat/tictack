using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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
            string? partitionsArg = null;
            string? fromFlag = null;
            string? toFlag = null;
            var cliExcludes = new List<string>();
            int chainGen = 0;
            string? confirmToken = null;
            var allowBootDisk = false;
            var skipVerify = false;
            var dryRun = false;
            var confirm = false;
            var inline = false;
            var repairBoot = false;
            string? windowsDir = null;
            string? nicDir = null;
            string? gpuDir = null;
            string? firmware = null;
            string? vhdxFlag = null;
            string? vmNameFlag = null;
            string? bootTimeoutFlag = null;
            string? bootMemoryFlag = null;

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
                    case "--list-snapshots":
                        verb = "list-snapshots";
                        break;
                    case "--list-volumes":
                        verb = "list-volumes";
                        break;
                    case "--gc":
                        verb = "gc";
                        break;
                    case "--service":
                        verb = "service";
                        break;
                    case "--locate-winre":
                        verb = "locate-winre";
                        break;
                    case "--prepare-universal":
                        verb = "prepare-universal";
                        break;
                    case "--check-boot-mode":
                        verb = "check-boot-mode";
                        break;
                    case "--check-repair":
                        verb = "check-repair";
                        break;
                    case "--boot-verify":
                        verb = "boot-verify";
                        break;
                    case "--vhdx":
                        if (i + 1 < args.Length) vhdxFlag = args[++i];
                        break;
                    case "--vm-name":
                        if (i + 1 < args.Length) vmNameFlag = args[++i];
                        break;
                    case "--timeout-seconds":
                        if (i + 1 < args.Length) bootTimeoutFlag = args[++i];
                        break;
                    case "--memory-mb":
                        if (i + 1 < args.Length) bootMemoryFlag = args[++i];
                        break;
                    case "--repair-boot":
                        repairBoot = true;
                        break;
                    case "--windows-dir":
                        if (i + 1 < args.Length) windowsDir = args[++i];
                        break;
                    case "--nic-dir":
                        if (i + 1 < args.Length) nicDir = args[++i];
                        break;
                    case "--gpu-dir":
                        if (i + 1 < args.Length) gpuDir = args[++i];
                        break;
                    case "--firmware":
                        if (i + 1 < args.Length) firmware = args[++i];
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
                        // Bare (--verify --chain) walks parent links; with a
                        // generation (--copy --chain <gen>) it selects that
                        // snapshot plus its ancestors.
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var cg) && cg > 0)
                        {
                            chain = true;
                            chainGen = cg;
                            i++;
                        }
                        else
                        {
                            chain = true;
                        }
                        break;
                    case "--copy":
                        verb = "copy";
                        break;
                    case "--sync":
                        verb = "sync";
                        break;
                    case "--from":
                        if (i + 1 < args.Length) fromFlag = args[++i];
                        break;
                    case "--to":
                        if (i + 1 < args.Length) toFlag = args[++i];
                        break;
                    case "--exclude":
                        if (i + 1 < args.Length) cliExcludes.Add(args[++i]);
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
                    case "--partitions":
                        if (i + 1 < args.Length) partitionsArg = args[++i];
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

            CorpusConfig? cfg;
            try
            {
                cfg = CorpusConfig.Load(cfgPath);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                // User error, not a crash: plain message on stderr, no stack
                // trace, no core dump (same shape as TicTack's config gate).
                Console.Error.WriteLine("Invalid config " + cfgPath + ": " + ex.Message);
                return 1;
            }
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
                    case "list-volumes":
                        return RunListVolumes(log);
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
                    case "locate-winre":
                        return RunLocateWinre(log);
                    case "prepare-universal":
                        return RunPrepareUniversal(windowsDir, drivers, nicDir, gpuDir, dryRun, log);
                    case "check-boot-mode":
                        return RunCheckBootMode(diskArg, firmware, log);
                    case "check-repair":
                        return RunCheckRepair(diskArg, log);
                    case "boot-verify":
                        return RunBootVerify(vhdxFlag, vmNameFlag, bootTimeoutFlag, bootMemoryFlag, log);
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
                        log.Info("exclusions: " + string.Join(", ", CapturePolicy.EffectiveSkip(cfg.Exclude)));
                        log.Info("Corpus config valid");
                        return 0;
                    case "image":
                        // Whole-disk target rides the same verb: --volume and
                        // --disk are mutually exclusive (RunImageDisk refuses
                        // the combo, never merges).
                        if (!string.IsNullOrEmpty(diskArg))
                            return RunImageDisk(cfg, diskArg, volume, name, incremental, parentArg, forceFull, cliExcludes, log);
                        if (string.IsNullOrEmpty(volume) || string.IsNullOrEmpty(name))
                        {
                            log.Error("--image requires --volume <path> --name <snapshot> (or --disk <N> --name <snapshot>)");
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
                                var excl = ResolveImageExclusions(cfg, source, volume, cliExcludes, log);
                                var m = excl == null
                                    ? store.ImageFast(source, name)
                                    : store.ImageFast(source, name, new ImageOptions(Exclusions: excl));
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
                            var m = store.Image(source, name, new ImageOptions(ParentGeneration: parentGen, Incremental: incremental, ForceFull: forceFull, ChangeSource: new MftDigestChangeSource(), Exclusions: ResolveImageExclusions(cfg, source, volume, cliExcludes, log)));
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
                    case "list-snapshots":
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
                    case "restore-disk":
                        return RunRestoreDisk(cfg, baseDir, layoutArg, partitionsArg, target, storeFlag, indexFlag, confirmToken, allowBootDisk, skipVerify, dryRun, repairBoot, log);
                    case "gc":
                        return RunGc(cfg, dryRun, confirm, log);
                    case "service":
                        return RunService(cfg, log);
                    case "copy":
                    case "sync":
                        return RunTransfer(cfg, verb, fromFlag, toFlag, snapshotArg, chain, chainGen, log);
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

        private static int RunListVolumes(ILogger log)
        {
            var vols = DiskInventory.ListVolumes();
            if (vols.Count == 0)
                log.Info("no ready volumes enumerated");
            foreach (var v in vols)
                Console.WriteLine(v.Name + " " + v.FileSystem + " " + v.Size);
            return 0;
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

        // --image --disk: whole-disk capture via the store lane (one manifest
        // per non-skipped partition + sealed layout sidecar). Volume captures
        // are untouched: --volume with --disk is rejected, never merged.
        private static int RunImageDisk(CorpusConfig cfg, string? diskArg, string? volume, string? name,
            bool incremental, string? parentArg, bool forceFull, List<string> cliExcludes, ILogger log)
        {
            if (string.IsNullOrEmpty(diskArg) || string.IsNullOrEmpty(name))
            {
                log.Error("--image --disk requires --disk <N> --name <snapshot>");
                return 1;
            }
            if (cliExcludes.Count > 0)
                log.Warn("--exclude applies to volume captures only, ignored for --disk");
            if (!string.IsNullOrEmpty(volume))
            {
                log.Error("--volume cannot be combined with --disk (capture a volume or a disk, not both)");
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
                Console.Error.WriteLine("--image --disk needs Windows");
                return 1;
            }
            DiskImager.CapturePlan = DiskCapture.BuildPlan;
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
        // --partitions <1,3,4> narrows to a subset (MSR auto-included).
        private static int RunRestoreDisk(CorpusConfig cfg, string baseDir, string? layoutArg, string? partitionsArg, string? target,
            string? storeFlag, string? indexFlag, string? confirmToken, bool allowBootDisk, bool skipVerify, bool dryRun, bool repairBoot, ILogger log)
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
            List<int>? partitions = null;
            if (partitionsArg != null)
            {
                partitions = new List<int>();
                foreach (var piece in partitionsArg.Split(','))
                {
                    if (!int.TryParse(piece.Trim(), out var n) || n <= 0)
                    {
                        log.Error("invalid --partitions (want comma-separated partition numbers, got: " + partitionsArg + ")");
                        return 1;
                    }
                    if (!partitions.Contains(n))
                        partitions.Add(n);
                }
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
            return orch.RestoreDisk(new DiskRestoreOptions(layoutGen, target, storePath, indexPath, confirmToken, allowBootDisk, skipVerify, dryRun, RepairBoot: repairBoot, Partitions: partitions));
        }

        // --copy/--sync: git-like delta transfer between two stores (missing
        // packs only, manifests verbatim, no format change). --copy takes one
        // snapshot (--snapshot <gen>, or --chain <gen> with ancestors);
        // --sync takes every snapshot the dest lacks. Index paths follow the
        // restore rule: store-local db when present, else the shared state db.
        private static int RunTransfer(CorpusConfig cfg, string verb, string? fromFlag, string? toFlag,
            string? snapshotArg, bool chain, int chainGen, ILogger log)
        {
            if (string.IsNullOrEmpty(fromFlag) || string.IsNullOrEmpty(toFlag))
            {
                log.Error("--" + verb + " requires --from <store> --to <store>");
                return 1;
            }
            TransferOptions opts;
            if (verb == "sync")
            {
                if (!string.IsNullOrEmpty(snapshotArg) || chain || chainGen > 0)
                {
                    log.Error("--sync copies every missing snapshot (use --copy with --snapshot/--chain for one)");
                    return 1;
                }
                opts = new TransferOptions();
            }
            else
            {
                if (chainGen > 0 && !string.IsNullOrEmpty(snapshotArg)
                    && (!int.TryParse(snapshotArg, out var sg) || sg != chainGen))
                {
                    log.Error("--snapshot and --chain name different generations (pass one)");
                    return 1;
                }
                var gen = chainGen;
                if (gen <= 0 && (!int.TryParse(snapshotArg, out gen) || gen <= 0))
                {
                    log.Error("--copy requires --snapshot <gen> or --chain <gen>");
                    return 1;
                }
                opts = new TransferOptions(Snapshot: gen, IncludeChain: chain || chainGen > 0);
            }
            var defaultDb = Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            var r = StoreTransfer.Copy(fromFlag, StoreTransfer.ResolveIndexPath(fromFlag, defaultDb),
                toFlag, StoreTransfer.ResolveIndexPath(toFlag, defaultDb), opts, log);
            Console.WriteLine($"{verb}: {r.ManifestsCopied} manifests, {r.PacksCopied} packs ({r.PackBytesCopied} bytes), {r.LandingCopied} landing, {r.LayoutsCopied} layouts");
            return 0;
        }

        // CLI --exclude (repeatable, --image only): merged over the config
        // policy (config null = built-in defaults), resolved to extents by
        // the item-4 resolver. Null only when the merged set is empty
        // (explicit opt-out) or unresolvable (non-Windows, no MFT).
        // Snapshot-first: the frozen shadow the bytes come from — a live
        // MFT could shift mid-capture and tear the manifest.
        private static IExclusionResolver? ResolveImageExclusions(CorpusConfig cfg, IBlockSource source, string volume, List<string> cliExcludes, ILogger log)
        {
            var merged = Store.MergeExclusionNames(cfg.Exclude, cliExcludes);
            if (merged.Count == 0)
                return null;
            var device = (source as IVolumeHandleProvider)?.SnapshotDevicePath;
            if (device == null)
                device = volume.Length == 2 && volume[1] == ':' && char.IsAsciiLetter(volume[0]) && OperatingSystem.IsWindows()
                    ? @"\\.\" + volume
                    : volume;
            var resolver = MftExclusionResolver.TryCreate(device, merged, log);
            if (resolver == null && !OperatingSystem.IsWindows())
                log.Warn("exclusions unresolvable on this platform (no MFT), capturing all: " + string.Join(", ", merged));
            return resolver;
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

        // Long-running host for scheduled GC + landing optimize: blocks
        // until Ctrl+C. Manual --gc is untouched; the schedule comes from
        // gc_daily_at/optimize_every (config-validated, refused loud here
        // too since --service can run with an unvalidated config).
        private static int RunService(CorpusConfig cfg, ILogger log)
        {
            if (!TimeSpan.TryParse(cfg.GcDailyAt, out var dailyAt) || dailyAt < TimeSpan.Zero || dailyAt >= TimeSpan.FromDays(1))
            {
                log.Error("gc_daily_at must be HH:mm within a day (got: " + cfg.GcDailyAt + ")");
                return 1;
            }
            if (!TimeSpan.TryParse(cfg.OptimizeEvery, out var every) || every <= TimeSpan.Zero)
            {
                log.Error("optimize_every must be a positive interval (got: " + cfg.OptimizeEvery + ")");
                return 1;
            }
            var level = cfg.Compression != null ? cfg.Compression.Level : 3;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            using var host = new GcHost(cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                cfg.Retention ?? new CorpusRetentionConfig(), log, cfg.ChunkMib, level,
                new GcSchedule(dailyAt, every), cfg.Encrypt);
            log.Info("service running: gc daily at " + cfg.GcDailyAt + ", optimize every " + cfg.OptimizeEvery + " (Ctrl+C to stop)");
            host.Run(cts.Token);
            return 0;
        }

        // Item 15a proof verb: locate winre.wim via reagentc + known path +
        // partition scan. Prints "<source> <path>", exit 1 when absent (the
        // caller falls back to the ADK path or refuses honestly).
        private static int RunLocateWinre(ILogger log)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--locate-winre needs Windows");
                return 1;
            }
            var found = WinReLocate.Locate(new WindowsWinReProbe(), log);
            if (found == null) return 1;
            Console.WriteLine(found.Source + " " + found.Path);
            return 0;
        }

        // Item 6 proof verb: offline storage-driver inject (DISM) + NIC/GPU
        // first-boot staging into a restored (offline) Windows tree. --driver
        // is required (storage inject is the core); NIC/GPU dirs warn-and-skip
        // when absent. Never runs against the live OS dir — offline only.
        private static int RunPrepareUniversal(string? windowsDir, List<string> drivers, string? nicDir, string? gpuDir, bool dryRun, ILogger log)
        {
            if (string.IsNullOrEmpty(windowsDir) || !Directory.Exists(windowsDir))
            {
                log.Error("--prepare-universal requires --windows-dir <offline Windows dir> (got: " + windowsDir + ")");
                return 1;
            }
            var rc = UniversalRestore.InjectStorageDrivers(new UniversalRestoreOptions(drivers.ToArray(), windowsDir, dryRun), new WindowsDismDriverInjector(log), log);
            if (rc != 0) return rc;
            return UniversalRestore.StageFirstBootDrivers(new FirstBootOptions(nicDir, gpuDir, windowsDir, dryRun), new FileFirstBootStager(), log);
        }

        // Item 6 boot-mode half: GPT wants UEFI, MBR wants BIOS. Firmware is
        // explicit (no guessing the target box); layout is captured live.
        private static int RunCheckBootMode(string? diskArg, string? firmware, ILogger log)
        {
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, out var diskNumber) || diskNumber < 0)
            {
                log.Error("--check-boot-mode requires --disk <N> (non-negative disk number, got: " + diskArg + ")");
                return 1;
            }
            if (!string.Equals(firmware, "UEFI", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(firmware, "BIOS", StringComparison.OrdinalIgnoreCase))
            {
                log.Error("--check-boot-mode requires --firmware <UEFI|BIOS> (got: " + firmware + ")");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--check-boot-mode needs Windows");
                return 1;
            }
            return UniversalRestore.CheckBootMode(PartitionTable.ReadDisk(diskNumber, log), firmware!, log);
        }

        // Item 5 proof verb: live-layout repair checks + diskpart/bcdboot
        // plan. Read-only (ApplyLayout/InstallBootloader only run inside
        // --restore-disk --repair-boot on the PE target) — safe on the
        // live box. Exit 1 names the blocker (ESP floor, MSR, style).
        private static int RunCheckRepair(string? diskArg, ILogger log)
        {
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, out var diskNumber) || diskNumber < 0)
            {
                log.Error("--check-repair requires --disk <N> (non-negative disk number, got: " + diskArg + ")");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--check-repair needs Windows");
                return 1;
            }
            var layout = PartitionTable.ReadDisk(diskNumber, log);
            var blocked = BootRepairOrchestrator.CheckLayout(layout);
            if (blocked != null)
            {
                log.Error(blocked);
                return 1;
            }
            Console.WriteLine(BootRepairOrchestrator.Plan(layout, diskNumber).TrimEnd());
            log.Info("repair checks passed (plan above, nothing applied)");
            return 0;
        }

        // Item 7 proof verb: boot a VHDX in Hyper-V and poll the heartbeat
        // IC to healthy (first boot takes minutes — the runner polls every
        // 15s to the timeout). No vSwitch: the verify VM stays offline.
        private static int RunBootVerify(string? vhdx, string? vmName, string? timeoutFlag, string? memoryFlag, ILogger log)
        {
            if (string.IsNullOrEmpty(vhdx))
            {
                log.Error("--boot-verify requires --vhdx <path> (export one first)");
                return 1;
            }
            var timeout = 300;
            if (!string.IsNullOrEmpty(timeoutFlag) && (!int.TryParse(timeoutFlag, out timeout) || timeout <= 0))
            {
                log.Error("--timeout-seconds must be a positive integer (got: " + timeoutFlag + ")");
                return 1;
            }
            var memoryMb = 2048;
            if (!string.IsNullOrEmpty(memoryFlag) && (!int.TryParse(memoryFlag, out memoryMb) || memoryMb <= 0))
            {
                log.Error("--memory-mb must be a positive integer (got: " + memoryFlag + ")");
                return 1;
            }
            // Both values land single-quoted in a PowerShell command: a raw
            // quote would break out, so refuse it here (trust boundary).
            if ((vhdx != null && vhdx.Contains('\'')) || (vmName != null && vmName.Contains('\'')))
            {
                log.Error("--vhdx/--vm-name must not contain single quotes");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--boot-verify needs Windows");
                return 1;
            }
            return BootVerifyOrchestrator.Verify(
                // Non-null: the IsNullOrEmpty guard above returns early.
                new BootVerifyOptions(vhdx!, vmName ?? "corpus-bootverify", timeout, memoryMb),
                new WindowsHyperVRunner(log, timeout, memoryMb), log);
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
            Console.WriteLine("  --image --disk <N> --name <snap> [--incremental | --parent <layoutGen>] [--full]  Image every non-skipped partition of PhysicalDriveN (elevated Windows)");
            Console.WriteLine("  --export --snapshot <gen> --out <path>  Export a snapshot to a file");
            Console.WriteLine("  --export-vhdx --snapshot <gen> --out <path>  Export a snapshot to dynamic VHDX");
            Console.WriteLine("  --vss-list                   Relay 'vssadmin list writers' (Windows)");
            Console.WriteLine("  --vss-snap --volume <X:>     Create+delete a VSS snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-read --volume <X:>     Read a 64 KiB extent via snapshot probe (Windows, elevated)");
            Console.WriteLine("  --vss-dump --volume <X:>     Hash one snapshot twice (read-stability probe)");
            Console.WriteLine("  --bitmap-stats --volume <X:> Used-cluster stats over a snapshot (Windows, elevated)");
            Console.WriteLine("  --bitmap-dump --volume <X:> [--device <dev>] --out <file>  Dump allocated runs (Windows, elevated)");
            Console.WriteLine("  --verify --snapshot <gen> [--chain]              Verify a snapshot (exit 1 on failure; --chain also walks parent links to genesis, a gap fails loud even if the target alone verifies)");
            Console.WriteLine("  --list-snapshots                     List snapshots (\"gen name\" lines)");
            Console.WriteLine("  --gc [--dry-run | --confirm]          Garbage-collect pruned snapshots (manual; --service runs this on schedule)");
            Console.WriteLine("      retention: keep_last/keep_within_days/pin from config; exports outside the store are untouched");
            Console.WriteLine("      retention pool is shared between volume and disk snapshots (use two stores for isolation)");
            Console.WriteLine("  --service                              Run scheduled GC (gc_daily_at) + landing optimize (optimize_every) until Ctrl+C");
            Console.WriteLine("  --locate-winre                         Print winre.wim source+path (reagentc/known-path/partition-scan; exit 1 when absent)");
            Console.WriteLine("  --restore-disk ... --repair-boot       Recreate layout + reinstall bootloader after restore (default: print manual steps)");
            Console.WriteLine("  --prepare-universal --windows-dir <offline> --driver <dir>... [--nic-dir <d>] [--gpu-dir <d>] [--dry-run]");
            Console.WriteLine("      Offline storage-driver inject (DISM) + NIC/GPU first-boot staging into a restored tree");
            Console.WriteLine("  --check-boot-mode --disk <N> --firmware <UEFI|BIOS>  Refuse GPT-on-BIOS / MBR-on-UEFI with the conversion path");
            Console.WriteLine("  --check-repair --disk <N>  Live-layout repair checks + diskpart/bcdboot plan (read-only, exit 1 names the blocker)");
            Console.WriteLine("  --boot-verify --vhdx <path> [--vm-name <n>] [--timeout-seconds <s>] [--memory-mb <mb>]  Boot a VHDX in Hyper-V, poll heartbeat to healthy (defaults 300s, 2048MB)");
            Console.WriteLine("  --build-media --staging-dir <dir> | --usb <drive> | --iso <file>  Build WinPE recovery media (exactly one sink)");
            Console.WriteLine("      [--adk <dir>] [--include-store <store>] [--db <db>] [--driver <infdir>]... [--initialize]");
            Console.WriteLine("      [--payload <dir>]  Skip re-publish (ADK box without the SDK: publish dir)");
            Console.WriteLine("  --list-disks                           List physical disks (Windows; empty on Linux)");
            Console.WriteLine("  --list-volumes                         List ready volumes (\"name filesystem bytes\" lines)");
            Console.WriteLine("  --disk-layout --disk <N>  Print DiskLayout JSON to stdout, capture nothing (Windows, elevated)");
            Console.WriteLine("  --restore-disk --layout <D> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]");
            Console.WriteLine("  --restore-disk --layout <D> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]");
            Console.WriteLine("      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run] [--partitions <1,3,4>]  Restore every non-skipped extent at its recorded offset");
            Console.WriteLine("      --partitions restores a subset (MSR auto-included, structural); target may be smaller than the source disk");
            Console.WriteLine("  --restore --snapshot <gen> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]");
            Console.WriteLine("      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run]  Restore a snapshot");
            Console.WriteLine("  --copy --from <store> --to <store> (--snapshot <gen> | --chain <gen>) [--chain]  Copy one snapshot, missing packs only (--chain includes ancestors)");
            Console.WriteLine("  --sync --from <store> --to <store>  Copy every snapshot the dest lacks, missing packs only (same id.key, or an empty dest)");
            Console.WriteLine("  --exclude <path|pattern>  (repeatable, volume captures only) merged over config exclude, recorded in the manifest ($LogFile never excludable)");
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
