using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Linq;
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
            string? toVhdx = null;
            string? toWim = null;
            string? toEsd = null;
            var zstd = false;
            string? vhdxWriter = null;
            string? budgetBytes = null;
            string? extractPaths = null;
            string? cloneFrom = null;
            string? retryLockFlag = null;
            var append = false;
            var bufferedIo = false;
            var skipIfUnchanged = false;
            string? splitSwm = null;
            var optimizeVhd = false;
            var recompress = false;
            var trimResetBase = false;
            var trimFeatures = new List<string>();
            var trimPackages = new List<string>();
            var trimNoHibernate = false;
            string? wimPath = null;
            string? mountDir = null;
            var commit = false;
            var discard = false;
            string? compressFlag = null;
            string? swmPath = null;
            string? unattendFile = null;
            var langPacks = new List<string>();
            string? regHive = null;
            string? regKey = null;
            string? regName = null;
            string? regType = null;
            string? regData = null;
            var removeKbs = new List<string>();

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
                    case "--to-vhdx":
                        if (i + 1 < args.Length) toVhdx = args[++i];
                        break;
                    case "--to-wim":
                        if (i + 1 < args.Length) toWim = args[++i];
                        break;
                    case "--to-esd":
                        if (i + 1 < args.Length) toEsd = args[++i];
                        break;
                    case "--zstd":
                        zstd = true;
                        break;
                    case "--vhdx-writer":
                        // TEMPORARY (§12.1 phase 1): A/B spike selector, dies
                        // with the loser the day the verdict lands.
                        if (i + 1 < args.Length) vhdxWriter = args[++i];
                        break;
                    case "--optimize-to-fit":
                        verb = "optimize-to-fit";
                        break;
                    case "--bytes":
                        if (i + 1 < args.Length) budgetBytes = args[++i];
                        break;
                    case "--extract":
                        verb = "extract";
                        break;
                    case "--paths":
                        if (i + 1 < args.Length) extractPaths = args[++i];
                        break;
                    case "--clone-from":
                        if (i + 1 < args.Length) cloneFrom = args[++i];
                        break;
                    case "--retry-lock":
                        if (i + 1 < args.Length) retryLockFlag = args[++i];
                        break;
                    case "--append":
                        append = true;
                        break;
                    case "--buffered-io":
                        bufferedIo = true;
                        break;
                    case "--skip-if-unchanged":
                        skipIfUnchanged = true;
                        break;
                    case "--split-swm":
                        if (i + 1 < args.Length) splitSwm = args[++i];
                        break;
                    case "--optimize-vhd":
                        optimizeVhd = true;
                        break;
                    case "--optimize-wim":
                        verb = "optimize-wim";
                        break;
                    case "--recompress":
                        recompress = true;
                        break;
                    case "--record-compact":
                        verb = "record-compact";
                        break;
                    case "--trim-resetbase":
                        trimResetBase = true;
                        break;
                    case "--trim-disable-feature":
                        if (i + 1 < args.Length) trimFeatures.Add(args[++i]);
                        break;
                    case "--trim-remove-package":
                        if (i + 1 < args.Length) trimPackages.Add(args[++i]);
                        break;
                    case "--trim-no-hibernate":
                        trimNoHibernate = true;
                        break;
                    case "--convert":
                        verb = "convert";
                        break;
                    case "--merge-swm":
                        verb = "merge-swm";
                        break;
                    case "--wim-info":
                        verb = "wim-info";
                        break;
                    case "--mount":
                        verb = "mount";
                        break;
                    case "--unmount":
                        verb = "unmount";
                        break;
                    case "--apply":
                        verb = "apply";
                        break;
                    case "--wim":
                        if (i + 1 < args.Length) wimPath = args[++i];
                        break;
                    case "--mount-dir":
                        if (i + 1 < args.Length) mountDir = args[++i];
                        break;
                    case "--commit":
                        commit = true;
                        break;
                    case "--discard":
                        discard = true;
                        break;
                    case "--compress":
                        if (i + 1 < args.Length) compressFlag = args[++i];
                        break;
                    case "--swm":
                        if (i + 1 < args.Length) swmPath = args[++i];
                        break;
                    case "--unattend":
                        if (i + 1 < args.Length) unattendFile = args[++i];
                        break;
                    case "--lang-pack":
                        if (i + 1 < args.Length) langPacks.Add(args[++i]);
                        break;
                    case "--reg-hive":
                        if (i + 1 < args.Length) regHive = args[++i];
                        break;
                    case "--reg-key":
                        if (i + 1 < args.Length) regKey = args[++i];
                        break;
                    case "--reg-name":
                        if (i + 1 < args.Length) regName = args[++i];
                        break;
                    case "--reg-type":
                        if (i + 1 < args.Length) regType = args[++i];
                        break;
                    case "--reg-data":
                        if (i + 1 < args.Length) regData = args[++i];
                        break;
                    case "--remove-kb":
                        if (i + 1 < args.Length) removeKbs.Add(args[++i]);
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
                        return RunPrepareUniversal(windowsDir, drivers, nicDir, gpuDir, dryRun, trimResetBase, trimFeatures, trimPackages, trimNoHibernate, unattendFile, langPacks, regHive, regKey, regName, regType, regData, removeKbs, log);
                    case "convert":
                        return RunConvert(wimPath, toFlag, indexFlag, compressFlag, log);
                    case "merge-swm":
                        return RunMergeSwm(swmPath, toFlag, log);
                    case "wim-info":
                        return RunWimInfo(wimPath, log);
                    case "mount":
                        return RunMount(wimPath, indexFlag, mountDir, log);
                    case "unmount":
                        return RunUnmount(mountDir, commit, discard, log);
                    case "apply":
                        return RunApply(wimPath, indexFlag, target, log);
                    case "optimize-wim":
                        return RunOptimizeWim(wimPath, recompress, log);
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
                        // §12.1 single-file backup rides --image but stores
                        // no snapshot: --volume only, never --name/--disk.
                        if (toVhdx != null || toWim != null || toEsd != null)
                            return RunSingleFile(cfg, volume, name, diskArg, toVhdx, toWim, toEsd, zstd, vhdxWriter, append, incremental, parentArg, forceFull, inline, cliExcludes, log, !bufferedIo, skipIfUnchanged, splitSwm, optimizeVhd);
                        // §12.9 writer lock: store captures serialize on the
                        // store root (storeless single-file above takes no
                        // lock). Contention/timeout throw loud via the outer
                        // handler naming the holder. Braced block: using-var is
                        // illegal directly in a switch section (CS8647).
                        {
                            // First capture initializes the store: the lock
                            // file lives inside it, so the dir must exist.
                            Directory.CreateDirectory(cfg.StorePath);
                            using var imageStoreLock = StoreLock.Acquire(cfg.StorePath, retryLockFlag == null ? null : StoreLock.ParseRetryTimeout(retryLockFlag), log);
                        // Whole-disk target rides the same verb: --volume and
                        // --disk are mutually exclusive (RunImageDisk refuses
                        // the combo, never merges).
                        if (!string.IsNullOrEmpty(diskArg))
                            return RunImageDisk(cfg, diskArg, volume, name, incremental, parentArg, forceFull, cliExcludes, log, !bufferedIo, skipIfUnchanged);
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
                            using (var source = OpenSource(volume, log, !bufferedIo))
                            {
                                var excl = ResolveImageExclusions(cfg, source, volume, cliExcludes, log);
                                var m = excl == null
                                    ? store.ImageFast(source, name, skipIfUnchanged: skipIfUnchanged)
                                    : store.ImageFast(source, name, new ImageOptions(Exclusions: excl), skipIfUnchanged);
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
                        using (var source = OpenSource(volume, log, !bufferedIo))
                        {
                            // MFT-digest default (USN hint inside for
                            // pre-digest parents and non-MFT sources).
                            var m = store.Image(source, name, new ImageOptions(ParentGeneration: parentGen, Incremental: incremental, ForceFull: forceFull, ChangeSource: new MftDigestChangeSource(), Exclusions: ResolveImageExclusions(cfg, source, volume, cliExcludes, log)), skipIfUnchanged);
                            log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks");
                        }
                        return 0;
                        }
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
                        // §12.5 reflink fast path: clone the previous export,
                        // overwrite dirty-vs-gen-N-1 ranges only. Base is
                        // always gen N-1 (manifest-compared); the file only
                        // needs matching VHDX capacity.
                        if (cloneFrom != null)
                        {
                            if (cloneFrom.StartsWith("--", StringComparison.Ordinal))
                            {
                                log.Error("bare --clone-from refuses (missing <base.vhdx>, got flag: " + cloneFrom + ")");
                                return 1;
                            }
                            using (var store = CreateStore(cfg, log))
                                ReflinkExport.ExportVhdxFromClone(store, vhdxGen, outPath, cloneFrom, log);
                            return 0;
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
                        using (StoreLock.Acquire(cfg.StorePath, retryLockFlag == null ? null : StoreLock.ParseRetryTimeout(retryLockFlag), log))
                            return RunGc(cfg, dryRun, confirm, log);
                    case "service":
                        return RunService(cfg, log);
                    case "copy":
                    case "sync":
                        return RunTransfer(cfg, verb, fromFlag, toFlag, snapshotArg, chain, chainGen, log);
                    case "optimize-to-fit":
                        long? budget = null;
                        if (budgetBytes != null)
                        {
                            if (!long.TryParse(budgetBytes, out var b) || b <= 0)
                            {
                                log.Error("invalid --bytes (want a positive byte count, got: " + budgetBytes + ")");
                                return 1;
                            }
                            budget = b;
                        }
                        var optimizer = new StoreOptimizer(cfg.StorePath, Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"), cfg.Retention ?? new CorpusRetentionConfig(), log, cfg.ChunkMib, cfg.Compression.Level, cfg.Encrypt);
                        using (StoreLock.Acquire(cfg.StorePath, retryLockFlag == null ? null : StoreLock.ParseRetryTimeout(retryLockFlag), log))
                            optimizer.Optimize(budget);
                        return 0;
                    case "extract":
                        if (!TrySnapshot(snapshotArg, log, out var extractGen) || string.IsNullOrEmpty(extractPaths) || string.IsNullOrEmpty(outPath))
                        {
                            log.Error("--extract requires --snapshot <gen> --paths <a,b> --out <dir>");
                            return 1;
                        }
                        // No Trim: NTFS names can hold spaces.
                        var index = EnsureWritableIndex(Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"), log);
                        var summary = SelectiveExtract.Extract(cfg.StorePath, index, extractGen, extractPaths.Split(','), outPath, log);
                        log.Info($"extracted {summary.Files} files + {summary.Directories} dirs ({summary.Bytes} bytes, {summary.Skipped} skipped) from snapshot {extractGen}");
                        return 0;
                    case "record-compact":
                        // §12.14 Compact OS: record-only. Queries the live (or
                        // offline --windows-dir) OS state and files it beside
                        // the snapshot manifest. Restore writes blocks, never
                        // DISM-applies, so there is no apply step to attach.
                        if (!TrySnapshot(snapshotArg, log, out var compactGen))
                        {
                            log.Error("--record-compact requires --snapshot <gen> [--windows-dir <offline>]");
                            return 1;
                        }
                        if (!OperatingSystem.IsWindows())
                        {
                            log.Error("--record-compact needs Windows (compact.exe query)");
                            return 1;
                        }
                        var state = CompactQuery.Query(new CompactQuery.ProcessCompactRunner(), windowsDir);
                        CompactStateRecord.Write(cfg.StorePath, compactGen, state, log);
                        return 0;
                    default:
                        return 1;
                }
            }
            catch (StoredSkippedException)
            {
                // §12.8: Store already logged the exact skip line at INFO;
                // exit 0 without logging again (else it appears twice).
                return 0;
            }
            catch (StoreLockException ex)
            {
                // Operator error (bad duration, contended store), not a
                // crash: plain message, no stack (house error shape).
                log.Error(ex.Message);
                return 1;
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
            bool incremental, string? parentArg, bool forceFull, List<string> cliExcludes, ILogger log, bool useUnbuffered = true, bool skipIfUnchanged = false)
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
            DiskImager.CapturePlan = (d, l) => DiskCapture.BuildPlan(d, l, useUnbuffered);
            using (var store = CreateStore(cfg, log))
            {
                var layout = store.ImageDisk(diskNumber, name,
                    new DiskImageOptions(Incremental: incremental, ForceFull: forceFull, ParentLayoutGen: parentGen), skipIfUnchanged);
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

        // §12.1 single-file backup: capture straight to one file, no chunk
        // store, no snapshot. Exactly one of --to-vhdx/--to-wim/--to-esd;
        // store concepts (--name/--inline/--incremental/--parent/--full)
        // and --disk are refused, never merged.
        private static int RunSingleFile(CorpusConfig cfg, string? volume, string? name, string? diskArg, string? toVhdx, string? toWim, string? toEsd, bool zstd, string? vhdxWriter, bool append, bool incremental, string? parentArg, bool forceFull, bool inline, List<string> cliExcludes, ILogger log, bool useUnbuffered = true, bool skipIfUnchanged = false, string? splitSwm = null, bool optimizeVhd = false)
        {
            var kinds = 0;
            if (toVhdx != null) kinds++;
            if (toWim != null) kinds++;
            if (toEsd != null) kinds++;
            if (kinds != 1)
            {
                log.Error("--to-vhdx/--to-wim/--to-esd are mutually exclusive (exactly one)");
                return 1;
            }
            // Bare flag swallowed the next flag as its value — refuse, never
            // write a file literally named "--zstd".
            foreach (var t in new[] { toVhdx, toWim, toEsd })
            {
                if (t != null && t.StartsWith("--", StringComparison.Ordinal))
                {
                    log.Error("bare --to-vhdx/--to-wim/--to-esd refuses (missing <file|dir>, got flag: " + t + ")");
                    return 1;
                }
            }
            if (string.IsNullOrEmpty(volume))
            {
                log.Error("--image --to-vhdx/--to-wim/--to-esd requires --volume <path|X:>");
                return 1;
            }
            if (!string.IsNullOrEmpty(diskArg))
            {
                log.Error("--to-vhdx/--to-wim/--to-esd cannot combine with --disk");
                return 1;
            }
            if (!string.IsNullOrEmpty(name))
            {
                log.Error("--to-vhdx/--to-wim/--to-esd store no snapshot, --name is refused (drop it)");
                return 1;
            }
            if (incremental || forceFull || inline)
            {
                log.Error("--to-vhdx/--to-wim/--to-esd take no --inline/--incremental/--full (store concepts)");
                return 1;
            }
            // Storeless outputs write no gen: nothing to compare, so the
            // flag is refused rather than silently ignored.
            if (skipIfUnchanged)
            {
                log.Error("--skip-if-unchanged needs a store capture (--image/--disk without --to-*)");
                return 1;
            }
            if (parentArg != null && toVhdx == null)
            {
                log.Error("--parent <base.vhdx> needs --to-vhdx (differencing child of a full VHDX)");
                return 1;
            }
            if (zstd && toVhdx == null)
            {
                log.Error("--zstd needs --to-vhdx (WIM/ESD are already compressed)");
                return 1;
            }
            if (vhdxWriter != null && toVhdx == null)
            {
                log.Error("--vhdx-writer needs --to-vhdx");
                return 1;
            }
            // §12.29 Optimize-VHD compacts a full VHDX only — the
            // differencing child's blocks fall through to the parent, so
            // compacting it is refused (unprobed interaction), never silent.
            if (optimizeVhd && toVhdx == null)
            {
                log.Error("--optimize-vhd needs --to-vhdx (full VHDX post-step)");
                return 1;
            }
            if (optimizeVhd && parentArg != null)
            {
                log.Error("--optimize-vhd refuses a --parent differencing child (compact the full parent instead)");
                return 1;
            }
            // Fail fast off-Windows: Compact would run the whole backup
            // first, then throw. Same message shape as the runner's.
            if (optimizeVhd && !OperatingSystem.IsWindows())
            {
                log.Error("--optimize-vhd needs Windows (Hyper-V Optimize-VHD)");
                return 1;
            }
            // §12.14 SWM split: chop a --to-wim into FAT32-sized parts.
            // WIM-only by Corpus policy (DISM itself splits ESD fine,
            // proven 2026-10-17 — same posture as --append).
            int splitMb = 0;
            if (splitSwm != null)
            {
                if (splitSwm.StartsWith("--", StringComparison.Ordinal))
                {
                    log.Error("bare --split-swm refuses (missing <sizeMb>, got flag: " + splitSwm + ")");
                    return 1;
                }
                if (toWim == null)
                {
                    log.Error("--split-swm needs --to-wim <base.wim> (Corpus policy: no ESD split; DISM itself splits ESD fine)");
                    return 1;
                }
                if (!int.TryParse(splitSwm, out splitMb) || splitMb <= 0)
                {
                    log.Error("invalid --split-swm (want a positive part size in MB, got: " + splitSwm + ")");
                    return 1;
                }
            }
            // §12.11 WIM-append: this volume's state as a new image index
            // in the existing base WIM. Gen = current index count + 1
            // (one redundant Get-WimInfo; captures dwarf it).
            if (append)
            {
                if (toWim == null)
                {
                    log.Error("--append needs --to-wim <base.wim> (new image index in an existing WIM)");
                    return 1;
                }
                using (var source = OpenSource(volume, log))
                {
                    var snap = (source as IVolumeHandleProvider)?.SnapshotDevicePath;
                    if (snap == null)
                    {
                        log.Error("--to-wim --append need --volume <drive:> (a live volume, not a file: " + volume + ")");
                        return 1;
                    }
                    var snapDir = snap.EndsWith('\\') ? snap : snap + "\\";
                    var merged = Store.MergeExclusionNames(cfg.Exclude, cliExcludes);
                    var dism = new DismRunner();
                    var info = dism.Run("/Get-WimInfo /WimFile:" + ProcessRunner.QuoteArg(toWim));
                    if (info.ExitCode != 0)
                    {
                        log.Error($"--append: cannot read indexes of {toWim} (exit {info.ExitCode}: {info.Stderr})");
                        return 1;
                    }
                    SingleFileBackup.AppendWim(snapDir, toWim, SingleFileBackup.CountImageIndexes(info.Stdout) + 1, merged, dism, log);
                    if (splitMb > 0)
                        SplitWimFile(toWim, splitMb, log);
                }
                return 0;
            }
            if (toVhdx != null)
            {
                VhdxWriterKind kind;
                try
                {
                    kind = ExtentSinkFactory.ParseKind(vhdxWriter);
                }
                catch (SingleFileBackupException ex)
                {
                    log.Error(ex.Message);
                    return 1;
                }
                // §12.6 differencing child: the parent VHDX is the baseline
                // (storeless single-file has no manifest to plan against), so
                // dirty = chunks where the live source differs from the
                // parent on the store chunk grid. Exclusions bake into the
                // full parent at creation — re-specifying them here is
                // refused, never silently reinterpreted.
                if (parentArg != null)
                {
                    if (parentArg.StartsWith("--", StringComparison.Ordinal))
                    {
                        log.Error("bare --parent refuses (missing <base.vhdx>, got flag: " + parentArg + ")");
                        return 1;
                    }
                    if (kind != VhdxWriterKind.DiscUtils)
                    {
                        log.Error("--parent <base.vhdx> needs the DiscUtils writer (differencing create is a DiscUtils path)");
                        return 1;
                    }
                    using (var source = OpenSource(volume, log, useUnbuffered))
                    {
                        var excl = ResolveImageExclusions(cfg, source, volume, cliExcludes, log);
                        if (excl != null)
                        {
                            log.Error("--parent cannot combine with exclusions (baked into the full parent at creation; got: " + string.Join(", ", Store.MergeExclusionNames(cfg.Exclude, cliExcludes)) + ")");
                            return 1;
                        }
                        var chunkBytes = cfg.ChunkMib * 1024 * 1024;
                        var dirty = DifferencingVhdx.ComputeDirtyVsParent(source, parentArg, chunkBytes, log);
                        var child = DifferencingVhdx.CreateDiff(source, volume, toVhdx, parentArg, dirty, chunkBytes, log);
                        if (zstd)
                            SingleFileBackup.CompressSidecar(child, log, cfg.Compression.Level);
                    }
                    return 0;
                }
                using (var source = OpenSource(volume, log, useUnbuffered))
                {
                    var excl = ResolveImageExclusions(cfg, source, volume, cliExcludes, log);
                    var path = SingleFileBackup.BackupVhdx(source, volume, toVhdx, kind, excl, log);
                    // Order: verify (inside BackupVhdx) → compact → zstd.
                    // The sidecar hashes and deletes the original, so
                    // compacting after it compacts nothing.
                    if (optimizeVhd)
                        OptimizeVhd.Compact(path, new PowershellOptimizeRunner(), log);
                    if (zstd)
                        SingleFileBackup.CompressSidecar(path, log, cfg.Compression.Level);
                }
                return 0;
            }
            // WIM/ESD capture from the VSS snapshot dir — a live volume, not
            // a file. Trailing backslash: the shadow device object is only a
            // valid directory path in root form.
            using (var source = OpenSource(volume, log))
            {
                var snap = (source as IVolumeHandleProvider)?.SnapshotDevicePath;
                if (snap == null)
                {
                    log.Error("--to-wim/--to-esd need --volume <drive:> (a live volume, not a file: " + volume + ")");
                    return 1;
                }
                var snapDir = snap.EndsWith('\\') ? snap : snap + "\\";
                var merged = Store.MergeExclusionNames(cfg.Exclude, cliExcludes);
                var dism = new DismRunner();
                if (toWim != null)
                {
                    var wimPath = SingleFileBackup.BackupWim(snapDir, volume, toWim, merged, dism, log);
                    if (splitMb > 0)
                        SplitWimFile(wimPath, splitMb, log);
                }
                else
                    SingleFileBackup.BackupEsd(snapDir, volume, toEsd, merged, dism, log);
            }
            return 0;
        }

        // SWM target: beside the WIM, same stem (<name>.swm, siblings
        // <name>2.swm...). Backend follows the OS: inbox DISM on Windows,
        // wimlib where it exists (Linux), loud when neither is present.
        private static void SplitWimFile(string wimPath, int sizeMb, ILogger log)
        {
            IWimRunner runner = OperatingSystem.IsWindows()
                ? new DismWimRunner(new DismRunner())
                : new WimlibRunner();
            var swm = Path.ChangeExtension(wimPath, "swm");
            WimBackend.SplitSwm(wimPath, swm, sizeMb, runner, log);
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
        // WIM file ops run through the backend seam: inbox DISM on
        // Windows, wimlib shell-out on Linux (same string protocol).
        private static IWimRunner CreateWimRunner() =>
            OperatingSystem.IsWindows()
                ? new DismWimRunner(new DismRunner())
                : new WimlibRunner();

        private static bool IsBare(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith("--", StringComparison.Ordinal);

        // §12.19 convert + single-index export: both directions are the
        // same op (source-agnostic export). --index optional on
        // single-image sources (defaults 1), required on multi-image.
        private static int RunConvert(string? wimPath, string? dest, string? indexFlag, string? compressFlag, ILogger log)
        {
            if (string.IsNullOrEmpty(wimPath) || IsBare(wimPath) || string.IsNullOrEmpty(dest) || IsBare(dest))
            {
                log.Error("--convert requires --wim <source> --to <dest> (bare flag refuses)");
                return 1;
            }
            int? index = null;
            if (!string.IsNullOrEmpty(indexFlag))
            {
                if (!int.TryParse(indexFlag, out var n) || n < 1)
                {
                    log.Error("invalid --index (want a positive image number, got: " + indexFlag + ")");
                    return 1;
                }
                index = n;
            }
            var compress = WimCompression.Max;
            if (!string.IsNullOrEmpty(compressFlag))
            {
                if (compressFlag.Equals("max", StringComparison.OrdinalIgnoreCase)) compress = WimCompression.Max;
                else if (compressFlag.Equals("recovery", StringComparison.OrdinalIgnoreCase)) compress = WimCompression.Recovery;
                else
                {
                    log.Error("invalid --compress (want max|recovery, got: " + compressFlag + ")");
                    return 1;
                }
            }
            else if (dest.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
            {
                compress = WimCompression.Recovery;
                log.Info("--convert to .esd implies --compress recovery (ESD is always solid)");
            }
            WimImageOps.Convert(wimPath, dest, index, compress, CreateWimRunner(), log);
            return 0;
        }

        // §12.28 wimlib optimize: dedupe (+ optional recompress) in place.
        // No DISM equivalent exists — the Windows runner refuses naming
        // wimlib as the path. Sizes + reclaimed bytes logged by the op.
        private static int RunOptimizeWim(string? wimPath, bool recompress, ILogger log)
        {
            if (string.IsNullOrEmpty(wimPath) || IsBare(wimPath))
            {
                log.Error("--optimize-wim requires --wim <file> [--recompress] (bare flag refuses)");
                return 1;
            }
            WimOptimize.Optimize(wimPath, recompress, CreateWimRunner(), log);
            return 0;
        }

        // §12.20 SWM merge: reunite a split set (join needs ALL parts).
        private static int RunMergeSwm(string? swmPath, string? dest, ILogger log)
        {
            if (string.IsNullOrEmpty(swmPath) || IsBare(swmPath) || string.IsNullOrEmpty(dest) || IsBare(dest))
            {
                log.Error("--merge-swm requires --swm <first.swm> --to <dest.wim> (bare flag refuses)");
                return 1;
            }
            WimImageOps.MergeSwm(swmPath, dest, CreateWimRunner(), log);
            return 0;
        }

        // §12.21 image dossier: indexes, sizes, languages (+ recorded
        // compact state when a sidecar path is derivable — not here: pass
        // null, the report owns the file read when given).
        private static int RunWimInfo(string? wimPath, ILogger log)
        {
            if (string.IsNullOrEmpty(wimPath) || IsBare(wimPath))
            {
                log.Error("--wim-info requires --wim <image> (bare flag refuses)");
                return 1;
            }
            Console.WriteLine(WimInfoReport.Get(wimPath, CreateWimRunner(), log).ToString());
            return 0;
        }

        // §12.22 mount: read/write servicing mount + our marker (never
        // mount over a non-empty dir; marker reclaims only our mounts).
        private static int RunMount(string? wimPath, string? indexFlag, string? mountDir, ILogger log)
        {
            if (string.IsNullOrEmpty(wimPath) || IsBare(wimPath) || string.IsNullOrEmpty(mountDir) || IsBare(mountDir))
            {
                log.Error("--mount requires --wim <image> --index <N> --mount-dir <empty dir> (bare flag refuses)");
                return 1;
            }
            if (string.IsNullOrEmpty(indexFlag) || !int.TryParse(indexFlag, out var index) || index < 1)
            {
                log.Error("--mount requires --index <N> (positive image number, got: " + indexFlag + ")");
                return 1;
            }
            WimMount.Mount(wimPath, index, mountDir, CreateWimRunner(), log);
            return 0;
        }

        // §12.22 unmount: exactly one of --commit/--discard (no silent
        // default — committing or throwing away servicing must be explicit).
        private static int RunUnmount(string? mountDir, bool commit, bool discard, ILogger log)
        {
            if (string.IsNullOrEmpty(mountDir) || IsBare(mountDir))
            {
                log.Error("--unmount requires --mount-dir <dir> (bare flag refuses)");
                return 1;
            }
            if (commit == discard)
            {
                log.Error("--unmount requires exactly one of --commit | --discard");
                return 1;
            }
            WimMount.Unmount(mountDir, commit, CreateWimRunner(), log);
            return 0;
        }

        // Install-without-USB flow: apply a WIM/ESD straight to a partition
        // + bcdboot, from the live OS or WinPE — no boot.wim needed. Index
        // rule mirrors --convert (single→default, multi→require). Refuses
        // the live-OS volume; /CheckIntegrity covers the WIM, not the write.
        private static int RunApply(string? wimPath, string? indexFlag, string? targetDir, ILogger log)
        {
            if (string.IsNullOrEmpty(wimPath) || IsBare(wimPath) || string.IsNullOrEmpty(targetDir) || IsBare(targetDir))
            {
                log.Error("--apply requires --wim <image> --target <dir> (bare flag refuses)");
                return 1;
            }
            int? index = null;
            if (!string.IsNullOrEmpty(indexFlag))
            {
                if (!int.TryParse(indexFlag, out var n) || n < 1)
                {
                    log.Error("invalid --index (want a positive image number, got: " + indexFlag + ")");
                    return 1;
                }
                index = n;
            }
            WimApply.Apply(wimPath, index, targetDir, CreateWimRunner(), log);
            return 0;
        }

        private static int RunPrepareUniversal(string? windowsDir, List<string> drivers, string? nicDir, string? gpuDir, bool dryRun, bool trimResetBase, List<string> trimFeatures, List<string> trimPackages, bool trimNoHibernate, string? unattendFile, List<string> langPacks, string? regHive, string? regKey, string? regName, string? regType, string? regData, List<string> removeKbs, ILogger log)
        {
            if (string.IsNullOrEmpty(windowsDir) || !Directory.Exists(windowsDir))
            {
                log.Error("--prepare-universal requires --windows-dir <offline Windows dir> (got: " + windowsDir + ")");
                return 1;
            }
            // Registry edit needs all five parts or none (partial = refuse,
            // never guess the missing piece).
            var regParts = new[] { regHive, regKey, regName, regType, regData };
            if (regParts.Any(p => p != null) && regParts.Any(string.IsNullOrEmpty))
            {
                log.Error("--reg-hive/--reg-key/--reg-name/--reg-type/--reg-data are all required together (partial edit refuses)");
                return 1;
            }
            var rc = UniversalRestore.InjectStorageDrivers(new UniversalRestoreOptions(drivers.ToArray(), windowsDir, dryRun), new WindowsDismDriverInjector(log), log);
            if (rc != 0) return rc;
            rc = UniversalRestore.StageFirstBootDrivers(new FirstBootOptions(nicDir, gpuDir, windowsDir, dryRun), new FileFirstBootStager(), log);
            if (rc != 0) return rc;
            // Payload adds first (drivers above, then language packs), then
            // the answer file that may reference them.
            if (langPacks.Count > 0)
            {
                rc = OfflineServicing.AddLangPack(windowsDir, langPacks, new WindowsTrimRunner(), log, dryRun);
                if (rc != 0) return rc;
            }
            if (!string.IsNullOrEmpty(unattendFile))
            {
                rc = OfflineServicing.ApplyUnattend(windowsDir, unattendFile, new WindowsTrimRunner(), log, dryRun);
                if (rc != 0) return rc;
            }
            // §12.14 footprint trim (REMOVAL — each op deletes something;
            // ResetBase is irreversible, stated again in flag help). Runs
            // after servicing so the base reset covers the injected drivers.
            // powercfg never runs live (that would hit the tech box): the
            // off switch is staged into SetupComplete.cmd for first boot.
            if (trimResetBase || trimFeatures.Count > 0 || trimPackages.Count > 0)
            {
                rc = OfflineTrim.Trim(new OfflineTrimOptions(windowsDir, trimResetBase, trimFeatures.ToArray(), trimPackages.ToArray(), false, dryRun), new WindowsTrimRunner(), log);
                if (rc != 0) return rc;
            }
            // KB removal after servicing (never the live OS — the guard
            // inside refuses %SystemRoot%).
            if (removeKbs.Count > 0)
            {
                rc = OfflineKb.RemoveKb(windowsDir, removeKbs, new WindowsTrimRunner(), log, dryRun);
                if (rc != 0) return rc;
            }
            // Registry tweaks last (config over content).
            if (regHive != null)
            {
                rc = OfflineRegistry.SetValue(regHive, regKey!, regName!, regType!, regData!, new WindowsRegistryRunner(), log, dryRun);
                if (rc != 0) return rc;
            }
            if (trimNoHibernate)
                return OfflineTrim.StageNoHibernate(windowsDir, dryRun, log);
            return 0;
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
        private static IBlockSource OpenSource(string volume, ILogger log, bool useUnbuffered = true) =>
            volume.Length == 2 && volume[1] == ':' && char.IsAsciiLetter(volume[0])
                ? new VssBlockSource(volume, log, useUnbuffered)
                : new FileBlockSource(volume, useUnbuffered);

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
            Console.WriteLine("      --clone-from <base.vhdx>  Reflink export: clone base (gen N-1 contents) + overwrite dirty ranges only (same-volume COW, else plain-copy WRN)");
            Console.WriteLine("  --image --volume <path|X:> --to-vhdx <file|dir> [--zstd] [--vhdx-writer <discutils|virtdisk>]  Single-file VHDX backup, no store (writer flag is temporary: A/B spike, loser deleted same day)");
            Console.WriteLine("      --skip-if-unchanged  Exit 0 with no new gen when the source matches the latest snapshot (volume+disk; --force-full wins; storeless --to-* refuses)");
            Console.WriteLine("      --buffered-io  Opt out of unbuffered (O_DIRECT/NO_BUFFERING) extent reads for weird drivers (bulk capture only)");
            Console.WriteLine("      --parent <base.vhdx>  Differencing child holding dirty-vs-parent chunks only (depth 1, parent immutable while the child lives; no --exclude)");
            Console.WriteLine("  --image --volume <X:> --to-wim <file|dir> | --to-esd <file|dir>  Single-file WIM/ESD via inbox DISM from the VSS snapshot (Windows, elevated)");
            Console.WriteLine("      --to-wim <base.wim> --append  New image index in an existing WIM (gen = count+1; ESD/solid refused)");
            Console.WriteLine("  --retry-lock <90|10m|2h>  Wait up to the duration for a contended store lock (writers: image/gc/optimize-to-fit; default fails loud naming the holder)");
            Console.WriteLine("  --optimize-to-fit [--bytes N]  Compact the chunk store in place (+ prune oldest gens to N bytes via GC)");
            Console.WriteLine("  --extract --snapshot <gen> --paths <a,b> --out <dir>  Pull files/folders out of a snapshot (no ADS/ACL/EFS — WRN names path+reason each)");
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
            Console.WriteLine("      [--trim-resetbase] [--trim-disable-feature <name>]... [--trim-remove-package <id>]... [--trim-no-hibernate]");
            Console.WriteLine("      Footprint trim = REMOVAL (ResetBase is irreversible; feature/package lists are caller-named, never builtin; hibernate stages to SetupComplete.cmd for first boot)");
            Console.WriteLine("      [--unattend <file>] [--lang-pack <cab>]... [--remove-kb <KBnnn>]... [--reg-hive <f> --reg-key <k> --reg-name <n> --reg-type <t> --reg-data <d>]");
            Console.WriteLine("      Servicing order: drivers, first-boot staging, lang packs, unattend, trim, KB removal, registry; KB guard refuses the live OS");
            Console.WriteLine("  --convert --wim <src> --to <dest> [--index N] [--compress max|recovery]  Convert WIM<->ESD or re-export single-index (.esd implies recovery; multi-image source requires --index)");
            Console.WriteLine("  --merge-swm --swm <first.swm> --to <dest.wim>  Reunite a split set (join needs ALL parts)");
            Console.WriteLine("  --wim-info --wim <image>  Human-readable dossier: indexes, sizes, languages");
            Console.WriteLine("  --mount --wim <image> --index <N> --mount-dir <empty dir>  Read/write servicing mount (our marker; never over non-empty dir)");
            Console.WriteLine("  --unmount --mount-dir <dir> --commit | --discard  Commit or throw away servicing (exactly one; foreign mounts refused)");
            Console.WriteLine("  --apply --wim <image> [--index N] --target <D:>  Apply image to partition + bcdboot, no WinPE boot needed (refuses the live-OS volume; /CheckIntegrity covers the WIM, not the disk write)");
            Console.WriteLine("  --image --volume <X:> --to-wim <file|dir> --split-swm <sizeMb>  Split the WIM into FAT32-sized .swm parts (DISM on Windows, wimlib on Linux; Corpus policy: no ESD split)");
            Console.WriteLine("  --optimize-wim --wim <file> [--recompress]  Dedupe (+ optional recompress) a WIM in place via wimlib optimize (no DISM equivalent; Windows refuses naming wimlib)");
            Console.WriteLine("  --image --volume <path|X:> --to-vhdx <file|dir> [--optimize-vhd]  Compact the VHDX post-verify via Optimize-VHD Mode Full (Windows Hyper-V module; refuses differencing children)");
            Console.WriteLine("  --record-compact --snapshot <gen> [--windows-dir <offline>]  Record Compact OS state beside the snapshot manifest (record-only; Windows queries live)");
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
