using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TicTack;

namespace Corpus
{
    // System imaging CLI (system-image-future.md). Verbs: image a block
    // source, export/verify/list snapshots, build WinPE recovery media,
    // restore a snapshot to a disk/file, list disks, validate config.
    static class Program
    {
        private sealed class CliArgs
        {
            public string CfgPath = "";
            public string? Verb, Volume, Name, SnapshotArg, OutPath, Device, ParentArg;
            public bool Incremental, Differential, VerifyBlocks, ForceFull, Chain, Initialize, AllowBootDisk, SkipVerify, DryRun, Confirm, Inline, RepairBoot, SkipFsCheck, ShrinkToFit, Stream;
            public string? AdkDir, IncludeStore, DbPath, StagingDir, UsbDrive, IsoPath, PayloadDir, Target, StoreFlag, IndexFlag;
            public string? DiskArg, LayoutArg, PartitionsArg, OnlyVolumesArg, FromFlag, ToFlag, ConfirmToken;
            public readonly List<string> Drivers = new();
            public readonly List<string> CliExcludes = new();
            public int ChainGen, NbdPort, BootGen;
            public string? WindowsDir, NicDir, GpuDir, Firmware, VhdxFlag, VmNameFlag, BootTimeoutFlag, BootMemoryFlag;
            public string? ToVhdx, ToWim, ToEsd, ToVmdk, ToOvf, ToOva, VhdxWriter, Controller, BudgetBytes, ExtractPaths, CloneFrom, RetryLockFlag, SplitSwm;
            public bool Zstd, Append, BufferedIo, SkipIfUnchanged, OptimizeVhd, Recompress, TrimResetBase, TrimNoHibernate, Commit, Discard, SkipRecovery;
            public readonly List<string> TrimFeatures = new();
            public readonly List<string> TrimPackages = new();
            public readonly List<string> LangPacks = new();
            public readonly List<string> RemoveKbs = new();
            public string? WimPath, MountDir, CompressFlag, SwmPath, UnattendFile, RegHive, RegKey, RegName, RegType, RegData, VolumesArg;
            public int? KeepDaily, KeepWeekly, KeepMonthly, KeepYearly;
        }

        static int Main(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cli = new CliArgs { CfgPath = Path.Combine(baseDir, "config_corpus.yaml") };
            var parseRc = ParseCliArgs(args, cli);
            if (parseRc.HasValue)
                return parseRc.Value;

            if (cli.Verb == null)
            {
                PrintHelp();
                return 1;
            }

            CorpusConfig? cfg;
            try
            {
                cfg = CorpusConfig.Load(cli.CfgPath);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                Console.Error.WriteLine("Invalid config " + cli.CfgPath + ": " + ex.Message);
                return 1;
            }
            var backupDir = CorpusConfig.ResolveLogBackupDir(cfg, cli.ToVhdx, cli.ToWim, cli.ToEsd, cfg != null ? CorpusConfig.StateRoot(cfg) : baseDir);
            var log = CreateLogger(cfg, baseDir, backupDir);
            if (!ApplyStartupGuards(cfg, cli, backupDir, log))
                return 1;
            try
            {
                var probeRc = DispatchConfigFreeVerb(cli.Verb, cli, baseDir, log);
                if (probeRc.HasValue)
                    return probeRc.Value;
                if (!ValidateStoreConfig(cfg, cli, log))
                    return 1;
                return DispatchStoreVerb(cli.Verb, cli, cfg!, baseDir, log);
            }
            catch (StoredSkippedException)
            {
                return 0;
            }
            catch (StoreLockException ex)
            {
                log.Error(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                log.Error("corpus " + cli.Verb + " failed", ex);
                return 1;
            }
            finally
            {
                (log as IDisposable)?.Dispose();
            }
        }

        private static int? ParseCliArgs(string[] args, CliArgs cli)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var flag = args[i].ToLowerInvariant();
                if (flag is "--help" or "-h" or "-?") { PrintHelp(); return 0; }
                if (flag is "--version" or "-v") { PrintVersion(); return 0; }
                if (TryParseVerbFlag(flag, cli)) continue;
                if (TryParseBoolOrSpecialFlag(flag, args, ref i, cli)) continue;
                if (TryParseValueFlag(flag, args, ref i, cli)) continue;
                if (TryParseServicingFlag(flag, args, ref i, cli)) continue;
                Console.Error.WriteLine("Unknown argument: " + args[i]);
                PrintHelp();
                return 1;
            }
            return null;
        }

        private static bool TryParseVerbFlag(string flag, CliArgs cli)
        {
            var verb = flag switch
            {
                "--validate" => "validate", "--image" => "image", "--disk-layout" => "disk-layout",
                "--restore-disk" => "restore-disk", "--export" => "export", "--export-vhdx" => "export-vhdx",
                "--export-vmdk" => "export-vmdk", "--export-ovf" => "export-ovf", "--export-ova" => "export-ova",
                "--verify" => "verify", "--deep-verify" => "deep-verify", "--mount-snapshot" => "mount-snapshot",
                "--serve-nbd" => "serve-nbd",
                "--list-snapshots" => "list-snapshots", "--list-volumes" => "list-volumes", "--gc" => "gc",
                "--service" => "service", "--locate-winre" => "locate-winre", "--prepare-universal" => "prepare-universal",
                "--check-boot-mode" => "check-boot-mode", "--check-repair" => "check-repair", "--boot-verify" => "boot-verify",
                "--vss-list" => "vss-list", "--vss-snap" => "vss-snap", "--vss-read" => "vss-read",
                "--vss-dump" => "vss-dump", "--bitmap-stats" => "bitmap-stats", "--bitmap-dump" => "bitmap-dump",
                "--copy" => "copy", "--sync" => "sync", "--build-media" => "build-media",
                "--restore" => "restore", "--list-disks" => "list-disks", "--optimize-to-fit" => "optimize-to-fit",
                "--extract" => "extract", "--optimize-wim" => "optimize-wim", "--record-compact" => "record-compact",
                "--convert" => "convert", "--merge-swm" => "merge-swm", "--wim-info" => "wim-info",
                "--mount" => "mount", "--unmount" => "unmount", "--apply" => "apply",
                _ => null,
            };
            if (verb == null) return false;
            cli.Verb = verb;
            return true;
        }

        private static readonly Dictionary<string, Action<CliArgs>> BoolFlags = new(StringComparer.Ordinal)
        {
            ["--repair-boot"] = c => c.RepairBoot = true,
            ["--confirm"] = c => c.Confirm = true,
            ["--inline"] = c => c.Inline = true,
            ["--incremental"] = c => c.Incremental = true,
            ["--differential"] = c => c.Differential = true,
            ["--verify-blocks"] = c => c.VerifyBlocks = true,
            ["--full"] = c => c.ForceFull = true,
            ["--initialize"] = c => c.Initialize = true,
            ["--allow-boot-disk"] = c => c.AllowBootDisk = true,
            ["--skip-verify"] = c => c.SkipVerify = true,
            ["--zstd"] = c => c.Zstd = true,
            ["--append"] = c => c.Append = true,
            ["--buffered-io"] = c => c.BufferedIo = true,
            ["--skip-if-unchanged"] = c => c.SkipIfUnchanged = true,
            ["--optimize-vhd"] = c => c.OptimizeVhd = true,
            ["--recompress"] = c => c.Recompress = true,
            ["--trim-resetbase"] = c => c.TrimResetBase = true,
            ["--trim-no-hibernate"] = c => c.TrimNoHibernate = true,
            ["--commit"] = c => c.Commit = true,
            ["--discard"] = c => c.Discard = true,
            ["--dry-run"] = c => c.DryRun = true,
            ["--skip-recovery"] = c => c.SkipRecovery = true,
            ["--skip-fs-check"] = c => c.SkipFsCheck = true,
            ["--shrink-to-fit"] = c => c.ShrinkToFit = true,
            ["--stream"] = c => c.Stream = true,
        };

