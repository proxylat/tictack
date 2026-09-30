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
            var cfgPath = Path.Combine(baseDir, "config.yaml");
            bool isService = false, isCli = false, isOnce = false, isValidate = false, isExternalDrives = false, isRebuild = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config":
                        if (i + 1 < args.Length) cfgPath = args[++i];
                        break;
                    case "--service": isService = true; break;
                    case "--cli": isCli = true; break;
                    case "--once": isOnce = true; break;
                    case "--validate": isValidate = true; break;
                    case "--external-drives": isExternalDrives = true; break;
                    case "--rebuild": isRebuild = true; break;
                }
            }

            var cfg = Config.Load(cfgPath);
            if (cfg == null)
            {
                Console.Error.WriteLine("config.yaml not found or invalid");
                return 1;
            }

            // Pre-logger pass so the log/alert files themselves land on
            // resolved volumes; the post-logger EnsureResolved below
            // adds the wait-for-volume gate once logging exists.
            VolumeResolver.ResolveConfig(cfg);

            var log = LoggerFactory.Create(
                cfg.Logging,
                baseDir,
                console: isCli || isOnce || isValidate || isRebuild || cfg.Logging.Console,
                eventLog: OperatingSystem.IsWindows() && (isService || !Environment.UserInteractive));
            rootLogger = log;

            if (!VolumeResolver.EnsureResolved(cfg, cfg.VolumeWaitMinutes, log))
                return 1;

            if (!Config.Validate(cfg, log))
                return 1;

            PowerGuard.Cleanup(cfg, log);

            if (isExternalDrives)
            {
                await RunExternalDrivesAsync(cfg, log);
                return 0;
            }

            if (isRebuild)
            {
                await RunRebuildAsync(cfg, log);
                return 0;
            }

            if (isValidate)
            {
                log.Info("Configuration OK");
                return 0;
            }

            if (isOnce)
            {
                await RunOnceAsync(cfg, log);
                return 0;
            }

            if (isCli)
            {
                RunCli(cfg, log);
                return 0;
            }

            if (OperatingSystem.IsWindows() && (isService || !Environment.UserInteractive))
            {
                RunService(cfgPath);
                return 0;
            }

            log.Info("Usage: TicTackSv.exe --cli | --once | --rebuild | --validate | --external-drives | --service | --config <path>");
            return 0;
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

        static async Task RunExternalDrivesAsync(TicTackConfig cfg, ILogger log)
        {
            if (cfg.ExternalDrives == null || string.IsNullOrEmpty(cfg.ExternalDrives.Command))
            {
                log.Error("No external_drives configuration found in config.yaml");
                return;
            }

            var cmd = cfg.ExternalDrives.Command;
            var sourcePaths = cfg.Sources != null && cfg.Sources.Count > 0
                ? string.Join(" ", cfg.Sources.ConvertAll(s => "\"" + s.Path + "\""))
                : null;

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

            var drives = DriveDiscoverer.GetEligibleDrives(cfg.ExternalDrives, destinationDrives, log);

            if (drives.Count == 0)
            {
                log.Info("No eligible external drives found");
                return;
            }

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
                return;
            }

            var wd = cfg.ExternalDrives.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;

            foreach (var drive in drives)
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

            log.Info("All external drive backups complete");
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

        internal static async Task RunRebuildAsync(TicTackConfig cfg, ILogger log)
        {
            log.Info("Full rebuild starting");
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(baseDir, "tictack.log")
                : cfg.Logging.Path;
            var deferredDir = Path.GetDirectoryName(logPath) ?? baseDir;
            var failures = 0;

            foreach (var src in cfg.Sources)
            {
                if (!Directory.Exists(src.Path))
                {
                    log.Error("Source folder missing, rebuild skipped: " + src.Path);
                    failures++;
                    continue;
                }
                if (!DriveGuard.IsReady(src.Destination))
                {
                    log.Error("Destination drive is not ready: " + src.Destination);
                    return;
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
                    failures++;
                    log.Error("Rebuild failed: " + src.Path, ex);
                }
            }

            if (failures > 0)
                log.Warn("Full rebuild finished with " + failures + " failure(s).");
            else
                log.Info("Full rebuild finished. All databases cleared, source re-scanned, parity enforced.");
        }

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

        internal static string DescribeMembers(List<IFileMonitor> members, MonitorConfig monitorConfig)
        {
            var parts = new List<string>();
            foreach (var m in members)
            {
                if (m is PollingMonitor) parts.Add("polling every " + monitorConfig.PollingIntervalSeconds + "s");
                else if (m is UsnJournalMonitor) parts.Add("usn");
                else parts.Add("watcher");
            }
            return string.Join(" + ", parts);
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
            var versioning = VersioningFactory.Create(src.Sync != null ? src.Sync.Versioning : null, src.Destination);
            var deletion = DeletionStrategyFactory.Create(src.Sync != null ? src.Sync.Deletion : null, src.Destination);

            var durability = FileDurability.Full;
            if (src.Sync != null)
            {
                if (string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase)) durability = FileDurability.RenameOnly;
                else if (string.Equals(src.Sync.Durability, "fdatasync", StringComparison.OrdinalIgnoreCase)) durability = FileDurability.FDataSync;
            }
            var copyAction = new CopyAction(accessor, durability, log);
            var renameAction = new RenameAction(deletion, log);

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

            IFileMonitor monitor;
            List<IFileMonitor>? compositeMembers = null;
            var monitorConfig = cfg.Monitor ?? new MonitorConfig();
            IFileAccessor CreateAccessorOrFallback(SourceConfig s, ILogger l)
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
            IFileMonitor CreateWatcher() => OperatingSystem.IsWindows()
                ? new FileWatcherMonitor(src.Path, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds)
                : new FsWatchMonitor(src.Path, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds);
            IFileMonitor? TryCreateUsn(string fallbackMsg)
            {
                if (!OperatingSystem.IsWindows())
                {
                    log.Warn("USN journal is Windows-only; " + fallbackMsg + ".");
                    return null;
                }
                try
                {
                    var m = new UsnJournalMonitor(src.Path, monitorConfig.RestartDelaySeconds, monitorConfig.UsnPollIntervalMs, monitorConfig.UsnParentPrefilter, log);
                    m.ProbeVolume();
                    return m;
                }
                catch (Exception ex)
                {
                    log.Warn("USN journal unavailable (" + ex.Message + "); " + fallbackMsg + ".");
                    return null;
                }
            }
            IFileMonitor CreateUsnOrFallback()
            {
                return TryCreateUsn("falling back to watcher") ?? CreateWatcher();
            }
            switch (monitorConfig.Type != null ? monitorConfig.Type.ToLowerInvariant() : "")
            {
                case "watcher":
                    monitor = CreateWatcher();
                    if (logMonitorStartup)
                        log.Info("Syncing: " + SourceName(src) + " (watcher).");
                    break;
                case "usn":
                    monitor = CreateUsnOrFallback();
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
                        monitorConfig, src.Path, CreateWatcher, () => TryCreateUsn("skipping USN member"));
                    monitor = new CompositeMonitor(compositeMembers.ToArray());
                    if (logMonitorStartup)
                        log.Info("Syncing: " + SourceName(src) + " (composite: " + string.Join(" + ", compositeMembers.Select(MemberShortName)) + ").");
                    break;
            }

            var sep = Path.DirectorySeparatorChar;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + sep;
            var srcDir = src.Path.TrimEnd('\\', '/') + sep;
            var excludes = new List<string>();
            if (baseDir.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase))
                excludes.Add(baseDir);
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(baseDir, "tictack.log")
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

            var pipeline = new SyncPipeline(src, monitor, comparer, copyAction, renameAction,
                retry, validator, versioning, deletion, log, stateDb,
                autoExcludePrefixes: excludes.ToArray(),
                deferredDir: deferredDir,
                deferredFilePrefix: deferredPrefix);
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
            return pipeline;
        }
    }
}
