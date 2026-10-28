using System.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    static class Program
    {
        // The resolve hook only runs under JIT (guarded below): in
        // framework-dependent deployments the two assemblies ship beside the
        // exe; under AOT single-file they are bundled and resolve normally.
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Hook body never executes under AOT (early return above); in JIT deployments the targets ship untrimmed beside the exe.")]
        static Program()
        {
            // .NET 10 SDK marks these as "type: platform" in deps.json
            // but the shared framework doesn't ship them yet. Use assembly-resolve
            // to load from app directory when the runtime can't find them.
            // Skipped under NativeAOT: single-file publish bundles them, so the
            // runtime resolves them without help (and dynamic load is trimmed).
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var n = name.Name;
                if (n == "System.ServiceProcess.ServiceController" ||
                    n == "System.Diagnostics.EventLog")
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, n + ".dll");
                    if (File.Exists(path))
                        return context.LoadFromAssemblyPath(path);
                }
                return null;
            };
        }

        static async Task<int> Main(string[] args)
        {
            ILogger? rootLogger = null;
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var o = new MainOptions { CfgPath = Path.Combine(baseDir, "config.yaml") };
                int? parsed = ParseMainArgs(o, args);
                if (parsed.HasValue)
                    return parsed.Value;
                TicTackConfig? cfg = LoadConfigOrDie(o.CfgPath);
                if (cfg == null)
                    return 1;
                // Pre-logger pass so the log/alert files themselves land on
                // resolved volumes; the post-logger EnsureResolved below
                // adds the wait-for-volume gate once logging exists.
                VolumeResolver.ResolveConfig(cfg);
                var (log, _) = OpenRootLogger(o, cfg);
                if (log == null)
                    return 1;
                rootLogger = log;
                if (!VolumeResolver.EnsureResolved(cfg, cfg.VolumeWaitMinutes, log))
                return 1;
                if (!Config.Validate(cfg, log))
                return 1;
                PowerGuard.Cleanup(cfg, log);
                int? maint = await RunMaintenanceModesAsync(o, args, cfg, log);
                if (maint.HasValue)
                    return maint.Value;
            return await RunSelectedModeAsync(o, cfg, log);
            }
            catch (Exception ex)
            {
                var crashLog = Path.Combine(Path.GetTempPath(), "TicTackSv-crash.log");
                try { File.WriteAllText(crashLog, DateTime.Now + " Main failed:\r\n" + ex); } catch { }
                LogCrash(ex);
                return 1;
            }
            finally
            {
                (rootLogger as IDisposable)?.Dispose();
            }
        }


        private static async Task<int> RunSelectedModeAsync(MainOptions o, TicTackConfig cfg, ILogger log)
        {
        if (o.isValidate)
        {
            log.Info("Configuration OK");
            return 0;
        }

        if (o.isOnce)
        {
            await RunOnceAsync(cfg, log);
            return 0;
        }

        if (o.isCli)
        {
            RunCli(cfg, log);
            return 0;
        }

        if (OperatingSystem.IsWindows() && (o.isService || !Environment.UserInteractive))
        {
            RunService(o.CfgPath);
            return 0;
        }

        log.Info("Usage: TicTackSv.exe --cli | --once | --rebuild | --validate | --external-drives | --forget [--dry-run] | --versions-list [glob] | --versions-pin <file@ts> | --versions-unpin <file@ts> | --deferred-list | --deferred-approve <source> | --deferred-check <source> | --reprove <source> | --os-capture --os-source <dir> --os-dest <dir> | --os-restore --os-source <dir> --os-dest <dir> | --service | --help | --version | --config <path>");
        return 0;
        }

        private sealed class MainOptions
        {
            public bool isService = false, isCli = false, isOnce = false, isValidate = false, isExternalDrives = false, isRebuild = false;
            public bool isDeferredList = false;
            public string? deferredVerb = null, deferredTarget = null;
            public bool isForget = false, forgetDryRun = false, versionsJson = false;
            public string? forgetKeepLast = null, forgetKeepWithin = null, forgetKeepDaily = null, forgetKeepWeekly = null, forgetKeepMonthly = null;
            public bool isVersionsList = false;
            public string? versionsGlob = null, versionsLatest = null;
            public string? pinVerb = null, pinTarget = null;
            public bool isOsCapture = false, isOsRestore = false;
            public string? osSource = null, osDest = null;
            public string CfgPath = "";
        }

        private static bool ApplyModeFlag(MainOptions o, string arg)
        {
            switch (arg)
            {
                case "--service": o.isService = true; return true;
                case "--cli": o.isCli = true; return true;
                case "--once": o.isOnce = true; return true;
                case "--validate": o.isValidate = true; return true;
                case "--external-drives": o.isExternalDrives = true; return true;
                case "--rebuild": o.isRebuild = true; return true;
                case "--forget": o.isForget = true; return true;
                case "--dry-run": o.forgetDryRun = true; return true;
                case "--json": o.versionsJson = true; return true;
                case "--deferred-list": o.isDeferredList = true; return true;
                case "--os-capture": o.isOsCapture = true; return true;
                case "--os-restore": o.isOsRestore = true; return true;
                default: return false;
            }
        }

        private static bool ParseValuedArg(MainOptions o, string[] args, ref int i)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--config":
                    if (i + 1 < args.Length) o.CfgPath = args[++i];
                    return true;
                case "--keep-last":
                    if (i + 1 < args.Length) o.forgetKeepLast = args[++i];
                    return true;
                case "--keep-within":
                    if (i + 1 < args.Length) o.forgetKeepWithin = args[++i];
                    return true;
                case "--keep-daily":
                    if (i + 1 < args.Length) o.forgetKeepDaily = args[++i];
                    return true;
                case "--keep-weekly":
                    if (i + 1 < args.Length) o.forgetKeepWeekly = args[++i];
                    return true;
                case "--keep-monthly":
                    if (i + 1 < args.Length) o.forgetKeepMonthly = args[++i];
                    return true;
                case "--versions-list":
                    o.isVersionsList = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        o.versionsGlob = args[++i];
                    return true;
                case "--latest":
                    if (i + 1 < args.Length) o.versionsLatest = args[++i];
                    return true;
                case "--versions-pin":
                case "--versions-unpin":
                case "--unpin":
                    return ParsePinArg(o, args, ref i);
                case "--os-source":
                    if (i + 1 < args.Length) o.osSource = args[++i];
                    return true;
                case "--os-dest":
                    if (i + 1 < args.Length) o.osDest = args[++i];
                    return true;
                case "--deferred-approve":
                case "--deferred-check":
                case "--reprove":
                case "--deferred-cancel":
                    o.deferredVerb = args[i].ToLowerInvariant();
                    if (i + 1 < args.Length) o.deferredTarget = args[++i];
                    return true;
                default: return false;
            }
        }

        private static int? ParseMainArgs(MainOptions o, string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].ToLowerInvariant();
                if (ApplyModeFlag(o, a)) continue;
                if (ParseValuedArg(o, args, ref i)) continue;
            switch (a)
            {
                case "--help":
                case "-h":
                case "-?":
                    PrintHelp();
                    return 0;
                case "--version":
                case "-v":
                    PrintVersion();
                    return 0;
                }
            }
            return null;
        }


        private static bool ParsePinArg(MainOptions o, string[] args, ref int i)
        {
            o.pinVerb = args[i].ToLowerInvariant();
            if (i + 1 < args.Length) o.pinTarget = args[++i];
            return true;
        }
        private static TicTackConfig? LoadConfigOrDie(string cfgPath) // sole caller passes non-null o.CfgPath
        {
            TicTackConfig? cfg;
        try
        {
            cfg = Config.Load(cfgPath);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // User error, not a crash: plain message on stderr, no stack
            // trace, no crash log. Scanner errors already carry their own
            // (Line/Col); escape errors get a hint because the classic is
            // a double-quoted Windows path ("C:\Users\..." — \U wants 8
            // hex digits, \x wants 2).
            var msg = "Invalid config " + cfgPath + ": " + ex.Message + ConfigErrorHint(ex);
            Console.Error.WriteLine(msg);
            if (OperatingSystem.IsWindows())
            {
                try { EventLog.WriteEntry("TicTackSv", msg, EventLogEntryType.Error); } catch { }
            }
            return null;
        }
        if (cfg == null)
        {
            Console.Error.WriteLine("config.yaml not found or invalid");
            return null;
        }
            return cfg;
        }

        private static (ILogger? log, string? logPath) OpenRootLogger(MainOptions o, TicTackConfig cfg)
        {
            string? logPath = null;
            logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack.log")
                : cfg.Logging.Path;
            var log = LoggerFactory.Create(
                cfg.Logging.Level,
                logPath,
                cfg.Logging.MaxSizeMb,
                cfg.Logging.MaxFiles,
                cfg.Logging.AlertPath,
                console: o.isCli || o.isOnce || o.isValidate || o.isRebuild || o.isDeferredList || o.deferredVerb != null || o.isForget || o.isVersionsList || o.pinVerb != null || o.isOsCapture || o.isOsRestore || cfg.Logging.Console,
                eventLog: OperatingSystem.IsWindows() && (o.isService || !Environment.UserInteractive),
                backupPath: Path.Combine(StateDbRoot(cfg), "tictack.log.bak"));
            return (log, logPath);
        }

        private static async Task<int?> RunMaintenanceModesAsync(MainOptions o, string[] args, TicTackConfig cfg, ILogger log)
        {
            if (o.isExternalDrives)
            {
                await RunExternalDrivesAsync(cfg, log);
                return 0;
            }

            if (o.isRebuild)
            {
                await RunRebuildAsync(cfg, log);
                return 0;
            }

            if (o.isDeferredList || o.deferredVerb != null)
            {
                return await RunDeferredAsync(cfg, log, o.isDeferredList, o.deferredVerb, o.deferredTarget);
            }

            if (o.isForget)
            {
                return RunForget(cfg, log, o.forgetDryRun, o.forgetKeepLast, o.forgetKeepWithin, o.forgetKeepDaily, o.forgetKeepWeekly, o.forgetKeepMonthly);
            }

            if (o.isVersionsList)
            {
                return RunVersionsList(cfg, log, o.versionsGlob, o.versionsLatest, o.versionsJson);
            }

            if (o.pinVerb != null)
            {
                return RunVersionsPin(cfg, log, o.pinTarget, pin: o.pinVerb == "--versions-pin");
            }

            if (o.isOsCapture || o.isOsRestore)
            {
                return await RunOsAsync(cfg, log, o.isOsRestore, o.osSource, o.osDest);
            }

            return null;
        }

        static async Task RunExternalDrivesAsync(TicTackConfig cfg, ILogger log)
        {
            if (cfg.ExternalDrives == null || string.IsNullOrEmpty(cfg.ExternalDrives.Command))
            {
                log.Error("No external_drives configuration found in config.yaml");
                return;
            }

            var cmd = cfg.ExternalDrives.Command;
            var sourcePaths = cfg.Sources != null && cfg.Sources.Count > 0
                ? string.Join(" ", cfg.Sources.Select(s => "\"" + s.Path + "\"").ToList())
                : null;

            var destinationDrives = CollectDestinationRoots(cfg);
            var drives = DriveDiscoverer.GetEligibleDrives(cfg.ExternalDrives, destinationDrives, log);

            if (drives.Count == 0)
            {
                log.Info("No eligible external drives found");
                return;
            }

            if (!ConfirmExternalDrives(cmd, drives, log)) return;

            var wd = cfg.ExternalDrives.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;

            foreach (var drive in drives)
            {
                await RunDriveBackupAsync(drive, cmd, sourcePaths, wd, log);
            }

            log.Info("All external drive backups complete");
        }

        internal static HashSet<string> CollectDestinationRoots(TicTackConfig cfg)
        {
                var destinationDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (cfg.Sources != null)
                {
                    foreach (var src in cfg.Sources)
                    {
                        if (string.IsNullOrEmpty(src.Destination)) continue;
                        var root = Path.GetPathRoot(src.Destination);
                        if (!string.IsNullOrEmpty(root))
                            destinationDrives.Add(root.TrimEnd('\\'));
                    }
                }
            return destinationDrives;
        }

        internal static bool ConfirmExternalDrives(string cmd, IReadOnlyList<string> drives, ILogger log)
        {
                Console.WriteLine("Eligible drives:");
                foreach (var drive in drives)
                {
                    var repoPath = cmd.Contains("{drive}", StringComparison.Ordinal)
                        ? cmd.Split(new[] { "-r " }, StringSplitOptions.None).LastOrDefault()?.Replace("{drive}", drive.TrimEnd('\\'))
                        : null;
                    Console.WriteLine("  " + drive + (repoPath != null ? " → " + repoPath : ""));
                }
                Console.WriteLine();
                Console.Write("Proceed? (y/n): ");
                var input = Console.ReadLine();
                if (string.IsNullOrEmpty(input) || !input.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                {
                    log.Info("External drive backups cancelled by user");
                    return false;
                }
            return true;
        }

        internal static async Task RunDriveBackupAsync(string drive, string cmd, string? sourcePaths, string wd, ILogger log)
        {
                    var fullCmd = cmd.Replace("{drive}", drive.TrimEnd('\\'));
                    if (sourcePaths != null)
                        fullCmd = fullCmd.Replace("{source}", sourcePaths);
    
                    log.Info("Running backup for " + drive + ": " + fullCmd);
    
                    try
                    {
                        // No redirection: the backup tool's output stays on the
                        // console; the runner owns the timeout and tree kill.
                        var (exitCode, _, _, timedOut) = await ProcessRunner.RunAsync(fullCmd, wd, redirect: false);
                        if (timedOut)
                            log.Error("Backup on " + drive + " timed out after 10 minutes, killed");
                        else if (exitCode != 0)
                            log.Error("Backup on " + drive + " exited " + exitCode);
                        else
                            log.Info("Backup on " + drive + " completed");
                    }
                    catch (Exception ex)
                    {
                        log.Error("Backup on " + drive + " failed", ex);
                    }
        }

        internal static async Task<bool> RunOnceAsync(TicTackConfig cfg, ILogger log)
        {
            log.Info("Once-off sync starting");
            var ran = false;
            foreach (var src in cfg.Sources)
            {
                if (!Directory.Exists(src.Path))
                {
                    log.Warn("Source folder missing, skipping once-off sync: " + src.Path);
                    continue;
                }
                if (!DriveGuard.IsReady(src.Destination))
                {
                    log.Error("Destination drive is not ready: " + src.Destination);
                    continue;
                }

                SyncPipeline? pipeline = null;
                try
                {
                    pipeline = BuildPipeline(src, cfg, log);
                    if (!await pipeline.RunOnceAsync())
                        log.Warn("Once-off sync skipped: " + src.Path);
                    else
                        ran = true;
                }
                catch (Exception ex)
                {
                    log.Error("Once-off sync failed: " + src.Path, ex);
                }
                finally
                {
                    pipeline?.Dispose();
                }
            }
            log.Info("Once-off sync complete");
            return ran;
        }

        // Item 11: file-level OS capture/restore, runnable end-to-end and
        // schedulable via jobs: (`command: TicTackSv --os-capture --os-source
        // C:\ --os-dest D:\oscap`). Mechanism is OsCapture (locked-tolerant
        // read, atomic write); the orchestrated bare-metal tail stays in
        // corpus item 5 (BootRepairOrchestrator).
        internal static async Task<int> RunOsAsync(TicTackConfig cfg, ILogger log, bool restore, string? source, string? dest)
        {
            var verb = restore ? "--os-restore" : "--os-capture";
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
            {
                log.Error(verb + " requires --os-source <dir> --os-dest <dir>");
                return 1;
            }
            if (!Directory.Exists(source))
            {
                log.Error(verb + ": source missing: " + source);
                return 1;
            }
            // Writing into a subdir of the enumerated tree would feed the
            // walk its own output (capture) or bury the target (restore).
            if (OsDestInsideSource(source, dest))
            {
                log.Error(verb + ": --os-dest must not be inside --os-source: " + dest);
                return 1;
            }
            if (restore)
            {
                var (restored, failed) = await OsCaptureRunner.RestoreTreeAsync(source, dest, log, CancellationToken.None);
                return failed > 0 ? 1 : 0;
            }
            var accessor = CreateOsAccessor(cfg, log);
            try
            {
                var (captured, failed) = await OsCaptureRunner.CaptureTreeAsync(accessor, source, dest, log, CancellationToken.None);
                return failed > 0 ? 1 : 0;
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }

        internal static bool OsDestInsideSource(string source, string dest)
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string srcFull, dstFull;
            try
            {
                srcFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                dstFull = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return false; // invalid paths fail later with their own message
            }
            return dstFull.Equals(srcFull, cmp)
                || dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, cmp)
                || dstFull.StartsWith(srcFull + Path.AltDirectorySeparatorChar, cmp);
        }

        // Same VSS-or-direct selection as BuildPipeline: VSS when any source
        // asks for it (Windows + available), direct otherwise.
        internal static IFileAccessor CreateOsAccessor(TicTackConfig cfg, ILogger log)
        {
            var wantVss = cfg.Sources != null && cfg.Sources.Any(s =>
                string.Equals(s.Sync != null ? s.Sync.FileAccess : null, "vss", StringComparison.OrdinalIgnoreCase));
            if (!wantVss)
                return new FileAccessor();
            if (!OperatingSystem.IsWindows())
            {
                log.Warn("sync.file_access 'vss' is Windows-only; os-capture using direct access.");
                return new FileAccessor();
            }
            try
            {
                var a = new VssFileAccessor(log);
                a.Probe();
                return a;
            }
            catch (Exception ex)
            {
                log.Warn("VSS unavailable (" + ex.Message + "); os-capture using direct access.");
                return new FileAccessor();
            }
        }

        internal static async Task RunRebuildAsync(TicTackConfig cfg, ILogger log)
        {
            log.Info("Full rebuild starting");
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack.log")
                : cfg.Logging.Path;
            var deferredDir = Path.GetDirectoryName(logPath) ?? baseDir;
            var failures = 0;


            foreach (var src in cfg.Sources)
            {
                var rebuildResult = await RebuildOneSourceAsync(src, cfg, log, deferredDir);
                if (rebuildResult < 0) return;
                failures += rebuildResult;
            }

            if (failures > 0)
                log.Warn("Full rebuild finished with " + failures + " failure(s).");
            else
                log.Info("Full rebuild finished. All databases cleared, source re-scanned, parity enforced.");
        }

        static async Task<int> RebuildOneSourceAsync(SourceConfig src, TicTackConfig cfg, ILogger log, string deferredDir)
        {
            if (!Directory.Exists(src.Path))
            {
                log.Error("Source folder missing, rebuild skipped: " + src.Path);
                return 1;
            }
            if (!DriveGuard.IsReady(src.Destination))
            {
                log.Error("Destination drive is not ready: " + src.Destination);
                return -1;
            }

            try
            {
                using var pipeline = BuildPipeline(src, cfg, log);
                // Holds clear through the pipeline's own store: it owns
                // the SQLite handle, and Windows refuses to delete open
                // files. A lock here means another process — the running
                // service — holds the store, so say so instead of
                // dumping a sharing-violation stack.
                try { pipeline.ClearDeferredHolds(); }
                catch (IOException ex) { throw new IOException("Rebuild blocked: stop the TicTackSv service first, then retry (" + ex.Message + ")", ex); }
                // Rebuild resets everything: state rows, every legacy
                // batch file and legacy name, then re-syncs from scratch.
                var deferredPrefix = DeferredFilePrefix(src);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(deferredDir, deferredPrefix + "-*.json"))
                        DeleteWithRetry(f);
                }
                catch (Exception ex) { log.Debug("Rebuild deferred cleanup skipped: " + ex.Message); }
                DeleteWithRetry(Path.Combine(deferredDir, LegacyDeferredFileName(src)));
                DeleteWithRetry(Path.Combine(src.Destination, ".tictack-deferred.json"));
                var rebuilt = await pipeline.RunOnceAsync(() =>
                {
                    pipeline.ResetState();
                    return Task.CompletedTask;
                });
                if (rebuilt)
                    log.Info("Rebuild complete: " + src.Path);
                else
                    log.Warn("Rebuild skipped: " + src.Path);
            }
            catch (Exception ex)
            {
                log.Error("Rebuild failed: " + src.Path, ex);
                return 1;
            }
                return 0;
        }


        internal static int DeferredHoldDays(SourceConfig src) =>
            src.Sync != null && src.Sync.DeleteHoldDays > 0 ? src.Sync.DeleteHoldDays : SyncConfig.DefaultDeleteHoldDays;

        internal static SourceConfig? ResolveDeferredSource(TicTackConfig cfg, string target, ILogger log)
        {
            foreach (var src in cfg.Sources)
                if (string.Equals(DeferredFilePrefix(src), target, StringComparison.OrdinalIgnoreCase))
                    return src;
            var leafHits = cfg.Sources.Where(s => string.Equals(SourceName(s), target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (leafHits.Count == 1) return leafHits[0];
            if (leafHits.Count > 1)
            {
                log.Error("Ambiguous source '" + target + "', use one of: "
                    + string.Join(", ", leafHits.Select(s => DeferredFilePrefix(s))));
                return null;
            }
            log.Error("Unknown deferred source '" + target + "', see --deferred-list");
            return null;
        }

        internal static string FormatAge(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return (int)span.TotalDays + "d";
            if (span.TotalHours >= 1) return (int)span.TotalHours + "h";
            return (int)span.TotalMinutes + "m";
        }

        // Next due across batches: earliest of per-batch expiry and daily
        // warning, labelled so the list shows what the timer waits for.
        internal static string FormatNextDue(IReadOnlyList<BatchSummary> batches, int holdDays, DateTime now)
        {
            string? best = null;
            var bestDue = DateTime.MaxValue;
            foreach (var b in batches)
            {
                var expiry = b.BlockedAt.AddDays(holdDays);
                var warnDue = b.LastWarningAt == DateTime.MinValue ? now : b.LastWarningAt.AddDays(1);
                var due = expiry < warnDue ? expiry : warnDue;
                var label = due == expiry ? "expiry" : "warning";
                if (due < bestDue) { bestDue = due; best = label; }
            }
            if (best == null) return "-";
            return bestDue <= now ? best + " now" : best + " in " + FormatAge(bestDue - now);
        }

        // Stop the service first for approve/cancel: the service owns the
        // store connection, so a second writer either blocks (Windows) or
        // races the drain (Linux). List and check are read-only.
        internal static async Task<int> RunDeferredAsync(TicTackConfig cfg, ILogger log, bool list, string? verb, string? target)
        {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack.log")
                        : cfg.Logging.Path;
                    var deferredDir = Path.GetDirectoryName(logPath) ?? baseDir;


            if (list) return ListDeferredHolds(cfg, log, deferredDir);

                    if (string.IsNullOrEmpty(target))
                    {
                        log.Error("Missing <source>: " + verb + " needs a source stem, see --deferred-list");
                        return 1;
                    }
                    var match = ResolveDeferredSource(cfg, target, log);
                    if (match == null) return 1;
                    var stem = DeferredFilePrefix(match);

            if (verb == "--deferred-check") return CheckDeferredHold(match, log, deferredDir, stem);
            if (verb == "--reprove" || verb == "--deferred-cancel") return CancelDeferredHold(match, log, deferredDir, stem);
            if (verb == "--deferred-approve") return await ApproveDeferredHoldAsync(match, cfg, log, stem);

                    log.Error("Unknown deferred verb '" + verb + "'");
                    return 1;
        }

        static int ListDeferredHolds(TicTackConfig cfg, ILogger log, string deferredDir)
        {
                        var any = false;
                        foreach (var src in cfg.Sources)
                        {
                            using var dd = new DeferredDeletion(deferredDir, DeferredFilePrefix(src), DeferredHoldDays(src), log);
                            var batches = dd.GetBatchSummaries();
                            if (batches.Count == 0) continue;
                            any = true;
                            var now = DateTime.UtcNow;
                            var oldest = batches.Min(b => b.BlockedAt);
                            log.Info(DeferredFilePrefix(src) + "  " + src.Path + "  batches=" + batches.Count
                                + " files=" + batches.Sum(b => b.Files)
                                + " oldest=" + FormatAge(now - oldest) + " ago"
                                + " next=" + FormatNextDue(batches, DeferredHoldDays(src), now));
                        }
                        if (!any) log.Info("No deferred holds.");
                        return 0;
        }

        static int CheckDeferredHold(SourceConfig match, ILogger log, string deferredDir, string stem)
        {
                        using var dd = new DeferredDeletion(deferredDir, stem, DeferredHoldDays(match), log);
                        if (!dd.HasPending) log.Info("No deferred holds for " + stem);
                        else dd.CheckWarnings();
                        return 0;
        }

        static int CancelDeferredHold(SourceConfig match, ILogger log, string deferredDir, string stem)
        {
                        using var dd = new DeferredDeletion(deferredDir, stem, DeferredHoldDays(match), log);
                        if (!dd.HasPending && !File.Exists(DeferredDbPath(deferredDir, match)))
                        {
                            log.Info("No deferred holds for " + stem);
                            return 0;
                        }
                        try { dd.ClearStore("reprove"); }
                        catch (IOException)
                        {
                            log.Error("Hold store is locked — stop the TicTackSv service first, then retry");
                            return 1;
                        }
                        log.Info("Hold cancelled for " + stem + ", destination files kept");
                        return 0;
        }

        static async Task<int> ApproveDeferredHoldAsync(SourceConfig match, TicTackConfig cfg, ILogger log, string stem)
        {
                        SyncPipeline? pipeline = null;
                        try
                        {
                            pipeline = BuildPipeline(match, cfg, log);
                            var (ran, proceeded, cancelled) = await pipeline.DrainExpiredAsync();
                            if (!ran)
                            {
                                log.Error("Exclusive lock unavailable — stop the TicTackSv service first, then retry");
                                return 1;
                            }
                            if (proceeded > 0) log.Warn("Deferred deletion: proceeding with " + proceeded + " files");
                            else if (cancelled > 0) log.Info("Deferred deletion: cancelled, files reappeared");
                            else log.Info("Nothing to approve for " + stem + ": no expired batches");
                            return 0;
                        }
                        catch (Exception ex)
                        {
                            log.Error("Deferred approve failed: " + match.Path, ex);
                            return 1;
                        }
                        finally
                        {
                            pipeline?.Dispose();
                        }
        }


        static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Console.WriteLine("TicTackSv " + (info ?? asm.GetName().Version?.ToString() ?? "unknown"));
        }

        static void PrintHelp()
        {
            Console.WriteLine("TicTackSv — real-time one-way file sync");
            Console.WriteLine();
            Console.WriteLine("Modes:");
            Console.WriteLine("  --cli                  Run all pipelines in the foreground (systemd runs this)");
            Console.WriteLine("  --service              Run as a Windows service");
            Console.WriteLine("  --once                 One sync pass over every source, then exit");
            Console.WriteLine("  --rebuild              Clear state and re-sync every source from scratch");
            Console.WriteLine("  --validate             Check config.yaml and exit");
            Console.WriteLine("  --external-drives      Back up to eligible external drives (interactive)");
            Console.WriteLine("  --config <path>        Use this config file (default: config.yaml next to the exe)");
            Console.WriteLine();
            Console.WriteLine("Retention (versions are per-file; forget IS prune):");
            Console.WriteLine("  --forget [--dry-run]   Apply each source's keep_* policies to its versions dir");
            Console.WriteLine("  --keep-last N --keep-within 30d --keep-daily N --keep-weekly N --keep-monthly N");
            Console.WriteLine("                           Override the config policies for one forget run");
            Console.WriteLine("  --versions-list [glob] [--latest N] [--json]");
            Console.WriteLine("                           List stored versions, newest first");
            Console.WriteLine("  --versions-pin <spec>  Never auto-delete these versions (spec: path or file@ts)");
            Console.WriteLine("  --versions-unpin <spec>");
            Console.WriteLine("                           Remove the pin again (--unpin is an alias)");
            Console.WriteLine();
            Console.WriteLine("Deferred deletions:");
            Console.WriteLine("  --deferred-list        Show held deletion batches per source");
            Console.WriteLine("  --deferred-approve <source>");
            Console.WriteLine("  --deferred-check <source>");
            Console.WriteLine("  --reprove <source>     Cancel the hold, keep destination files (--deferred-cancel)");
            Console.WriteLine();
            Console.WriteLine("OS capture (file-level; corpus item 5 owns the bare-metal tail):");
            Console.WriteLine("  --os-capture --os-source <dir> --os-dest <dir>");
            Console.WriteLine("                           Capture a live OS tree (locked-tolerant, atomic)");
            Console.WriteLine("  --os-restore --os-source <cap> --os-dest <dir>");
            Console.WriteLine("                           Restore a capture, then print the bootloader line");
            Console.WriteLine("                           (schedulable: jobs: [{command: TicTackSv --os-capture ...}])");
            Console.WriteLine();
            Console.WriteLine("Other:");
            Console.WriteLine("  --help, -h             This text");
            Console.WriteLine("  --version, -v          Print the version");
        }

        // forget: apply retention to every source's versions dir. Config
        // policies by default; any --keep-* flag switches the whole run to
        // flag-only policies (explicit beats configured, never merges).
        internal static int RunForget(TicTackConfig cfg, ILogger log, bool dryRun,
            string? keepLast, string? keepWithin, string? keepDaily, string? keepWeekly, string? keepMonthly)
        {
            var (overrides, orc) = ParseForgetOverrides(keepLast, keepWithin, keepDaily, keepWeekly, keepMonthly, log);
            if (orc != 0) return orc;
            var removedTotal = 0;
            var keptTotal = 0;
            foreach (var src in cfg.Sources)
            {
                var (r, kept) = ForgetSourceVersions(src, overrides, dryRun, log);
                removedTotal += r;
                keptTotal += kept;
            }
            log.Info(dryRun ? "forget --dry-run: change nothing" : "forget: removed " + removedTotal + ", kept " + keptTotal);
            return 0;
        }

        internal static (RetentionPolicy? Overrides, int Rc) ParseForgetOverrides(
            string? keepLast, string? keepWithin, string? keepDaily, string? keepWeekly, string? keepMonthly, ILogger log)
        {
                RetentionPolicy? overrides = null;
                if (keepLast != null || keepWithin != null || keepDaily != null || keepWeekly != null || keepMonthly != null)
                {
                    overrides = new RetentionPolicy();
                    int? Num(string? raw, string flag)
                    {
                        if (raw == null) return null;
                        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
                            throw new InvalidOperationException("Invalid " + flag + " '" + raw + "' (use >= 0)");
                        return n;
                    }
                    try
                    {
                        overrides.KeepLast = Num(keepLast, "--keep-last");
                        overrides.KeepDaily = Num(keepDaily, "--keep-daily");
                        overrides.KeepWeekly = Num(keepWeekly, "--keep-weekly");
                        overrides.KeepMonthly = Num(keepMonthly, "--keep-monthly");
                        if (keepWithin != null)
                        {
                            if (!Config.TryParseDuration(keepWithin, out var within))
                                throw new InvalidOperationException("Invalid --keep-within '" + keepWithin + "' (use 30d/12h)");
                            overrides.KeepWithin = within;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        log.Error(ex.Message);
                        return (null, 1);
                    }
                }
            return (overrides, 0);
        }

        internal static (int Removed, int Kept) ForgetSourceVersions(SourceConfig src, RetentionPolicy? overrides, bool dryRun, ILogger log)
        {
            var removed = 0;
            var keptTotal = 0;
                    var vcfg = src.Sync?.Versioning;
                    if (vcfg == null || string.IsNullOrEmpty(vcfg.Path)) return (removed, keptTotal);
                    if (!Directory.Exists(vcfg.Path))
                    {
                        log.Info("forget: no versions dir for " + src.Path);
                        return (removed, keptTotal);
                    }
                    var policy = overrides ?? RetentionPolicy.FromConfig(vcfg, log);
                    if (policy.IsEmpty) policy = new RetentionPolicy { KeepLast = 10 };
                    try
                    {
                        var groups = GroupVersions(vcfg.Path);
                        foreach (var group in groups.Values)
                        {
                            var files = group.Select(e => new VersionFile(e.FullPath, e.Stamp, e.HasStamp, e.Pinned)).ToList();
                            var kept = 0;
                            foreach (var d in policy.Evaluate(files, DateTime.Now))
                            {
                                if (d.Keep) { kept++; continue; }
                                if (dryRun)
                                    log.Info("would remove " + d.Path + " (" + d.Reason + ")");
                                else
                                {
                                    try { File.Delete(d.Path); removed++; }
                                    catch (Exception ex) { log.Warn("forget: cannot delete " + d.Path + ": " + ex.Message); }
                                }
                            }
                            keptTotal += kept;
                            if (dryRun) log.Info("would keep " + kept + " version(s) of " + group[0].Display);
                        }
                        if (!dryRun)
                        {
                            foreach (var dir in groups.Values.SelectMany(g => g).Select(e => Path.GetDirectoryName(e.FullPath)).Distinct(StringComparer.Ordinal))
                                if (dir != null) CopyAction.FlushDirectory(dir);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error("forget failed: " + src.Path, ex);
                    }
            return (removed, keptTotal);
        }

        internal sealed class VersionEntry
        {
            public string FullPath { get; set; } = "";
            public string Display { get; set; } = "";
            public DateTime Stamp { get; set; }
            public bool HasStamp { get; set; }
            public bool Pinned { get; set; }
            public long Size { get; set; }
        }

        // All version files under a versions base, grouped per original file
        // (directory + parsed original name). Unparseable names form
        // single-file groups: the policy always keeps them, and the list
        // still shows them.
        internal static Dictionary<string, List<VersionEntry>> GroupVersions(string versionBase)
        {
            var groups = new Dictionary<string, List<VersionEntry>>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(versionBase, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".pinned", StringComparison.Ordinal)
                    || fileName.EndsWith(".tictack.tmp", StringComparison.Ordinal))
                    continue;
                var dir = Path.GetDirectoryName(file) ?? versionBase;
                var dirRel = Path.GetRelativePath(versionBase, dir);
                if (dirRel == ".") dirRel = "";
                var entry = new VersionEntry { FullPath = file };
                string groupKey;
                if (TimestampVersioning.TryParseStamp(fileName, out var stamp, out var orig))
                {
                    entry.Stamp = stamp;
                    entry.HasStamp = true;
                    entry.Display = string.IsNullOrEmpty(dirRel) ? orig : dirRel + "/" + orig;
                    groupKey = dir + "\0" + orig;
                }
                else
                {
                    entry.Display = string.IsNullOrEmpty(dirRel) ? fileName : dirRel + "/" + fileName;
                    groupKey = file; // never grouped: always kept as its own group
                }
                entry.Pinned = TimestampVersioning.IsPinned(file);
                try { entry.Size = new FileInfo(file).Length; } catch { }
                if (!groups.TryGetValue(groupKey, out var list))
                {
                    list = new List<VersionEntry>();
                    groups[groupKey] = list;
                }
                list.Add(entry);
            }
            return groups;
        }

        internal static int RunVersionsList(TicTackConfig cfg, ILogger log, string? glob, string? latestRaw, bool json)
        {
            int? latest = null;
            if (latestRaw != null)
            {
                if (!int.TryParse(latestRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
                {
                    log.Error("Invalid --latest '" + latestRaw + "' (use >= 1)");
                    return 1;
                }
                latest = n;
            }
            var rows = new List<(string Source, VersionEntry Entry)>();
            foreach (var src in cfg.Sources)
            {
                var vpath = src.Sync?.Versioning?.Path;
                if (string.IsNullOrEmpty(vpath) || !Directory.Exists(vpath)) continue;
                Dictionary<string, List<VersionEntry>> groups;
                try { groups = GroupVersions(vpath); }
                catch (Exception ex) { log.Error("versions-list failed: " + src.Path, ex); continue; }
                foreach (var group in groups.Values.OrderBy(g => g[0].Display, StringComparer.OrdinalIgnoreCase))
                {
                    var ordered = group
                        .OrderByDescending(e => e.HasStamp ? e.Stamp : DateTime.MinValue)
                        .ToList();
                    if (latest != null && ordered.Count > latest.Value)
                        ordered = ordered.GetRange(0, latest.Value);
                    foreach (var e in ordered)
                    {
                        if (glob != null && !MatchesFileFilter(e.Display, glob))
                            continue;
                        rows.Add((src.Path, e));
                    }
                }
            }
            if (json)
            {
                // Pure JSON on stdout: bypass the logger (level prefixes
                // would corrupt the document for scripts).
                Console.WriteLine("[");
                for (int i = 0; i < rows.Count; i++)
                {
                    var (source, e) = rows[i];
                    var stamp = e.HasStamp ? "\"" + e.Stamp.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\"" : "null";
                    Console.WriteLine("  {\"source\": \"" + JsonEscape(source) + "\", \"file\": \"" + JsonEscape(e.Display)
                        + "\", \"timestamp\": " + stamp + ", \"size\": " + e.Size
                        + ", \"pinned\": " + (e.Pinned ? "true" : "false") + "}" + (i + 1 < rows.Count ? "," : ""));
                }
                Console.WriteLine("]");
            }
            else
            {
                if (rows.Count == 0) log.Info("No versions found.");
                foreach (var (_, e) in rows)
                {
                    var stamp = e.HasStamp ? e.Stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";
                    log.Info(stamp + "  " + FormatSize(e.Size).PadLeft(9) + "  " + e.Display + (e.Pinned ? "  [pinned]" : ""));
                }
            }
            return 0;
        }

        internal static int RunVersionsPin(TicTackConfig cfg, ILogger log, string? spec, bool pin)
        {
            var verb = pin ? "pin" : "unpin";
            if (string.IsNullOrWhiteSpace(spec))
            {
                log.Error("Missing <spec>: --versions-" + verb + " needs a version path or file@timestamp");
                return 1;
            }
            var targets = ResolvePinTargets(cfg, spec.Trim(), log);
            if (targets == null) return 1;
            if (targets.Count == 0)
            {
                log.Error("No versions match '" + spec + "'");
                return 1;
            }
            var changed = 0;
            foreach (var t in targets)
            {
                try
                {
                    if (pin)
                    {
                        if (!File.Exists(TimestampVersioning.PinPath(t)))
                        {
                            File.WriteAllText(TimestampVersioning.PinPath(t), "pinned " + DateTime.UtcNow.ToString("o"));
                            changed++;
                        }
                    }
                    else if (File.Exists(TimestampVersioning.PinPath(t)))
                    {
                        File.Delete(TimestampVersioning.PinPath(t));
                        changed++;
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("Cannot " + verb + " " + t + ": " + ex.Message);
                }
            }
            log.Info((pin ? "Pinned " : "Unpinned ") + changed + " of " + targets.Count + " version(s)");
            return 0;
        }

        // A pin spec is either a version file path (pinned directly) or
        // file@ts, where file is a wildcard over the displayed original path
        // (empty = all files) and ts is a timestamp prefix in any common
        // spelling — 2026-10-01, 20261001_1422, 2026-10-01T14:22 all match
        // the same versions. Null = hard error (message already logged).
        internal static List<string>? ResolvePinTargets(TicTackConfig cfg, string spec, ILogger log)
        {
            if (File.Exists(spec)) return ResolvePinDirectFile(cfg, spec, log);
            var at = spec.LastIndexOf('@');
            if (at < 0)
            {
                log.Error("Pin spec must be a version path or file@timestamp: " + spec);
                return null;
            }
            var pattern = spec.Substring(0, at).Trim();
            var tsPrefix = new string(spec.Substring(at + 1).Where(char.IsDigit).ToArray());
            if (tsPrefix.Length == 0)
            {
                log.Error("Pin timestamp needs at least one digit: " + spec);
                return null;
            }
            var hits = new List<string>();
            foreach (var src in cfg.Sources)
            {
                var vpath = src.Sync?.Versioning?.Path;
                if (string.IsNullOrEmpty(vpath) || !Directory.Exists(vpath)) continue;
                Dictionary<string, List<VersionEntry>> groups;
                try { groups = GroupVersions(vpath); }
                catch (Exception ex) { log.Error("Pin scan failed: " + src.Path, ex); continue; }
                foreach (var group in groups.Values)
                {
                    foreach (var e in group)
                    {
                        if (!e.HasStamp) continue;
                        if (!e.Stamp.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture).StartsWith(tsPrefix, StringComparison.Ordinal))
                            continue;
                        if (pattern.Length > 0 && !MatchesFileFilter(e.Display, pattern))
                            continue;
                        hits.Add(e.FullPath);
                    }
                }
            }
            return hits;
        }

        internal static List<string>? ResolvePinDirectFile(TicTackConfig cfg, string spec, ILogger log)
        {
            var full = Path.GetFullPath(spec);
            foreach (var src in cfg.Sources)
            {
                var vpath = src.Sync?.Versioning?.Path;
                if (string.IsNullOrEmpty(vpath)) continue;
                var root = Path.GetFullPath(vpath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (full.StartsWith(root, cmp)
                    && !full.EndsWith(".pinned", StringComparison.Ordinal)
                    && !full.EndsWith(".tictack.tmp", StringComparison.Ordinal))
                    return new List<string> { full };
            }
            log.Error("Not a version file: " + spec);
            return null;
        }

        internal static bool WildcardMatch(string text, string pattern)
        {
            // Glob over display paths: separators normalized both sides so a
            // Windows-style glob matches on Linux and vice versa.
            var t = text.Replace('\\', '/');
            var p = pattern.Replace('\\', '/');
            var rx = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            // Bounded match: the pattern is user input, the text is a path.
            // 2s can never legitimately trigger; it only caps ReDoS shapes.
            return Regex.IsMatch(t, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }

        // List/pin file filter: a bare word ("notes") matches by substring, a
        // pattern with * or ? matches as a glob. Forgiving interactively,
        // precise when asked. Both the full display path and the bare file
        // name count, so "notes" and "Desktop/notes.txt" both hit.
        internal static bool MatchesFileFilter(string display, string pattern)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
                return WildcardMatch(display, pattern) || WildcardMatch(Path.GetFileName(display), pattern);
            return display.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(display).Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        internal static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        static void DeleteWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(500);
                }
            }
        }

        static void RunCli(TicTackConfig cfg, ILogger log)
        {
            var pipelines = new List<SyncPipeline>();

            foreach (var src in cfg.Sources)
            {
                var pipeline = BuildPipeline(src, cfg, log);
                pipeline.Start();
                pipelines.Add(pipeline);
            }

            // Same scheduler the Windows service runs: systemd's --cli is the
            // Linux service, so jobs must fire here too — otherwise every
            // jobs: entry is dead config on Linux.
            var scheduler = new TimerScheduler(cfg, log);
            scheduler.Start();

            log.Info("All pipelines running. Press Ctrl+C or Enter to stop.");

            using (var wait = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    wait.Set();
                };
                if (!Console.IsInputRedirected)
                {
                    var thread = new Thread(() => { Console.ReadLine(); wait.Set(); });
                    thread.Start();
                }
                wait.WaitOne();
            }

            log.Info("Shutting down...");
            scheduler.Dispose();
            foreach (var p in pipelines) p.Dispose();
            log.Info("Stopped.");
        }

        static void RunService(string cfgPath)
        {
            ServiceBase.Run(new TicTackService(cfgPath));
        }

        static void LogCrash(Exception ex)
        {
            if (!OperatingSystem.IsWindows()) return;
            try { EventLog.WriteEntry("TicTackSv", "Main failed: " + ex, EventLogEntryType.Error); } catch { }
        }

        // Backslash escapes only exist in double-quoted YAML scalars, so a
        // hex/escape scanner error is near-always a "C:\..." path in double
        // quotes. Point at the one-line fix; anything else gets no hint.
        internal static string ConfigErrorHint(Exception ex) =>
            ex.Message.Contains("hexadecimal", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("quoted scalar", StringComparison.OrdinalIgnoreCase)
                ? " Hint: backslash escapes (\\U, \\x) only work in double-quoted strings — put Windows paths in 'single quotes' or use forward slashes."
                : "";

        // One derivation for the per-source name, state DB file, and deferred
        // file: the rebuild, the pipeline build, and the state path must agree.
        internal static string SourceName(SourceConfig src)
        {
            var name = Path.GetFileName(src.Path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? "default" : name;
        }

        internal static string DeferredFilePrefix(SourceConfig src) =>
            "tictack-deferred-" + SourceName(src).ToLowerInvariant() + "-" + DeferredDeletion.StableHash(src.Path);

        internal static string LegacyDeferredFileName(SourceConfig src) =>
            "tictack-deferred-" + SourceName(src).ToLowerInvariant() + ".json";

        internal static string DeferredDbPath(string deferredDir, SourceConfig src) =>
            Path.Combine(deferredDir, DeferredFilePrefix(src) + ".db");

        // Leaf name plus a stable hash of the full source path: two sources
        // sharing a folder name (C:\a\Desktop vs D:\b\Desktop) must not
        // share one .db. One-time move from the legacy leaf-only name so
        // existing installs keep their state (plus WAL sidecars).
        // Directory that holds the per-source state DBs. First source wins;
        // an install shares one root, so per-source divergence is not a case
        // the log-backup path needs to model.
        internal static string StateDbRoot(TicTackConfig cfg)
        {
            if (cfg.Sources != null)
                foreach (var s in cfg.Sources)
                    if (!string.IsNullOrEmpty(s.StateDbPath)) return s.StateDbPath!;
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TicTack")
                : "/var/lib/tictack";
        }

        internal static string GetStateDbPath(SourceConfig src)
        {
            var root = !string.IsNullOrEmpty(src.StateDbPath)
                ? src.StateDbPath
                : OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TicTack")
                    : "/var/lib/tictack";
            var path = Path.Combine(root, SourceName(src) + "-" + DeferredDeletion.StableHash(src.Path) + ".db");
            var legacy = Path.Combine(root, SourceName(src) + ".db");
            if (!File.Exists(path) && File.Exists(legacy))
            {
                try
                {
                    File.Move(legacy, path);
                    foreach (var ext in new[] { "-wal", "-shm" })
                    {
                        var side = legacy + ext;
                        if (File.Exists(side)) File.Move(side, path + ext);
                    }
                }
                catch { }
            }
            return path;
        }

        // Testable composite member selection: watcher always, USN when
        // probed OK, polling unless explicitly stood down with a healthy
        // USN member. Factories are injected so tests use fakes (USN
        // probing needs Windows + elevation).
        internal static List<IFileMonitor> BuildCompositeMembers(
            MonitorConfig monitorConfig, string path,
            Func<IFileMonitor> createWatcher, Func<IFileMonitor?> tryCreateUsn)
        {
            var members = new List<IFileMonitor> { createWatcher() };
            // Defense in depth: the journal cursor is an independent
            // observer alongside the lossy watcher. Path-keyed
            // dedup in the pipeline collapses the doubled events.
            // Probe-skips (non-Windows, unelevated) with a warn.
            IFileMonitor? usn = null;
            if (monitorConfig.Usn)
                usn = tryCreateUsn();
            if (usn != null) members.Add(usn);
            // Stand-down: with a healthy USN member, watcher overflows and
            // journal re-baselines fire on-demand covering scans, so the
            // hourly polling snapshot is pure redundancy. Kept automatically
            // when USN is unavailable: always a backstop.
            if (monitorConfig.PollingBackstop || usn == null)
                members.Add(new PollingMonitor(path, monitorConfig.PollingIntervalSeconds));
            return members;
        }

        // Startup one-liner names only: intervals and buffer sizes are config
        // echoes, not decisions. Polling-only keeps its interval (otherwise
        // the line says nothing about when it runs).
        internal static string MemberShortName(IFileMonitor m) => m switch
        {
            PollingMonitor => "polling",
            UsnJournalMonitor => "usn",
            _ => "watcher",
        };

        internal static SyncPipeline BuildPipeline(SourceConfig src, TicTackConfig cfg, ILogger log, bool logMonitorStartup = false)
        {
            var accessor = CreateAccessorOrFallback(src, log);
            var level = Config.ParseVerification(src.Sync != null ? src.Sync.Verification : null);
            var comparer = ComparerFactory.Create(level, accessor);
            var validator = ValidatorFactory.Create(level, accessor);
            var retry = src.Sync != null && src.Sync.Retry != null
                ? new ExponentialBackoffRetry(src.Sync.Retry.MaxAttempts, src.Sync.Retry.DelayMs, src.Sync.Retry.Backoff, log)
                : new ExponentialBackoffRetry(log: log);
            var versioning = VersioningFactory.Create(src.Sync != null ? src.Sync.Versioning : null, src.Destination, log);
            var deletion = DeletionStrategyFactory.Create(src.Sync != null ? src.Sync.Deletion : null, src.Destination);

            var durability = ParseDurability(src);

            var copyAction = new CopyAction(accessor, durability, log);
            var renameAction = new RenameAction(deletion, log);

            var stateDb = OpenStateDbOrNull(src, log);

            var monitorConfig = cfg.Monitor ?? new MonitorConfig();
            var (monitor, compositeMembers) = CreateMonitor(monitorConfig, src, stateDb, log, logMonitorStartup);

            var (excludes, deferredDir, deferredPrefix) = ResolveSourcePaths(src, cfg, log);

            var pipeline = new SyncPipeline(src, monitor, comparer, copyAction, renameAction,
                retry, validator, versioning, deletion, log, stateDb,
                autoExcludePrefixes: excludes,
                deferredDir: deferredDir,
                deferredFilePrefix: deferredPrefix);
            WireGapRescan(compositeMembers, pipeline);
            return pipeline;
        }

        private static IFileAccessor CreateAccessorOrFallback(SourceConfig s, ILogger l)
            {
                if (!string.Equals(s.Sync != null ? s.Sync.FileAccess : null, "vss", StringComparison.OrdinalIgnoreCase))
                    return new FileAccessor();
                if (!OperatingSystem.IsWindows())
                {
                    l.Warn("sync.file_access 'vss' is Windows-only; using direct access.");
                    return new FileAccessor();
                }
                try
                {
                    var a = new VssFileAccessor(l);
                    a.Probe();
                    return a;
                }
                catch (Exception ex)
                {
                    l.Warn("VSS unavailable (" + ex.Message + "); using direct access.");
                    return new FileAccessor();
                }
            }

        internal static IFileMonitor CreateWatcher(string path, MonitorConfig mc) => OperatingSystem.IsWindows()
            ? new FileWatcherMonitor(path, mc.WatcherBufferKb, mc.RestartDelaySeconds)
            : new FsWatchMonitor(path, mc.WatcherBufferKb, mc.RestartDelaySeconds);

        internal static IFileMonitor? TryCreateUsn(string path, MonitorConfig mc, StateDb? stateDb, ILogger log, string fallbackMsg)
            {
                if (!OperatingSystem.IsWindows())
                {
                    log.Warn("USN journal is Windows-only; " + fallbackMsg + ".");
                    return null;
                }
                try
                {
                    var m = new UsnJournalMonitor(path, mc.RestartDelaySeconds, mc.UsnPollIntervalMs, mc.UsnParentPrefilter, log, mc.UsnResolveGap, mc.UsnCheckpoint, stateDb);
                    m.ProbeVolume();
                    return m;
                }
                catch (Exception ex)
                {
                    log.Warn("USN journal unavailable (" + ex.Message + "); " + fallbackMsg + ".");
                    return null;
                }
            }

        internal static IFileMonitor CreateUsnOrFallback(string path, MonitorConfig mc, StateDb? stateDb, ILogger log)
        {
            return TryCreateUsn(path, mc, stateDb, log, "falling back to watcher") ?? CreateWatcher(path, mc);
        }

        internal static FileDurability ParseDurability(SourceConfig src)
        {
                var durability = FileDurability.Full;
                if (src.Sync != null)
                {
                    if (string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase)) durability = FileDurability.RenameOnly;
                    else if (string.Equals(src.Sync.Durability, "fdatasync", StringComparison.OrdinalIgnoreCase)) durability = FileDurability.FDataSync;
                }
            return durability;
        }

        internal static StateDb? OpenStateDbOrNull(SourceConfig src, ILogger log)
        {
                StateDb? stateDb = null;
                try
                {
                    var dbPath = GetStateDbPath(src);
                    stateDb = new StateDb(dbPath, log);
                }
                catch (Exception ex)
                {
                    log.Warn("StateDB init failed, continuing without: " + ex.Message);
                }
            return stateDb;
        }

        internal static (string[] Excludes, string DeferredDir, string DeferredPrefix) ResolveSourcePaths(SourceConfig src, TicTackConfig cfg, ILogger log)
        {
                var sep = Path.DirectorySeparatorChar;
                var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + sep;
                var srcDir = src.Path.TrimEnd('\\', '/') + sep;
                var excludes = new List<string>();
                if (baseDir.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase))
                    excludes.Add(baseDir);
                var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack.log")
                    : cfg.Logging.Path;
                var logDir = (Path.GetDirectoryName(logPath) ?? baseDir).TrimEnd('\\', '/') + sep;
                if (logDir.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && !excludes.Contains(logDir))
                    excludes.Add(logDir);
                var deferredDir = Path.GetDirectoryName(logPath) ?? baseDir;
                var deferredPrefix = DeferredFilePrefix(src);
                var legacyDeferred = Path.Combine(src.Destination, ".tictack-deferred.json");
                try
                {
                    // Old dest-side single file moves to the legacy per-source
                    // name; DeferredDeletion migrates it into the hold database.
                    var legacyTarget = Path.Combine(deferredDir, LegacyDeferredFileName(src));
                    // Legacy dest file first: short-circuits before any directory
                    // enumeration on fresh installs.
                    if (File.Exists(legacyDeferred) && !File.Exists(legacyTarget)
                        && Directory.EnumerateFiles(deferredDir, deferredPrefix + "-*.json").FirstOrDefault() == null)
                    {
                        if (!Directory.Exists(deferredDir))
                            Directory.CreateDirectory(deferredDir);
                        File.Move(legacyDeferred, legacyTarget);
                    }
                }
                catch (Exception ex) { log.Warn("Legacy deferred-state migration failed: " + ex.Message); }
            return (excludes.ToArray(), deferredDir, deferredPrefix);
        }

        internal static (IFileMonitor Monitor, List<IFileMonitor>? CompositeMembers) CreateMonitor(
            MonitorConfig monitorConfig, SourceConfig src, StateDb? stateDb, ILogger log, bool logMonitorStartup)
        {
                IFileMonitor monitor;
                List<IFileMonitor>? compositeMembers = null;
                switch (monitorConfig.Type != null ? monitorConfig.Type.ToLowerInvariant() : "")
                {
                    case "watcher":
                        monitor = CreateWatcher(src.Path, monitorConfig);
                        if (logMonitorStartup)
                            log.Info("Syncing: " + SourceName(src) + " (watcher).");
                        break;
                    case "usn":
                        monitor = CreateUsnOrFallback(src.Path, monitorConfig, stateDb, log);
                        if (logMonitorStartup)
                            log.Info("Syncing: " + SourceName(src) + (monitor is UsnJournalMonitor ? " (usn)." : " (watcher, USN unavailable)."));
                        break;
                    case "polling":
                        monitor = new PollingMonitor(src.Path, monitorConfig.PollingIntervalSeconds);
                        if (logMonitorStartup)
                            log.Info("Syncing: " + SourceName(src) + " (polling every " + monitorConfig.PollingIntervalSeconds + "s).");
                        break;
                    default:
                        compositeMembers = BuildCompositeMembers(
                            monitorConfig, src.Path, () => CreateWatcher(src.Path, monitorConfig), () => TryCreateUsn(src.Path, monitorConfig, stateDb, log, "skipping USN member"));
                        monitor = new CompositeMonitor(compositeMembers.ToArray());
                        if (logMonitorStartup)
                            log.Info("Syncing: " + SourceName(src) + " (composite: " + string.Join(" + ", compositeMembers.Select(MemberShortName)) + ").");
                        break;
                }
            return (monitor, compositeMembers);
        }

        internal static void WireGapRescan(List<IFileMonitor>? compositeMembers, SyncPipeline pipeline)
        {
            // Gap-triggered covering scans: watcher overflows and USN
            // re-baselines fire GapDetected; the pipeline answers with an
            // on-demand scan (creates + modifies + deletes, nothing
            // retained). Works with or without the polling member.
            if (compositeMembers != null)
            {
                foreach (var m in compositeMembers)
                {
                    if (m is IGapSource gap)
                        gap.GapDetected += (s, e) => { try { _ = pipeline.RequestRescanAsync(); } catch { } };
                }
            }
        }
    }
}