        private static bool TryParseBoolOrSpecialFlag(string flag, string[] args, ref int i, CliArgs cli)
        {
            if (BoolFlags.TryGetValue(flag, out var act)) { act(cli); return true; }
            if (flag != "--chain") return false;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cg) && cg > 0)
            {
                cli.Chain = true;
                cli.ChainGen = cg;
                i++;
            }
            else
                cli.Chain = true;
            return true;
        }

        private static readonly Dictionary<string, Action<CliArgs, string>> ValueFlags = new(StringComparer.Ordinal)
        {
            ["--config"] = (c, v) => c.CfgPath = v,
            ["--device"] = (c, v) => c.Device = v,
            ["--volume"] = (c, v) => c.Volume = v,
            ["--volumes"] = (c, v) => c.VolumesArg = v,
            ["--name"] = (c, v) => c.Name = v,
            ["--snapshot"] = (c, v) => c.SnapshotArg = v,
            ["--out"] = (c, v) => c.OutPath = v,
            ["--parent"] = (c, v) => c.ParentArg = v,
            ["--from"] = (c, v) => c.FromFlag = v,
            ["--to"] = (c, v) => c.ToFlag = v,
            ["--exclude"] = (c, v) => c.CliExcludes.Add(v),
            ["--target"] = (c, v) => c.Target = v,
            ["--disk"] = (c, v) => c.DiskArg = v,
            ["--layout"] = (c, v) => c.LayoutArg = v,
            ["--partitions"] = (c, v) => c.PartitionsArg = v,
            ["--only-volumes"] = (c, v) => c.OnlyVolumesArg = v,
            ["--store"] = (c, v) => c.StoreFlag = v,
            ["--index"] = (c, v) => c.IndexFlag = v,
            ["--confirm-type"] = (c, v) => c.ConfirmToken = v,
            ["--to-vhdx"] = (c, v) => c.ToVhdx = v,
            ["--to-wim"] = (c, v) => c.ToWim = v,
            ["--to-esd"] = (c, v) => c.ToEsd = v,
            ["--to-vmdk"] = (c, v) => c.ToVmdk = v,
            ["--to-ovf"] = (c, v) => c.ToOvf = v,
            ["--to-ova"] = (c, v) => c.ToOva = v,
            ["--vhdx-writer"] = (c, v) => c.VhdxWriter = v,
            ["--controller"] = (c, v) => c.Controller = v,
            ["--port"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p)) c.NbdPort = p; },
            ["--generation"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var g)) c.BootGen = g; },
            ["--bytes"] = (c, v) => c.BudgetBytes = v,
            ["--paths"] = (c, v) => c.ExtractPaths = v,
            ["--clone-from"] = (c, v) => c.CloneFrom = v,
            ["--retry-lock"] = (c, v) => c.RetryLockFlag = v,
            ["--split-swm"] = (c, v) => c.SplitSwm = v,
            ["--keep-daily"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) c.KeepDaily = n; },
            ["--keep-weekly"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) c.KeepWeekly = n; },
            ["--keep-monthly"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) c.KeepMonthly = n; },
            ["--keep-yearly"] = (c, v) => { if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) c.KeepYearly = n; },
        };

        private static bool TryParseValueFlag(string flag, string[] args, ref int i, CliArgs cli)
        {
            if (!ValueFlags.TryGetValue(flag, out var act)) return false;
            if (i + 1 < args.Length) act(cli, args[++i]);
            return true;
        }

        private static readonly Dictionary<string, Action<CliArgs, string>> ServicingFlags = new(StringComparer.Ordinal)
        {
            ["--vhdx"] = (c, v) => c.VhdxFlag = v,
            ["--vm-name"] = (c, v) => c.VmNameFlag = v,
            ["--timeout-seconds"] = (c, v) => c.BootTimeoutFlag = v,
            ["--memory-mb"] = (c, v) => c.BootMemoryFlag = v,
            ["--windows-dir"] = (c, v) => c.WindowsDir = v,
            ["--nic-dir"] = (c, v) => c.NicDir = v,
            ["--gpu-dir"] = (c, v) => c.GpuDir = v,
            ["--firmware"] = (c, v) => c.Firmware = v,
            ["--adk"] = (c, v) => c.AdkDir = v,
            ["--include-store"] = (c, v) => c.IncludeStore = v,
            ["--db"] = (c, v) => c.DbPath = v,
            ["--driver"] = (c, v) => c.Drivers.Add(v),
            ["--staging-dir"] = (c, v) => c.StagingDir = v,
            ["--usb"] = (c, v) => c.UsbDrive = v,
            ["--iso"] = (c, v) => c.IsoPath = v,
            ["--payload"] = (c, v) => c.PayloadDir = v,
            ["--trim-disable-feature"] = (c, v) => c.TrimFeatures.Add(v),
            ["--trim-remove-package"] = (c, v) => c.TrimPackages.Add(v),
            ["--wim"] = (c, v) => c.WimPath = v,
            ["--mount-dir"] = (c, v) => c.MountDir = v,
            ["--compress"] = (c, v) => c.CompressFlag = v,
            ["--swm"] = (c, v) => c.SwmPath = v,
            ["--unattend"] = (c, v) => c.UnattendFile = v,
            ["--lang-pack"] = (c, v) => c.LangPacks.Add(v),
            ["--reg-hive"] = (c, v) => c.RegHive = v,
            ["--reg-key"] = (c, v) => c.RegKey = v,
            ["--reg-name"] = (c, v) => c.RegName = v,
            ["--reg-type"] = (c, v) => c.RegType = v,
            ["--reg-data"] = (c, v) => c.RegData = v,
            ["--remove-kb"] = (c, v) => c.RemoveKbs.Add(v),
        };

        private static bool TryParseServicingFlag(string flag, string[] args, ref int i, CliArgs cli)
        {
            if (!ServicingFlags.TryGetValue(flag, out var act)) return false;
            if (i + 1 < args.Length) act(cli, args[++i]);
            return true;
        }

        private static bool ApplyStartupGuards(CorpusConfig? cfg, CliArgs cli, string? backupDir, ILogger log)
        {
            ProcessHardening.Apply(log);
            if (!ConfigGuard.CheckConfigFile(cli.CfgPath, log, cfg?.StrictConfigPerms ?? false))
                return false;
            if (cfg != null)
            {
                if (cfg.EcoQos)
                    ProcessHardening.ApplyEcoQos(log);
                ChunkId.ConfigureOverride(cfg.KeyPath, cfg.KeyEnv);
                if (!ConfigGuard.CheckSecretFile(cfg.KeyPath, log, cfg.StrictConfigPerms))
                    return false;
                if (cfg.DestinationProtect && !string.IsNullOrEmpty(cfg.StorePath))
                    DestinationGuard.Enforce(cfg.StorePath, log);
            }
            var backupTarget = CorpusConfig.LogBackupTargetDir(cfg, cli.ToVhdx, cli.ToWim, cli.ToEsd);
            if (!string.IsNullOrEmpty(backupTarget) && !string.Equals(backupDir, backupTarget, StringComparison.OrdinalIgnoreCase))
                log.Warn("Log backup dir " + backupTarget + " not writable; using " + backupDir + " instead.");
            return true;
        }

        private static int? DispatchConfigFreeVerb(string verb, CliArgs cli, string baseDir, ILogger log)
        {
            switch (verb)
            {
                case "vss-list": return VssList(baseDir);
                case "list-disks": return RunListDisks(log);
                case "list-volumes": return RunListVolumes(log);
                case "disk-layout": return RunDiskLayout(cli.DiskArg, log);
                case "vss-snap":
                case "vss-read":
                case "vss-dump":
                case "bitmap-stats":
                    var probeVol = ProbeVolume(cli.Volume, log);
                    return probeVol == null ? 1 : RunProbe(verb, probeVol, log);
                case "bitmap-dump": return RunBitmapDump(cli.Volume, cli.Device, cli.OutPath, log);
                case "locate-winre": return RunLocateWinre(log);
                case "prepare-universal": return RunPrepareUniversal(cli.WindowsDir, cli.Drivers, cli.NicDir, cli.GpuDir, cli.DryRun, cli.TrimResetBase, cli.TrimFeatures, cli.TrimPackages, cli.TrimNoHibernate, cli.UnattendFile, cli.LangPacks, cli.RegHive, cli.RegKey, cli.RegName, cli.RegType, cli.RegData, cli.RemoveKbs, log);
                case "convert": return RunConvert(cli.WimPath, cli.ToFlag, cli.IndexFlag, cli.CompressFlag, log);
                case "merge-swm": return RunMergeSwm(cli.SwmPath, cli.ToFlag, log);
                case "wim-info": return RunWimInfo(cli.WimPath, log);
                case "mount": return RunMount(cli.WimPath, cli.IndexFlag, cli.MountDir, log);
                case "unmount": return RunUnmount(cli.MountDir, cli.Commit, cli.Discard, log);
                case "apply": return RunApply(cli.WimPath, cli.IndexFlag, cli.Target, log);
                case "optimize-wim": return RunOptimizeWim(cli.WimPath, cli.Recompress, log);
                case "check-boot-mode": return RunCheckBootMode(cli.DiskArg, cli.Firmware, log);
                case "check-repair": return RunCheckRepair(cli.DiskArg, log);
                case "boot-verify": return RunBootVerify(cli.VhdxFlag, cli.VmNameFlag, cli.BootTimeoutFlag, cli.BootMemoryFlag, log);
                default: return null;
            }
        }

        private static bool ValidateStoreConfig(CorpusConfig? cfg, CliArgs cli, ILogger log)
        {
            if (cfg == null)
            {
                if (!File.Exists(cli.CfgPath))
                    log.Error("config not found: " + cli.CfgPath + " (copy config_corpus.yaml.example to config_corpus.yaml beside Corpus.exe, or pass --config <path>)");
                else
                    log.Error("config invalid (empty?): " + cli.CfgPath);
                return false;
            }
            if (!string.IsNullOrEmpty(cli.StoreFlag))
                cfg.StorePath = cli.StoreFlag;
            if (!CorpusConfig.Validate(cfg, log))
                return false;
            if (cli.Confirm && cli.Verb != "gc")
            {
                log.Error("--confirm is only valid with --gc");
                return false;
            }
            return true;
        }

        private static int DispatchStoreVerb(string verb, CliArgs cli, CorpusConfig cfg, string baseDir, ILogger log)
        {
            switch (verb)
            {
                case "validate":
                    log.Info("store: " + cfg.StorePath);
                    log.Info("state root: " + CorpusConfig.StateRoot(cfg));
                    VssHealth.ReportServiceStates(log);
                    log.Info("chunk: " + cfg.ChunkMib + " MiB, zstd level " + cfg.Compression.Level);
                    log.Info("exclusions: " + string.Join(", ", CapturePolicy.EffectiveSkip(cfg.Exclude)));
                    log.Info("Corpus config valid");
                    return 0;
                case "image":
                    return RunImageVerb(cfg, cli, log);
                case "export":
                case "export-vhdx":
                case "export-vmdk":
                case "export-ovf":
                case "export-ova":
                case "verify":
                case "deep-verify":
                case "mount-snapshot":
                case "list-snapshots":
                    return RunSnapshotStoreVerb(verb, cfg, cli, baseDir, log);
                case "build-media":
                    MediaBuilder.Build(new MediaBuildOptions(cli.AdkDir, cli.IncludeStore, cli.DbPath, cli.Drivers.ToArray(), cli.StagingDir, cli.UsbDrive, cli.IsoPath, cli.Initialize, cli.PayloadDir), log);
                    return 0;
                case "restore":
                    return RunRestore(cfg, baseDir, cli.SnapshotArg, cli.Target, cli.StoreFlag, cli.IndexFlag, cli.ConfirmToken, cli.AllowBootDisk, cli.SkipVerify, cli.DryRun, log);
                case "restore-disk":
                    return RunRestoreDisk(cfg, baseDir, cli.LayoutArg, cli.PartitionsArg, cli.Target, cli.StoreFlag, cli.IndexFlag, cli.ConfirmToken, cli.AllowBootDisk, cli.SkipVerify, cli.DryRun, cli.RepairBoot, log);
                case "gc":
                    using (StoreLock.Acquire(cfg.StorePath, cli.RetryLockFlag == null ? null : StoreLock.ParseRetryTimeout(cli.RetryLockFlag), log))
                        return RunGc(cfg, cli.DryRun, cli.Confirm, log, cli.KeepDaily, cli.KeepWeekly, cli.KeepMonthly, cli.KeepYearly);
                case "service":
                    return RunService(cfg, log);
                case "copy":
                case "sync":
                    return RunTransfer(cfg, verb, cli.FromFlag, cli.ToFlag, cli.SnapshotArg, cli.Chain, cli.ChainGen, log);
                case "optimize-to-fit":
                    return RunOptimizeToFit(cfg, cli, log);
                case "extract":
                    return RunExtractVerb(cfg, cli, log);
                case "record-compact":
                    return RunRecordCompact(cfg, cli, log);
                default:
                    return 1;
            }
        }

        private static int RunImageVerb(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            if (cli.ToVmdk != null || cli.ToOvf != null || cli.ToOva != null)
                return RunDirectOvfImage(cfg, cli, log);
            if (cli.ToVhdx != null || cli.ToWim != null || cli.ToEsd != null)
                return RunSingleFile(cfg, cli.Volume, cli.Name, cli.DiskArg, cli.ToVhdx, cli.ToWim, cli.ToEsd, cli.Zstd, cli.VhdxWriter, cli.Append, cli.Incremental, cli.ParentArg, cli.ForceFull, cli.Inline, cli.CliExcludes, log, !cli.BufferedIo, cli.SkipIfUnchanged, cli.SplitSwm, cli.OptimizeVhd);
            Directory.CreateDirectory(cfg.StorePath);
            using var imageStoreLock = StoreLock.Acquire(cfg.StorePath, cli.RetryLockFlag == null ? null : StoreLock.ParseRetryTimeout(cli.RetryLockFlag), log);
            if (!string.IsNullOrEmpty(cli.DiskArg))
                return RunImageDisk(cfg, cli.DiskArg, cli.Volume, cli.Name, cli.Incremental, cli.ParentArg, cli.ForceFull, cli.CliExcludes, log, !cli.BufferedIo, cli.SkipIfUnchanged, cli.Differential, cli.VerifyBlocks, cli.OnlyVolumesArg, cli.SkipRecovery);
            if (!string.IsNullOrEmpty(cli.VolumesArg))
                return RunConsistencyGroup(cfg, cli.VolumesArg, cli.Name ?? "cg", cli.CliExcludes, log, !cli.BufferedIo, cli.SkipIfUnchanged);
            if (string.IsNullOrEmpty(cli.Volume) || string.IsNullOrEmpty(cli.Name))
            {
                log.Error("--image requires --volume <path> --name <snapshot> (or --disk <N> --name <snapshot>)");
                return 1;
            }
            if (!cli.Inline)
            {
                if (cli.Incremental || cli.ParentArg != null || cli.ForceFull)
                {
                    log.Error("--incremental/--parent/--full need --inline (fast capture is always full)");
                    return 1;
                }
                using var store = CreateStore(cfg, log);
                using var source = OpenSource(cli.Volume, log, !cli.BufferedIo, cfg);
                var excl = ResolveImageExclusions(cfg, source, cli.Volume, cli.CliExcludes, log);
                var m = excl == null
                    ? store.ImageFast(source, cli.Name, skipIfUnchanged: cli.SkipIfUnchanged)
                    : store.ImageFast(source, cli.Name, new ImageOptions(Exclusions: excl), cli.SkipIfUnchanged);
                log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks (landing, optimize pending)");
                return 0;
            }
            if (cli.Incremental && cli.ParentArg != null)
            {
                log.Error("--incremental and --parent are mutually exclusive");
                return 1;
            }
            var parentGen = 0;
            if (cli.ParentArg != null && !TryParent(cli.ParentArg, log, out parentGen))
                return 1;
            using (var store = CreateStore(cfg, log))
            using (var source = OpenSource(cli.Volume, log, !cli.BufferedIo, cfg))
            {
                var m = store.Image(source, cli.Name, new ImageOptions(ParentGeneration: parentGen, Incremental: cli.Incremental, ForceFull: cli.ForceFull, ChangeSource: new MftDigestChangeSource(), Exclusions: ResolveImageExclusions(cfg, source, cli.Volume, cli.CliExcludes, log), Differential: cli.Differential, VerifyBlocks: cli.VerifyBlocks), cli.SkipIfUnchanged);
                log.Info($"snapshot {m.Generation:D6}-{m.Name}: {m.Chunks.Count} chunks");
            }
            return 0;
        }

        private static int RunSnapshotStoreVerb(string verb, CorpusConfig cfg, CliArgs cli, string baseDir, ILogger log)
        {
            switch (verb)
            {
                case "export":
                    if (!TrySnapshot(cli.SnapshotArg, log, out var exportGen) || string.IsNullOrEmpty(cli.OutPath))
                    {
                        log.Error("--export requires --snapshot <gen> --out <path>");
                        return 1;
                    }
                    using (var store = CreateStore(cfg, log))
                        store.Export(exportGen, cli.OutPath);
                    return 0;
                case "export-vhdx":
                    return RunExportVhdx(cfg, cli, log);
                case "export-vmdk":
                case "export-ovf":
                case "export-ova":
                    return RunOvfFamilyExport(verb, cfg, cli, log);
                case "verify":
                    if (!TrySnapshot(cli.SnapshotArg, log, out var verifyGen)) { log.Error("--verify requires --snapshot <gen>"); return 1; }
                    using (var store = CreateStore(cfg, log))
                        return store.Verify(verifyGen, cli.Chain) ? 0 : 1;
                case "deep-verify":
                    if (!TrySnapshot(cli.SnapshotArg, log, out var deepGen)) { log.Error("--deep-verify requires --snapshot <gen>"); return 1; }
                    return RunDeepVerify(cfg, baseDir, deepGen, log);
                case "mount-snapshot":
                    if (!TrySnapshot(cli.SnapshotArg, log, out var mountGen)) { log.Error("--mount-snapshot requires --snapshot <gen>"); return 1; }
                    if (cli.Stream)
                    {
                        using var dav = StreamWebDavMount.Start(cfg.StorePath, Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"), mountGen, log, cli.NbdPort);
                        Console.WriteLine("WebDAV mounted: " + dav.Url + " (Ctrl+C to unmount)");
                        using var wait = new ManualResetEventSlim(false);
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; wait.Set(); };
                        wait.Wait();
                        return 0;
                    }
                    return RunMountSnapshot(cfg, baseDir, mountGen, null, log);
                case "serve-nbd":
                    return RunServeNbd(cfg, cli, log);
                case "list-snapshots":
                    using (var store = CreateStore(cfg, log))
                    {
                        foreach (var (gen, snap) in store.ListSnapshots())
                            Console.WriteLine(gen + " " + snap);
                    }
                    return 0;
                default:
                    return 1;
            }
        }

        private static int RunOvfFamilyExport(string verb, CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            if (string.IsNullOrEmpty(cli.OutPath))
            {
                log.Error($"--{verb} requires (--snapshot <gen> | --layout <gen>) --out <path>");
                return 1;
            }
            using var store = CreateStore(cfg, log);
            if (!string.IsNullOrEmpty(cli.LayoutArg))
            {
                if (!int.TryParse(cli.LayoutArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layoutGen) || layoutGen <= 0)
                {
                    log.Error("invalid --layout (want a positive layout generation, got: " + cli.LayoutArg + ")");
                    return 1;
                }
                return RunOvfExportTarget(verb, cli.OutPath, cli.Zstd, cfg.Compression.Level, log,
                    () => store.ExportDiskVmdk(layoutGen, cli.OutPath, cli.SkipRecovery),
                    () => store.ExportDiskOvf(layoutGen, cli.OutPath, cli.SkipRecovery, cli.Controller ?? "ahci"),
                    () => store.ExportDiskOva(layoutGen, cli.OutPath, cli.SkipRecovery, cli.Controller ?? "ahci"));
            }
            if (!TrySnapshot(cli.SnapshotArg, log, out var gen))
            {
                log.Error($"--{verb} requires (--snapshot <gen> | --layout <gen>) --out <path>");
                return 1;
            }
            return RunOvfExportTarget(verb, cli.OutPath, cli.Zstd, cfg.Compression.Level, log,
                () => store.ExportVmdk(gen, cli.OutPath),
                () => store.ExportOvf(gen, cli.OutPath, cli.Controller ?? "ahci"),
                () => store.ExportOva(gen, cli.OutPath, cli.Controller ?? "ahci"));
        }

        // Direct storeless --image (--disk <N> | --volume <X:>) --to-vmdk|--to-ovf|--to-ova:
        // bypasses the chunk store completely and streams straight from VSS into VMDK/OVF/OVA.
        private static int RunDirectOvfImage(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            var kinds = (cli.ToVmdk != null ? 1 : 0) + (cli.ToOvf != null ? 1 : 0) + (cli.ToOva != null ? 1 : 0);
            if (kinds != 1) { log.Error("--to-vmdk/--to-ovf/--to-ova are mutually exclusive (exactly one)"); return 1; }
            var (format, outPath) = cli.ToVmdk != null ? ("vmdk", cli.ToVmdk)
                : cli.ToOvf != null ? ("ovf", cli.ToOvf) : ("ova", cli.ToOva!);
            if (outPath.StartsWith("--", StringComparison.Ordinal))
            {
                log.Error($"bare --to-{format} refuses (missing <file|dir>, got flag: {outPath})");
                return 1;
            }
            if (!string.IsNullOrEmpty(cli.DiskArg) && !string.IsNullOrEmpty(cli.Volume))
            {
                log.Error("--disk and --volume are mutually exclusive");
                return 1;
            }
            if (!string.IsNullOrEmpty(cli.DiskArg))
                return RunDirectDiskOvf(cfg, cli, format, outPath, log);
            if (string.IsNullOrEmpty(cli.Volume))
            {
                log.Error($"--image --to-{format} requires --disk <N> or --volume <path|X:>");
                return 1;
            }
            using var source = OpenSource(cli.Volume, log, !cli.BufferedIo, cfg);
            var excl = ResolveImageExclusions(cfg, source, cli.Volume, cli.CliExcludes, log);
            var vmName = Path.GetFileNameWithoutExtension(outPath);
            var res = format switch
            {
                "vmdk" => new OvfExportResult("", outPath, "", OvfExporter.AlignedCapacity(source.Length), 0, OvfExporter.ExportVmdk(outPath, source.Length, s => OvfExporter.StreamBlockSource(source, s, 0, excl)).FileBytes),
                "ovf" => OvfExporter.ExportOvf(outPath, vmName, source.Length, s => OvfExporter.StreamBlockSource(source, s, 0, excl)),
                _ => OvfExporter.ExportOva(outPath, vmName, source.Length, s => OvfExporter.StreamBlockSource(source, s, 0, excl)),
            };
            if (cli.Zstd)
                SingleFileBackup.CompressSidecar(format == "ovf" ? res.VmdkPath : outPath, log, cfg.Compression.Level);
            log.Info($"direct {format.ToUpperInvariant()} export of {cli.Volume} -> {outPath} complete");
            return 0;
        }

        private static int RunDirectDiskOvf(CorpusConfig cfg, CliArgs cli, string format, string outPath, ILogger log)
        {
            if (!int.TryParse(cli.DiskArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var diskNumber) || diskNumber < 0)
            {
                log.Error("invalid --disk (want a non-negative disk number, got: " + cli.DiskArg + ")");
                return 1;
            }
            HashSet<int>? includeParts = null;
            if (!string.IsNullOrEmpty(cli.PartitionsArg))
            {
                includeParts = new HashSet<int>();
                foreach (var tok in cli.PartitionsArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(tok.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
                    {
                        log.Error("invalid --partitions entry: " + tok);
                        return 1;
                    }
                    includeParts.Add(n);
                }
            }
            HashSet<string>? onlyVolumes = null;
            if (!string.IsNullOrEmpty(cli.OnlyVolumesArg))
            {
                onlyVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tok in cli.OnlyVolumesArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var norm = DiskCapture.NormalizeDrive(tok);
                    if (string.IsNullOrEmpty(norm))
                    {
                        log.Error("invalid --only-volumes entry: " + tok);
                        return 1;
                    }
                    onlyVolumes.Add(norm);
                }
            }
            var excludes = Store.MergeExclusionNames(cfg.Exclude, cli.CliExcludes);
            var plan = DiskCapture.BuildPlan(diskNumber, log, !cli.BufferedIo, cfg.StrictVssWriters, cli.SkipRecovery, includeParts, onlyVolumes);
            var vmName = Path.GetFileNameWithoutExtension(outPath);
            var res = OvfExporter.ExportDiskPlanDirect(format, outPath, vmName, plan, excludes, log);
            if (cli.Zstd)
                SingleFileBackup.CompressSidecar(format == "ovf" ? res.VmdkPath : outPath, log, cfg.Compression.Level);
            log.Info($"direct bootable {format.ToUpperInvariant()} (UEFI/GPT) of disk {diskNumber} -> {outPath} complete ({res.VmdkBytes} VMDK bytes, {res.PopulatedBytes} populated)");
            return 0;
        }

        private static int RunOvfExportTarget(
            string verb, string outPath, bool zstd, int zstdLevel, ILogger log,
            Action exportVmdk, Func<OvfExportResult> exportOvf, Action exportOva)
        {
            switch (verb)
            {
                case "export-vmdk":
                    exportVmdk();
                    if (zstd) SingleFileBackup.CompressSidecar(outPath, log, zstdLevel);
                    return 0;
                case "export-ovf":
                    var ovf = exportOvf();
                    if (zstd) SingleFileBackup.CompressSidecar(ovf.VmdkPath, log, zstdLevel);
                    return 0;
                case "export-ova":
                    exportOva();
                    if (zstd) SingleFileBackup.CompressSidecar(outPath, log, zstdLevel);
                    return 0;
                default:
                    return 1;
            }
        }

        private static int RunServeNbd(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            var dbPath = Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            NbdServer server;
            if (!string.IsNullOrEmpty(cli.LayoutArg) && TrySnapshot(cli.LayoutArg, log, out var layoutGen))
                server = NbdServer.StartForLayout(cfg.StorePath, dbPath, layoutGen, log, cli.NbdPort);
            else if (TrySnapshot(cli.SnapshotArg, log, out var snapGen))
                server = NbdServer.StartForSnapshot(cfg.StorePath, dbPath, snapGen, log, cli.NbdPort);
            else
            {
                log.Error("--serve-nbd requires --snapshot <gen> or --layout <gen>");
                return 1;
            }
            using (server)
            {
                server.Start();
                Console.WriteLine($"nbd://127.0.0.1:{server.Port} (Ctrl+C to stop)");
                using var wait = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; wait.Set(); };
                wait.Wait();
            }
            return 0;
        }

        private static int RunExportVhdx(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            if (!string.IsNullOrEmpty(cli.LayoutArg))
            {
                if (!TrySnapshot(cli.LayoutArg, log, out var layoutGen) || string.IsNullOrEmpty(cli.OutPath))
                {
                    log.Error("--export-vhdx --layout requires --layout <gen> --out <path>");
                    return 1;
                }
                using (var store = CreateStore(cfg, log))
                    store.ExportDiskVhdx(layoutGen, cli.OutPath);
                return 0;
            }
            if (!TrySnapshot(cli.SnapshotArg, log, out var vhdxGen) || string.IsNullOrEmpty(cli.OutPath))
            {
                log.Error("--export-vhdx requires --snapshot <gen> (or --layout <gen>) --out <path>");
                return 1;
            }
            if (cli.CloneFrom != null)
            {
                if (cli.CloneFrom.StartsWith("--", StringComparison.Ordinal))
                {
                    log.Error("bare --clone-from refuses (missing <base.vhdx>, got flag: " + cli.CloneFrom + ")");
                    return 1;
                }
                using (var store = CreateStore(cfg, log))
                    ReflinkExport.ExportVhdxFromClone(store, vhdxGen, cli.OutPath, cli.CloneFrom, log);
                return 0;
            }
            using (var store = CreateStore(cfg, log))
                store.ExportVhdx(vhdxGen, cli.OutPath);
            return 0;
        }

        private static int RunOptimizeToFit(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            long? budget = null;
            if (cli.BudgetBytes != null)
            {
                if (!long.TryParse(cli.BudgetBytes, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b) || b <= 0)
                {
                    log.Error("invalid --bytes (want a positive byte count, got: " + cli.BudgetBytes + ")");
                    return 1;
                }
                budget = b;
            }
            using var deltaCodec = new ZstdDeltaCodec(cfg.Compression.Level);
            var optimizer = new StoreOptimizer(cfg.StorePath, Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"), cfg.Retention ?? new CorpusRetentionConfig(), log, cfg.ChunkMib, cfg.Compression.Level, cfg.Encrypt, deltaCodec, cfg.EccOverheadPercent, cfg.WormMode);
            using (StoreLock.Acquire(cfg.StorePath, cli.RetryLockFlag == null ? null : StoreLock.ParseRetryTimeout(cli.RetryLockFlag), log))
                optimizer.Optimize(budget);
            return 0;
        }

        private static int RunExtractVerb(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            if (!TrySnapshot(cli.SnapshotArg, log, out var extractGen) || string.IsNullOrEmpty(cli.ExtractPaths) || string.IsNullOrEmpty(cli.OutPath))
            {
                log.Error("--extract requires --snapshot <gen> --paths <a,b> --out <dir>");
                return 1;
            }
            var index = EnsureWritableIndex(Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"), log);
            var summary = SelectiveExtract.Extract(cfg.StorePath, index, extractGen, cli.ExtractPaths.Split(','), cli.OutPath, log, new ZstdDeltaCodec(cfg.Compression.Level));
            log.Info($"extracted {summary.Files} files + {summary.Directories} dirs ({summary.Bytes} bytes, {summary.Skipped} skipped) from snapshot {extractGen}");
            return 0;
        }

        private static int RunRecordCompact(CorpusConfig cfg, CliArgs cli, ILogger log)
        {
            if (!TrySnapshot(cli.SnapshotArg, log, out var compactGen))
            {
                log.Error("--record-compact requires --snapshot <gen> [--windows-dir <offline>]");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                log.Error("--record-compact needs Windows (compact.exe query)");
                return 1;
            }
            var state = CompactQuery.Query(new CompactQuery.ProcessCompactRunner(), cli.WindowsDir);
            CompactStateRecord.Write(cfg.StorePath, compactGen, state, log);
            return 0;
        }

        private static ILogger CreateLogger(CorpusConfig? cfg, string baseDir, string? backupDir)
        {
            var logPath = cfg != null && cfg.Logging != null && !string.IsNullOrEmpty(cfg.Logging.Path)
                ? cfg.Logging.Path
                : Path.Combine(baseDir, "corpus.log");
            var dir = string.IsNullOrEmpty(backupDir) ? (cfg != null ? CorpusConfig.StateRoot(cfg) : baseDir) : backupDir;
            if (!string.IsNullOrEmpty(backupDir))
            {
                var probePath = Path.Combine(backupDir, ".corpus-write-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
                }
                catch
                {
                    dir = cfg != null ? CorpusConfig.StateRoot(cfg) : baseDir;
                }
            }
            // Shared factory (primitive params, no TicTack config model):
            // size-rotated primary + time-bounded .bak + optional console.
            return LoggerFactory.Create(
                cfg != null && cfg.Logging != null ? cfg.Logging.Level : null,
                logPath, 10, 5, null,
                console: cfg == null || cfg.Logging == null || cfg.Logging.Console,
                eventLog: OperatingSystem.IsWindows(),
                backupPath: Path.Combine(dir, "corpus.log.bak"),
                eventLogSource: "Corpus");
        }

        private static Store CreateStore(CorpusConfig cfg, ILogger log)
        {
            var store = new Store(
                cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                cfg.ChunkMib, cfg.Compression.Level, log,
                cfg.Encrypt, new ZstdDeltaCodec(cfg.Compression.Level),
                changeAlarmPercent: cfg.ChangeAlarmPercent,
                chunker: cfg.Chunker,
                eccOverheadPercent: cfg.EccOverheadPercent,
                decryptHook: cfg.Decrypt);
            store.WormMode = cfg.WormMode;
            store.Workers = cfg.Workers;
            store.MinFreeBytes = DiskSpaceGuard.GbToBytes(cfg.MinFreeGb);
            store.StrictFitCheck = cfg.StrictFitCheck;
            store.ProgressOpts = LiveProgressOptions.FromConfig(cfg.Logging);
            return store;
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
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var diskNumber) || diskNumber < 0)
            {
                log.Error("--disk-layout requires --disk <N> (non-negative disk number, got: " + diskArg + ")");
                return 1;
            }
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--disk-layout needs Windows");
                return 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(PartitionTable.ReadDisk(diskNumber, log), CorpusJsonContext.Default.DiskLayout));
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
            using var store = new Store(storePath, indexPath, cfg.ChunkMib, cfg.Compression.Level, log, cfg.Encrypt, new ZstdDeltaCodec(cfg.Compression.Level));
            store.ProgressOpts = LiveProgressOptions.FromConfig(cfg.Logging);
            var orch = new RestoreOrchestrator(store, log, RestoreOrchestrator.OpenTarget);
            orch.ProgressOpts = LiveProgressOptions.FromConfig(cfg.Logging);
            return orch.Restore(new RestoreOptions(gen, target, storePath, indexPath, confirmToken, allowBootDisk, skipVerify, dryRun));
        }

        // --image --disk: whole-disk capture via the store lane (one manifest
        // per non-skipped partition + sealed layout sidecar). Volume captures
        // are untouched: --volume with --disk is rejected, never merged.
        private static int RunImageDisk(CorpusConfig cfg, string? diskArg, string? volume, string? name,
            bool incremental, string? parentArg, bool forceFull, List<string> cliExcludes, ILogger log, bool useUnbuffered = true, bool skipIfUnchanged = false, bool differential = false, bool verifyBlocks = false, string? onlyVolumesArg = null, bool skipRecovery = false)
        {
            if (string.IsNullOrEmpty(diskArg) || string.IsNullOrEmpty(name))
            {
                log.Error("--image --disk requires --disk <N> --name <snapshot>");
                return 1;
            }
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
            if (!int.TryParse(diskArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var diskNumber) || diskNumber < 0)
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
            IReadOnlySet<string>? onlyVolumes = null;
            if (!string.IsNullOrWhiteSpace(onlyVolumesArg))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in onlyVolumesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    set.Add(v.TrimEnd('\\'));
                onlyVolumes = set;
            }
            DiskImager.CapturePlan = (d, l) => DiskCapture.BuildPlan(d, l, useUnbuffered, strictWriters: cfg.StrictVssWriters, skipRecovery: skipRecovery, onlyVolumes: onlyVolumes);
            using (var store = CreateStore(cfg, log))
            {
                var exclusions = Store.MergeExclusionNames(cfg.Exclude, cliExcludes);
                var layout = store.ImageDisk(diskNumber, name,
                    new DiskImageOptions(Incremental: incremental, ForceFull: forceFull, ParentLayoutGen: parentGen, Differential: differential, VerifyBlocks: verifyBlocks, Exclusions: exclusions), skipIfUnchanged);
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
            if (!int.TryParse(layoutArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layoutGen) || layoutGen <= 0)
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
                    if (!int.TryParse(piece.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
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
            using var store = new Store(storePath, indexPath, cfg.ChunkMib, cfg.Compression.Level, log, cfg.Encrypt, new ZstdDeltaCodec(cfg.Compression.Level));
            store.ProgressOpts = LiveProgressOptions.FromConfig(cfg.Logging);
            var orch = new RestoreOrchestrator(store, log, RestoreOrchestrator.OpenTarget);
            orch.ProgressOpts = LiveProgressOptions.FromConfig(cfg.Logging);
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
                    && (!int.TryParse(snapshotArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sg) || sg != chainGen))
                {
                    log.Error("--snapshot and --chain name different generations (pass one)");
                    return 1;
                }
                var gen = chainGen;
                if (gen <= 0 && (!int.TryParse(snapshotArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out gen) || gen <= 0))
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
            if (!ValidateSingleFileFlags(volume, name, diskArg, toVhdx, toWim, toEsd, zstd, vhdxWriter, append, incremental, parentArg, forceFull, inline, skipIfUnchanged, splitSwm, optimizeVhd, log, out var splitMb))
                return 1;
            if (append)
                return RunSingleFileAppendWim(cfg, volume!, toWim!, splitMb, cliExcludes, log);
            if (toVhdx != null)
                return RunSingleFileVhdx(cfg, volume!, toVhdx, zstd, vhdxWriter, parentArg, cliExcludes, useUnbuffered, optimizeVhd, log);
            return RunSingleFileWimOrEsd(cfg, volume!, toWim, toEsd, splitMb, cliExcludes, log);
        }

        private static bool ValidateSingleFileFlags(
            string? volume, string? name, string? diskArg, string? toVhdx, string? toWim, string? toEsd,
            bool zstd, string? vhdxWriter, bool append, bool incremental, string? parentArg, bool forceFull,
            bool inline, bool skipIfUnchanged, string? splitSwm, bool optimizeVhd, ILogger log, out int splitMb)
        {
            splitMb = 0;
            if (!ValidateSingleFileTarget(volume, name, diskArg, toVhdx, toWim, toEsd, incremental, forceFull, inline, skipIfUnchanged, log))
                return false;
            return ValidateSingleFileOptions(toVhdx, toWim, zstd, vhdxWriter, append, parentArg, splitSwm, optimizeVhd, log, out splitMb);
        }

        private static bool ValidateSingleFileTarget(
            string? volume, string? name, string? diskArg, string? toVhdx, string? toWim, string? toEsd,
            bool incremental, bool forceFull, bool inline, bool skipIfUnchanged, ILogger log)
        {
            var kinds = (toVhdx != null ? 1 : 0) + (toWim != null ? 1 : 0) + (toEsd != null ? 1 : 0);
            if (kinds != 1) { log.Error("--to-vhdx/--to-wim/--to-esd are mutually exclusive (exactly one)"); return false; }
            foreach (var t in new[] { toVhdx, toWim, toEsd })
            {
                if (t != null && t.StartsWith("--", StringComparison.Ordinal)) { log.Error("bare --to-vhdx/--to-wim/--to-esd refuses (missing <file|dir>, got flag: " + t + ")"); return false; }
            }
            if (string.IsNullOrEmpty(volume)) { log.Error("--image --to-vhdx/--to-wim/--to-esd requires --volume <path|X:>"); return false; }
            if (!string.IsNullOrEmpty(diskArg)) { log.Error("--to-vhdx/--to-wim/--to-esd cannot combine with --disk"); return false; }
            if (!string.IsNullOrEmpty(name)) { log.Error("--to-vhdx/--to-wim/--to-esd store no snapshot, --name is refused (drop it)"); return false; }
            if (incremental || forceFull || inline) { log.Error("--to-vhdx/--to-wim/--to-esd take no --inline/--incremental/--full (store concepts)"); return false; }
            if (skipIfUnchanged) { log.Error("--skip-if-unchanged needs a store capture (--image/--disk without --to-*)"); return false; }
            return true;
        }

        private static bool ValidateSingleFileOptions(
            string? toVhdx, string? toWim, bool zstd, string? vhdxWriter, bool append, string? parentArg,
            string? splitSwm, bool optimizeVhd, ILogger log, out int splitMb)
        {
            splitMb = 0;
            if (parentArg != null && toVhdx == null) { log.Error("--parent <base.vhdx> needs --to-vhdx (differencing child of a full VHDX)"); return false; }
            if (zstd && toVhdx == null) { log.Error("--zstd needs --to-vhdx (WIM/ESD are already compressed)"); return false; }
            if (vhdxWriter != null && toVhdx == null) { log.Error("--vhdx-writer needs --to-vhdx"); return false; }
            if (optimizeVhd && toVhdx == null) { log.Error("--optimize-vhd needs --to-vhdx (full VHDX post-step)"); return false; }
            if (optimizeVhd && parentArg != null) { log.Error("--optimize-vhd refuses a --parent differencing child (compact the full parent instead)"); return false; }
            if (optimizeVhd && !OperatingSystem.IsWindows()) { log.Error("--optimize-vhd needs Windows (Hyper-V Optimize-VHD)"); return false; }
            if (append && toWim == null) { log.Error("--append needs --to-wim <base.wim> (new image index in an existing WIM)"); return false; }
            if (splitSwm != null)
            {
                if (splitSwm.StartsWith("--", StringComparison.Ordinal)) { log.Error("bare --split-swm refuses (missing <sizeMb>, got flag: " + splitSwm + ")"); return false; }
                if (toWim == null) { log.Error("--split-swm needs --to-wim <base.wim> (Corpus policy: no ESD split; DISM itself splits ESD fine)"); return false; }
                if (!int.TryParse(splitSwm, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out splitMb) || splitMb <= 0)
                { log.Error("invalid --split-swm (want a positive part size in MB, got: " + splitSwm + ")"); return false; }
            }
            return true;
        }

        private static int RunSingleFileAppendWim(CorpusConfig cfg, string volume, string toWim, int splitMb, List<string> cliExcludes, ILogger log)
        {
            using var source = OpenSource(volume, log, cfg: cfg);
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
            return 0;
        }

        private static int RunSingleFileVhdx(CorpusConfig cfg, string volume, string toVhdx, bool zstd, string? vhdxWriter, string? parentArg, List<string> cliExcludes, bool useUnbuffered, bool optimizeVhd, ILogger log)
        {
            VhdxWriterKind kind;
            try { kind = ExtentSinkFactory.ParseKind(vhdxWriter); }
            catch (SingleFileBackupException ex) { log.Error(ex.Message); return 1; }
            if (parentArg != null)
            {
                if (parentArg.StartsWith("--", StringComparison.Ordinal)) { log.Error("bare --parent refuses (missing <base.vhdx>, got flag: " + parentArg + ")"); return 1; }
                if (kind != VhdxWriterKind.DiscUtils) { log.Error("--parent <base.vhdx> needs the DiscUtils writer (differencing create is a DiscUtils path)"); return 1; }
                using var source = OpenSource(volume, log, useUnbuffered, cfg);
                var explicitExcl = Store.ExplicitExclusionNames(cfg.Exclude, cliExcludes);
                if (explicitExcl.Count > 0) { log.Error("--parent cannot combine with exclusions (baked into the full parent at creation; got: " + string.Join(", ", explicitExcl) + ")"); return 1; }
                var chunkBytes = cfg.ChunkMib * 1024 * 1024;
                var dirty = DifferencingVhdx.ComputeDirtyVsParent(source, parentArg, chunkBytes, log);
                var child = DifferencingVhdx.CreateDiff(source, volume, toVhdx, parentArg, dirty, chunkBytes, log);
                if (zstd) SingleFileBackup.CompressSidecar(child, log, cfg.Compression.Level);
                return 0;
            }
            using (var source = OpenSource(volume, log, useUnbuffered, cfg))
            {
                var excl = ResolveImageExclusions(cfg, source, volume, cliExcludes, log);
                var path = SingleFileBackup.BackupVhdx(source, volume, toVhdx, kind, excl, log);
                if (optimizeVhd) OptimizeVhd.Compact(path, new PowershellOptimizeRunner(), log);
                if (zstd) SingleFileBackup.CompressSidecar(path, log, cfg.Compression.Level);
            }
            return 0;
        }

        private static int RunSingleFileWimOrEsd(CorpusConfig cfg, string volume, string? toWim, string? toEsd, int splitMb, List<string> cliExcludes, ILogger log)
        {
            using var source = OpenSource(volume, log, cfg: cfg);
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
        private static int RunDeepVerify(CorpusConfig cfg, string baseDir, int gen, ILogger log)
        {
            SnapshotManifest manifest;
            using (var store = CreateStore(cfg, log))
                manifest = store.GetManifest(gen);
            var db = Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            var summary = DeepVerifyJob.Run(cfg.StorePath, db, manifest, cfg.DeepVerifyPercent, log,
                CancellationToken.None, new ZstdDeltaCodec(cfg.Compression.Level));
            log.Info("deep-verify: " + summary.Checked + "/" + summary.TotalChunks + " checked, " + summary.Failed + " failed");
            return summary.Ok ? 0 : 1;
        }

        private static int RunMountSnapshot(CorpusConfig cfg, string baseDir, int gen, string? parentDir, ILogger log)
        {
            var db = Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db");
            using var mount = SnapshotMount.Mount(cfg.StorePath, db, gen, log,
                new SnapshotMountOptions(parentDir), CancellationToken.None);
            log.Info("snapshot " + gen + " mounted at " + mount.MountPath + " (Ctrl+C to unmount)");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); } catch (AggregateException) { }
            return 0;
        }

        private static int RunGc(CorpusConfig cfg, bool dryRun, bool confirm, ILogger log,
            int? keepDaily = null, int? keepWeekly = null, int? keepMonthly = null, int? keepYearly = null)
        {
            if (dryRun && confirm)
            {
                log.Error("--dry-run and --confirm are mutually exclusive");
                return 1;
            }
            var ret = cfg.Retention ?? new CorpusRetentionConfig();
            if (keepDaily.HasValue) ret.KeepDaily = keepDaily.Value;
            if (keepWeekly.HasValue) ret.KeepWeekly = keepWeekly.Value;
            if (keepMonthly.HasValue) ret.KeepMonthly = keepMonthly.Value;
            if (keepYearly.HasValue) ret.KeepYearly = keepYearly.Value;
            var gc = new GarbageCollector(
                cfg.StorePath,
                Path.Combine(CorpusConfig.StateRoot(cfg), "corpus-chunks.db"),
                ret, log,
                wormMode: cfg.WormMode);
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
                new GcSchedule(dailyAt, every), cfg.Encrypt, cfg.WormMode);
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
                if (!int.TryParse(indexFlag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
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
            if (string.IsNullOrEmpty(indexFlag) || !int.TryParse(indexFlag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 1)
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
                if (!int.TryParse(indexFlag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
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
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var diskNumber) || diskNumber < 0)
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
            if (string.IsNullOrEmpty(diskArg) || !int.TryParse(diskArg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var diskNumber) || diskNumber < 0)
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
        private static int RunBootVerify(string? vhdx, string? vmName, string? timeoutFlag, string? memoryFlag, ILogger log, int generation = 0, bool checkFs = true)
        {
            if (string.IsNullOrEmpty(vhdx))
            {
                log.Error("--boot-verify requires --vhdx <path> (export one first)");
                return 1;
            }
            var timeout = 300;
            if (!string.IsNullOrEmpty(timeoutFlag) && (!int.TryParse(timeoutFlag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out timeout) || timeout <= 0))
            {
                log.Error("--timeout-seconds must be a positive integer (got: " + timeoutFlag + ")");
                return 1;
            }
            var memoryMb = 2048;
            if (!string.IsNullOrEmpty(memoryFlag) && (!int.TryParse(memoryFlag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out memoryMb) || memoryMb <= 0))
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
                new BootVerifyOptions(vhdx!, vmName ?? "corpus-bootverify", timeout, memoryMb, generation, checkFs),
                new WindowsHyperVRunner(log, timeout, memoryMb), log);
        }

        // Drive-letter-colon ("T:") images the live volume through VSS;
        // anything else is a file path for FileBlockSource.
        private static IBlockSource OpenSource(string volume, ILogger log, bool useUnbuffered = true, CorpusConfig? cfg = null) =>
            volume.Length == 2 && volume[1] == ':' && char.IsAsciiLetter(volume[0])
                ? new VssBlockSource(
                    volume,
                    log,
                    useUnbuffered,
                    strictWriters: cfg?.StrictVssWriters ?? true,
                    preFreeze: string.IsNullOrWhiteSpace(cfg?.PreFreeze) ? null : () => RunFreezeHook(cfg!.PreFreeze, "pre_freeze", log, throwOnFail: true),
                    postThaw: string.IsNullOrWhiteSpace(cfg?.PostThaw) ? null : () => RunFreezeHook(cfg!.PostThaw, "post_thaw", log, throwOnFail: false))
                : new FileBlockSource(volume, useUnbuffered);

        private static void RunFreezeHook(string command, string phase, ILogger log, bool throwOnFail)
        {
            log.Info($"consistency hook ({phase}): {command}");
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + command)
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"" + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException($"{phase} hook failed to start");
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                var msg = $"{phase} hook exited with code {proc.ExitCode}";
                if (throwOnFail) throw new InvalidOperationException(msg);
                log.Warn(msg);
            }
        }

        private static int RunConsistencyGroup(
            CorpusConfig cfg, string volumesArg, string name,
            List<string> cliExcludes, ILogger log, bool useUnbuffered, bool skipIfUnchanged)
        {
            var volumes = volumesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (volumes.Length == 0)
            {
                log.Error("--volumes requires at least one drive (e.g. C:,D:)");
                return 1;
            }
            var groupId = Guid.NewGuid().ToString("N");
            var scopes = new List<BitLockerCaptureScope>(volumes.Length);
            try
            {
                foreach (var v in volumes)
                    scopes.Add(BitLockerCaptureScope.Arm(v, ManageBdeBitLocker.Instance, log));
                Action? pre = string.IsNullOrWhiteSpace(cfg.PreFreeze) ? null : () => RunFreezeHook(cfg.PreFreeze, "pre_freeze", log, throwOnFail: true);
                Action? post = string.IsNullOrWhiteSpace(cfg.PostThaw) ? null : () => RunFreezeHook(cfg.PostThaw, "post_thaw", log, throwOnFail: false);
                using var set = VssSnapshotSet.Create(volumes, log, null, cfg.StrictVssWriters, pre, post);
                using var lifetime = new DiskCaptureLifetime(set, scopes, volumes);
                var key = ChunkId.EnsureKey(cfg.StorePath);
                using var store = CreateStore(cfg, log);
                for (int i = 0; i < volumes.Length; i++)
                {
                    var vol = volumes[i];
                    using var src = new DiskSnapshotSource(lifetime, vol, set.DeviceFor(vol), scopes[i], useUnbuffered);
                    var excl = ResolveImageExclusions(cfg, src, vol, cliExcludes, log);
                    var snapName = $"{name}-{vol.TrimEnd(':', '\\')}";
                    var m = excl == null
                        ? store.ImageFast(src, snapName, skipIfUnchanged: skipIfUnchanged)
                        : store.ImageFast(src, snapName, new ImageOptions(Exclusions: excl), skipIfUnchanged);
                    m.ConsistencyGroupId = groupId;
                    ManifestStore.Save(Path.Combine(cfg.StorePath, "manifests"), m, key, preserveCreatedUtc: true);
                    log.Info($"consistency-group {groupId}: {m.Generation:D6}-{m.Name} ({m.Chunks.Count} chunks)");
                }
                set.Complete();
                return 0;
            }
            catch
            {
                foreach (var s in scopes) { try { s.Dispose(); } catch { } }
                throw;
            }
        }

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
            switch (verb)
            {
                case "vss-snap":
                    using (var s = VssSnapshot.Create(volume, log))
                        log.Info("device: " + s.DeviceObject);
                    return 0;
                case "vss-read": return ProbeVssRead(volume, log);
                case "vss-dump": return ProbeVssDump(volume, log);
                case "bitmap-stats": return ProbeBitmapStats(volume, log);
                default: return 1;
            }
        }

        private static int ProbeVssRead(string volume, ILogger log)
        {
            using var s = VssSnapshot.Create(volume, log);
            var dev = s.DeviceObject.TrimEnd('\\');
            using var raw = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var fs = new VssReadStream(raw, VssBlockSource.DeviceLength(volume, dev), () => { });
            var buf = new byte[64 * 1024];
            var filled = 0;
            int n;
            while (filled < buf.Length && (n = fs.Read(buf, filled, buf.Length - filled)) > 0)
                filled += n;
            using var sha = SHA256.Create();
            log.Info($"read {filled} bytes: sha256={Convert.ToHexString(sha.ComputeHash(buf, 0, filled))} head={Convert.ToHexString(buf, 0, Math.Min(8, filled))}");
            return 0;
        }

        private static int ProbeVssDump(string volume, ILogger log)
        {
            using var s = VssSnapshot.Create(volume, log);
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
            return 0;
        }

        private static int ProbeBitmapStats(string volume, ILogger log)
        {
            using var s = VssSnapshot.Create(volume, log);
            var bpc = VolumeBitmap.BytesPerCluster(volume + "\\");
            var total = VolumeBitmap.TotalBytes(volume + "\\");
            var runs = VolumeBitmap.ReadRuns(s.DeviceObject.TrimEnd('\\'), total / bpc);
            long used = 0;
            foreach (var r in runs) used += r.Count;
            log.Info($"{volume} {used}/{total / bpc} clusters used ({used * bpc}/{total} bytes) in {runs.Count} runs");
            for (var i = 0; i < Math.Min(5, runs.Count); i++)
                log.Info($"  run {i}: LCN {runs[i].Start} +{runs[i].Count}");
            return 0;
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
            IReadOnlyList<(long Start, long Count)> runs;
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
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out generation) || generation <= 0)
            {
                log.Error("invalid --snapshot (want a positive generation number, got: " + arg + ")");
                return false;
            }
            return true;
        }

        private static bool TryParent(string? arg, ILogger log, out int generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out generation) || generation <= 0)
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

        private static readonly string[] HelpLines =
        {
            "Corpus — system imaging",
            "",
            "  --image --volume <path|X:> --name <snap>   Fast landing capture (default, always full; run --gc/optimize to fold)",
            "      --inline [--incremental | --parent <gen>] [--full]   Classic packed capture instead of landing",
            "  --image --volumes <C:,D:> --name <snap>   Atomic multi-volume VSS consistency group capture",
            "  --image --disk <N> --name <snap> [--incremental | --parent <layoutGen>] [--full]  Image every non-skipped partition of PhysicalDriveN (elevated Windows)",
            "  --export --snapshot <gen> --out <path>  Export a snapshot to a file",
            "  --export-vhdx --snapshot <gen> --out <path>  Export a snapshot to dynamic VHDX",
            "  --export-vmdk (--snapshot <gen> | --layout <gen>) --out <file.vmdk> [--skip-recovery] [--zstd]  Export to streamOptimized VMDK",
            "  --export-ovf (--snapshot <gen> | --layout <gen>) --out <file.ovf|dir> [--skip-recovery] [--zstd]  Export to OVF + VMDK + SHA-256 .mf",
            "  --export-ova (--snapshot <gen> | --layout <gen>) --out <file.ova> [--skip-recovery] [--zstd]  Export to single-file OVA (UEFI/GPT with --layout)",
            "  --image (--disk <N> | --volume <X:>) --to-vmdk|--to-ovf|--to-ova <path> [--skip-recovery] [--only-volumes C:] [--partitions 1,3] [--exclude <p>]... [--zstd]  Direct storeless VMDK/OVF/OVA export (bypasses chunk store)",
            "      --clone-from <base.vhdx>  Reflink export: clone base (gen N-1 contents) + overwrite dirty ranges only (same-volume COW, else plain-copy WRN)",
            "  --image --volume <path|X:> --to-vhdx <file|dir> [--zstd] [--vhdx-writer <discutils|virtdisk>]  Single-file VHDX backup, no store (writer flag is temporary: A/B spike, loser deleted same day)",
            "      --skip-if-unchanged  Exit 0 with no new gen when the source matches the latest snapshot (volume+disk; --force-full wins; storeless --to-* refuses)",
            "      --buffered-io  Opt out of unbuffered (O_DIRECT/NO_BUFFERING) extent reads for weird drivers (bulk capture only)",
            "      --parent <base.vhdx>  Differencing child holding dirty-vs-parent chunks only (depth 1, parent immutable while the child lives; no --exclude)",
            "  --image --volume <X:> --to-wim <file|dir> | --to-esd <file|dir>  Single-file WIM/ESD via inbox DISM from the VSS snapshot (Windows, elevated)",
            "  Imaging requires: VSS + EventSystem services and volsnap driver running, swprv service enabled ('corpus --validate' reports them)",
            "      --to-wim <base.wim> --append  New image index in an existing WIM (gen = count+1; ESD/solid refused)",
            "  --retry-lock <90|10m|2h>  Wait up to the duration for a contended store lock (writers: image/gc/optimize-to-fit; default fails loud naming the holder)",
            "  --optimize-to-fit [--bytes N]  Compact the chunk store in place (+ prune oldest gens to N bytes via GC)",
            "  --extract --snapshot <gen> --paths <a,b> --out <dir>  Pull files/folders out of a snapshot (no ADS/ACL/EFS — WRN names path+reason each)",
            "  --vss-list                   Relay 'vssadmin list writers' (Windows)",
            "  --vss-snap --volume <X:>     Create+delete a VSS snapshot probe (Windows, elevated)",
            "  --vss-read --volume <X:>     Read a 64 KiB extent via snapshot probe (Windows, elevated)",
            "  --vss-dump --volume <X:>     Hash one snapshot twice (read-stability probe)",
            "  --bitmap-stats --volume <X:> Used-cluster stats over a snapshot (Windows, elevated)",
            "  --bitmap-dump --volume <X:> [--device <dev>] --out <file>  Dump allocated runs (Windows, elevated)",
            "  --verify --snapshot <gen> [--chain]              Verify a snapshot (exit 1 on failure; --chain also walks parent links to genesis, a gap fails loud even if the target alone verifies)",
            "  --deep-verify --snapshot <gen>                   Re-read deep_verify_percent of chunks from packs, hash-compare (exit 1 on mismatch; percent 0 = no-op)",
            "  --mount-snapshot --snapshot <gen>                Materialize a snapshot read-only under temp (Ctrl+C to unmount)",
            "  --list-snapshots                     List snapshots (\"gen name\" lines)",
            "  --gc [--dry-run | --confirm] [--keep-daily N] [--keep-weekly N] [--keep-monthly N] [--keep-yearly N]",
            "      Garbage-collect pruned snapshots (manual; --service runs this on schedule)",
            "      retention: keep_last/keep_within_days/keep_daily/keep_weekly/keep_monthly/keep_yearly/pin from config; exports outside the store are untouched",
            "      retention pool is shared between volume and disk snapshots (use two stores for isolation)",
            "  --service                              Run scheduled GC (gc_daily_at) + landing optimize (optimize_every) until Ctrl+C",
            "  --locate-winre                         Print winre.wim source+path (reagentc/known-path/partition-scan; exit 1 when absent)",
            "  --restore-disk ... --repair-boot       Recreate layout + reinstall bootloader after restore (default: print manual steps)",
            "  --prepare-universal --windows-dir <offline> --driver <dir>... [--nic-dir <d>] [--gpu-dir <d>] [--dry-run]",
            "      Offline storage-driver inject (DISM) + NIC/GPU first-boot staging into a restored tree",
            "      [--trim-resetbase] [--trim-disable-feature <name>]... [--trim-remove-package <id>]... [--trim-no-hibernate]",
            "      Footprint trim = REMOVAL (ResetBase is irreversible; feature/package lists are caller-named, never builtin; hibernate stages to SetupComplete.cmd for first boot)",
            "      [--unattend <file>] [--lang-pack <cab>]... [--remove-kb <KB|identity-fragment>]... [--reg-hive <f> --reg-key <k> --reg-name <n> --reg-type <t> --reg-data <d>]",
            "      Servicing order: drivers, first-boot staging, lang packs, unattend, trim, KB removal, registry; KB guard refuses the live OS",
            "  --convert --wim <src> --to <dest> [--index N] [--compress max|recovery]  Convert WIM<->ESD or re-export single-index (.esd implies recovery; multi-image source requires --index)",
            "  --merge-swm --swm <first.swm> --to <dest.wim>  Reunite a split set (join needs ALL parts)",
            "  --wim-info --wim <image>  Human-readable dossier: indexes, sizes, languages",
            "  --mount --wim <image> --index <N> --mount-dir <empty dir>  Read/write servicing mount (our marker; never over non-empty dir)",
            "  --unmount --mount-dir <dir> --commit | --discard  Commit or throw away servicing (exactly one; foreign mounts refused)",
            "  --apply --wim <image> [--index N] --target <D:>  Apply image to partition + bcdboot, no WinPE boot needed (refuses the live-OS volume; /CheckIntegrity covers the WIM, not the disk write)",
            "  --image --volume <X:> --to-wim <file|dir> --split-swm <sizeMb>  Split the WIM into FAT32-sized .swm parts (DISM on Windows, wimlib on Linux; Corpus policy: no ESD split)",
            "  --optimize-wim --wim <file> [--recompress]  Dedupe (+ optional recompress) a WIM in place via wimlib optimize (no DISM equivalent; Windows refuses naming wimlib)",
            "  --image --volume <path|X:> --to-vhdx <file|dir> [--optimize-vhd]  Compact the VHDX post-verify via Optimize-VHD Mode Full (Windows Hyper-V module; refuses differencing children)",
            "  --record-compact --snapshot <gen> [--windows-dir <offline>]  Record Compact OS state beside the snapshot manifest (record-only; Windows queries live)",
            "  --check-boot-mode --disk <N> --firmware <UEFI|BIOS>  Refuse GPT-on-BIOS / MBR-on-UEFI with the conversion path",
            "  --check-repair --disk <N>  Live-layout repair checks + diskpart/bcdboot plan (read-only, exit 1 names the blocker)",
            "  --boot-verify --vhdx <path> [--vm-name <n>] [--timeout-seconds <s>] [--memory-mb <mb>]  Boot a VHDX in Hyper-V, poll heartbeat to healthy (defaults 300s, 2048MB)",
            "  --build-media --staging-dir <dir> | --usb <drive> | --iso <file>  Build WinPE recovery media (exactly one sink)",
            "      [--adk <dir>] [--include-store <store>] [--db <db>] [--driver <infdir>]... [--initialize]",
            "      [--payload <dir>]  Skip re-publish (ADK box without the SDK: publish dir)",
            "  --list-disks                           List physical disks (Windows; empty on Linux)",
            "  --list-volumes                         List ready volumes (\"name filesystem bytes\" lines)",
            "  --disk-layout --disk <N>  Print DiskLayout JSON to stdout, capture nothing (Windows, elevated)",
            "  --restore-disk --layout <D> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]",
            "  --restore-disk --layout <D> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]",
            "      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run] [--partitions <1,3,4>]  Restore every non-skipped extent at its recorded offset",
            "      --partitions restores a subset (MSR auto-included, structural); target may be smaller than the source disk",
            "  --restore --snapshot <gen> --target <disk#|\\\\.\\PhysicalDriveN|file> [--store <p>] [--index <p>]",
            "      --confirm-type <TOKEN> [--allow-boot-disk] [--skip-verify] [--dry-run]  Restore a snapshot",
            "  --copy --from <store> --to <store> (--snapshot <gen> | --chain <gen>) [--chain]  Copy one snapshot, missing packs only (--chain includes ancestors)",
            "  --sync --from <store> --to <store>  Copy every snapshot the dest lacks, missing packs only (same id.key, or an empty dest)",
            "  --exclude <path|pattern>  (repeatable, volume captures only) merged over config exclude, recorded in the manifest ($LogFile never excludable)",
            "  --validate             Check config_corpus.yaml and exit",
            "  --config <path>        Config file (default: config_corpus.yaml beside the exe)",
            "  --version              Print version and exit",
            "  --help                 This text",
            "",
            "  incrementals reuse the parent's clean chunks without re-reading; any doubt → full re-image",
            "  per-pack encryption is an external shell hook (encrypt: in config), never builtin crypto",
            "  restore streams chunk-by-chunk, then re-reads the whole target to verify:",
            "    wall time is ~2x sequential device speed; --confirm-type is required always",
            "    (even --dry-run); a refused run names the want-TOKEN to re-run with",
        };

        static void PrintHelp()
        {
            foreach (var line in HelpLines)
                Console.WriteLine(line);
        }
    }
}
