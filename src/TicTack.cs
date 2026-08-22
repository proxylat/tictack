using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.ServiceProcess;
using System.Threading;

namespace TicTack
{
    static class Program
    {
        static Program()
        {
            // ponytail: .NET 10 SDK marks these as "type: platform" in deps.json
            // but the shared framework doesn't ship them yet. Use assembly-resolve
            // to load from app directory when the runtime can't find them.
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

        static int Main(string[] args)
        {
            var diag = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack-diag.log");
            try { File.AppendAllText(diag, DateTime.Now + " [1] Main started, baseDir=" + AppDomain.CurrentDomain.BaseDirectory + "\n"); } catch { }
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "config.yaml");
            bool isService = false, isCli = false, isOnce = false, isValidate = false, isResticDrives = false, isRebuild = false;

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
                    case "--restic-drives": isResticDrives = true; break;
                    case "--rebuild": isRebuild = true; break;
                }
            }

            var cfg = Config.Load(cfgPath);
            if (cfg == null)
            {
                Console.Error.WriteLine("config.yaml not found or invalid");
                return 1;
            }

            VolumeResolver.ResolveConfig(cfg);

            var level = LogLevelParser.Parse(cfg.Logging.Level);
            var loggers = new List<ILogger>();
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(baseDir, "tictack.log")
                : cfg.Logging.Path;
            loggers.Add(new FileLogger(logPath, level, cfg.Logging.MaxSizeMb, cfg.Logging.MaxFiles));
            if (isCli || isOnce || isValidate || cfg.Logging.Console)
                loggers.Add(new ConsoleLogger(level));
            loggers.Add(new DesktopAlertLogger(level > LogLevel.Debug ? LogLevel.Warn : LogLevel.Debug, cfg.Logging.AlertPath));
            loggers.Add(new EventLogLogger());
            var log = new MultiLogger(loggers);

            PowerGuard.Cleanup(cfg, log);

            if (isResticDrives)
            {
                RunResticDrives(cfg, log);
                return 0;
            }

            if (isRebuild)
            {
                RunRebuild(cfg, log);
                return 0;
            }

            if (isValidate)
            {
                var valid = Config.Validate(cfg, log);
                log.Info(valid ? "Configuration OK" : "Configuration INVALID");
                return valid ? 0 : 1;
            }

            if (isOnce)
            {
                RunOnce(cfg, log);
                return 0;
            }

            if (isCli)
            {
                RunCli(cfg, log);
                return 0;
            }

            if (isService || !Environment.UserInteractive)
            {
                try { File.AppendAllText(diag, DateTime.Now + " [2] Before RunService\n"); } catch { }
                try { File.AppendAllText(diag, DateTime.Now + " [2a] dir dlls: " + string.Join(", ", Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll").Select(Path.GetFileName)) + "\n"); } catch { }
                RunService(cfgPath);
                try { File.AppendAllText(diag, DateTime.Now + " [3] After RunService (returning 0)\n"); } catch { }
                return 0;
            }

            log.Info("Usage: TicTackSv.exe --cli | --once | --rebuild | --validate | --restic-drives | --service | --config <path>");
            return 0;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(diag, DateTime.Now + " [E] Main catch: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace + "\n"); } catch { }
                var crashLog = Path.Combine(Path.GetTempPath(), "TicTackSv-crash.log");
                try { File.WriteAllText(crashLog, DateTime.Now + " Main failed:\r\n" + ex); } catch { }
                LogCrash(ex);
                return 1;
            }
        }

        static void RunResticDrives(TicTackConfig cfg, ILogger log)
        {
            if (cfg.ResticDrives == null || string.IsNullOrEmpty(cfg.ResticDrives.Command))
            {
                log.Error("No restic_drives configuration found in config.yaml");
                return;
            }

            var cmd = cfg.ResticDrives.Command;
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

            var drives = DriveDiscoverer.GetEligibleDrives(cfg.ResticDrives, destinationDrives, log);

            if (drives.Count == 0)
            {
                log.Info("No eligible external drives found");
                return;
            }

            Console.WriteLine("Eligible drives:");
            foreach (var drive in drives)
            {
                var repoPath = cmd.Contains("{drive}")
                    ? cmd.Split(new[] { "-r " }, StringSplitOptions.None).LastOrDefault()?.Replace("{drive}", drive.TrimEnd('\\'))
                    : null;
                Console.WriteLine("  " + drive + (repoPath != null ? " → " + repoPath : ""));
            }
            Console.WriteLine();
            Console.Write("Proceed? (y/n): ");
            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input) || !input.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                log.Info("Restic drives cancelled by user");
                return;
            }

            var wd = cfg.ResticDrives.WorkingDir ?? AppDomain.CurrentDomain.BaseDirectory;

            foreach (var drive in drives)
            {
                var fullCmd = cmd.Replace("{drive}", drive.TrimEnd('\\'));
                if (sourcePaths != null)
                    fullCmd = fullCmd.Replace("{source}", sourcePaths);

                log.Info("Running restic for " + drive + ": " + fullCmd);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + fullCmd,
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                try
                {
                    using (var p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(600000))
                        {
                            try { p.Kill(); } catch { }
                            log.Error("Restic on " + drive + " timed out after 10 minutes, killed");
                        }
                        else if (p.ExitCode != 0)
                            log.Error("Restic on " + drive + " exited " + p.ExitCode);
                        else
                            log.Info("Restic on " + drive + " completed");
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Restic on " + drive + " failed", ex);
                }
            }

            log.Info("All restic drive backups complete");
        }

        static void RunOnce(TicTackConfig cfg, ILogger log)
        {
            log.Info("Once-off sync starting");

            foreach (var src in cfg.Sources)
            {
                var srcPath = src.Path;
                var dstBase = src.Destination;
                var accessor = new FileAccessor();
                var comparer = ComparerFactory.Create(Config.ParseVerification(src.Sync != null ? src.Sync.Verification : null), accessor);
                var validator = ValidatorFactory.Create(Config.ParseVerification(src.Sync != null ? src.Sync.Verification : null), accessor);
                var retry = src.Sync != null && src.Sync.Retry != null
                    ? new ExponentialBackoffRetry(src.Sync.Retry.MaxAttempts, src.Sync.Retry.DelayMs, src.Sync.Retry.Backoff)
                    : new ExponentialBackoffRetry();
            var versioning = VersioningFactory.Create(src.Sync != null ? src.Sync.Versioning : null, src.Destination);
                var deletion = DeletionStrategyFactory.Create(src.Sync != null ? src.Sync.Deletion : null, dstBase);
                var copy = new CopyAction(accessor);

                var filters = new List<IFileFilter>();
                if (src.Filter != null && src.Filter.Exclude != null && src.Filter.Exclude.Count > 0)
                    filters.Add(new PatternFilter(src.Filter.Exclude.ToArray()));
                long? maxSize = src.Filter != null ? Config.ParseFileSizeLimit(src.Filter.MaxFileSizeMb) : null;
                if (maxSize.HasValue && maxSize.Value > 0)
                    filters.Add(new SizeFilter(maxSize.Value));
                var filter = filters.Count > 0 ? new CompositeFilter(filters) : null;

                log.Info("Syncing: " + srcPath + " -> " + dstBase);

                var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(srcPath))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(srcPath, "*", SearchOption.AllDirectories))
                        {
                            if (filter != null && !filter.ShouldProcess(f))
                                continue;
                            var rel = f.Substring(srcPath.Length).TrimStart('\\', '/');
                            sourceFiles[rel] = f;
                        }
                    }
                    catch (UnauthorizedAccessException) { log.Warn("Access denied scanning " + srcPath); }
                }

                foreach (var kv in sourceFiles)
                {
                    var rel = kv.Key;
                    var srcFile = kv.Value;
                    var dstFile = Path.Combine(dstBase, rel);
                    var e = new FileChangedEventArgs(ChangeType.Created, srcFile);
                    var actionArgs = new FileActionArgs(e, srcPath, dstBase);

                    if (comparer.AreEqual(srcFile, dstFile))
                        continue;

                    if (versioning != null)
                        versioning.ArchivePreviousVersionAsync(dstFile, CancellationToken.None).GetAwaiter().GetResult();

                    var result = retry.ExecuteAsync(() => copy.ExecuteAsync(actionArgs, CancellationToken.None), CancellationToken.None).GetAwaiter().GetResult();

                    if (!result.Success)
                    {
                        log.Error("Copy failed: " + srcFile + ": " + result.ErrorMessage);
                        continue;
                    }

                    if (validator != null)
                    {
                        var valid = validator.ValidateAsync(srcFile, dstFile).GetAwaiter().GetResult();
                        if (!valid)
                        {
                            log.Error("Validation FAILED: " + srcFile + " -> " + dstFile);
                            continue;
                        }
                    }

                    log.Info("Synced: " + srcFile);
                }

                if (Directory.Exists(dstBase))
                {
                    try
                    {
                        foreach (var dstFile in Directory.EnumerateFiles(dstBase, "*", SearchOption.AllDirectories))
                        {
                            var rel = dstFile.Substring(dstBase.Length).TrimStart('\\', '/');
                            if (!sourceFiles.ContainsKey(rel))
                            {
                                deletion.HandleDeletionAsync(null, dstFile, CancellationToken.None).GetAwaiter().GetResult();
                                log.Debug("Deleted: " + dstFile);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                }
            }

            log.Info("Once-off sync complete");
        }

        static void RunRebuild(TicTackConfig cfg, ILogger log)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (var src in cfg.Sources)
            {
                var dbPath = src.StateDbPath;
                if (string.IsNullOrEmpty(dbPath))
                {
                    var name = Path.GetFileName(src.Path.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(name)) name = "default";
                    dbPath = Path.Combine(localAppData, "TicTack", name + ".db");
                }
                try
                {
                    if (Directory.Exists(dbPath))
                    {
                        log.Warn("State DB path is a directory, expected a .db file: " + dbPath);
                        continue;
                    }
                    if (File.Exists(dbPath))
                    {
                        File.Delete(dbPath);
                        log.Info("Cleared state DB: " + dbPath);
                    }
                }
                catch (Exception ex) { log.Error("Failed to delete state DB: " + dbPath + ": " + ex.Message); }
            }

            var deferredPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".tictack-deferred.json");
            try
            {
                File.Delete(deferredPath);
                log.Info("Cleared deferred deletions: " + deferredPath);
            }
            catch { }

            var accessor = new FileAccessor();
            foreach (var src in cfg.Sources)
            {
                var srcPath = src.Path;
                var dstBase = src.Destination;
                var level = Config.ParseVerification(src.Sync != null ? src.Sync.Verification : null);
                var comparer = ComparerFactory.Create(level, accessor);
                var validator = ValidatorFactory.Create(level, accessor);
                var retry = src.Sync != null && src.Sync.Retry != null
                    ? new ExponentialBackoffRetry(src.Sync.Retry.MaxAttempts, src.Sync.Retry.DelayMs, src.Sync.Retry.Backoff)
                    : new ExponentialBackoffRetry();
                var versioning = VersioningFactory.Create(src.Sync != null ? src.Sync.Versioning : null, dstBase);
                var deletion = DeletionStrategyFactory.Create(src.Sync != null ? src.Sync.Deletion : null, dstBase);
                var copy = new CopyAction(accessor);

                var filters = new List<IFileFilter>();
                if (src.Filter != null && src.Filter.Exclude != null && src.Filter.Exclude.Count > 0)
                    filters.Add(new PatternFilter(src.Filter.Exclude.ToArray()));
                long? maxSize = src.Filter != null ? Config.ParseFileSizeLimit(src.Filter.MaxFileSizeMb) : null;
                if (maxSize.HasValue && maxSize.Value > 0)
                    filters.Add(new SizeFilter(maxSize.Value));
                var filter = filters.Count > 0 ? new CompositeFilter(filters) : null;

                log.Info("Rebuilding: " + srcPath + " -> " + dstBase);

                var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(srcPath))
                {
                    foreach (var f in Directory.EnumerateFiles(srcPath, "*", SearchOption.AllDirectories))
                    {
                        if (filter != null && !filter.ShouldProcess(f)) continue;
                        var rel = f.Substring(srcPath.Length).TrimStart('\\', '/');
                        sourceFiles.Add(rel);

                        var dstFile = Path.Combine(dstBase, rel);
                        var e = new FileChangedEventArgs(ChangeType.Created, f);
                        var actionArgs = new FileActionArgs(e, srcPath, dstBase);

                        if (comparer.AreEqual(f, dstFile)) continue;

                        if (versioning != null)
                            versioning.ArchivePreviousVersionAsync(dstFile, CancellationToken.None).GetAwaiter().GetResult();

                        var result = retry.ExecuteAsync(() => copy.ExecuteAsync(actionArgs, CancellationToken.None), CancellationToken.None).GetAwaiter().GetResult();

                        if (!result.Success)
                        {
                            log.Error("Copy failed: " + f + ": " + result.ErrorMessage);
                            continue;
                        }

                        if (validator != null)
                        {
                            var valid = validator.ValidateAsync(f, dstFile).GetAwaiter().GetResult();
                            if (!valid)
                                log.Error("Validation FAILED: " + f + " -> " + dstFile);
                        }
                        log.Info("Synced: " + f);
                    }
                }

                if (Directory.Exists(dstBase))
                {
                    foreach (var dstFile in Directory.EnumerateFiles(dstBase, "*", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(dstFile);
                        if (name == ".tictack.lock" || name == ".tictack-deferred.json") continue;
                        var rel = dstFile.Substring(dstBase.Length).TrimStart('\\', '/');
                        if (!sourceFiles.Contains(rel))
                        {
                            deletion.HandleDeletionAsync(null, dstFile, CancellationToken.None).GetAwaiter().GetResult();
                            log.Info("Archived stale: " + dstFile);
                        }
                    }
                    foreach (var dir in Directory.EnumerateDirectories(dstBase, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length))
                    {
                        var rel = dir.Substring(dstBase.Length).TrimStart('\\', '/');
                        if (rel == ".archive" || rel.StartsWith(".archive\\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (rel == ".versions" || rel.StartsWith(".versions\\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (rel == ".tictack.lock" || rel == ".tictack-deferred.json") continue;
                        if (sourceFiles.Any(f => f.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                        deletion.HandleDeletionAsync(null, dir, CancellationToken.None).GetAwaiter().GetResult();
                        log.Info("Archived stale dir: " + dir);
                    }
                }

                log.Info("Rebuild complete: " + srcPath);
            }

            log.Info("Full rebuild finished. All databases cleared, source re-scanned, parity enforced.");
        }

        static void RunCli(TicTackConfig cfg, ILogger log)
        {
            var valid = Config.Validate(cfg, log);
            if (!valid)
            {
                log.Error("Configuration invalid, exiting.");
                return;
            }

            var pipelines = new List<ISyncPipeline>();

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
                var thread = new Thread(() => { Console.ReadLine(); wait.Set(); });
                thread.Start();
                wait.WaitOne();
            }

            log.Info("Shutting down...");
            foreach (var p in pipelines) p.Dispose();
            log.Info("Stopped.");
        }

        static void RunService(string cfgPath)
        {
            var diag = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tictack-diag.log");
            try { File.AppendAllText(diag, DateTime.Now + " [4] RunService: creating TicTackService\n"); } catch { }
            try
            {
                var svc = new TicTackService(cfgPath);
                try { File.AppendAllText(diag, DateTime.Now + " [5] RunService: calling ServiceBase.Run\n"); } catch { }
                ServiceBase.Run(svc);
                try { File.AppendAllText(diag, DateTime.Now + " [6] RunService: ServiceBase.Run returned\n"); } catch { }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(diag, DateTime.Now + " [E] RunService failed: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace + "\n"); } catch { }
                try { File.AppendAllText(diag, DateTime.Now + " [E2] dir dlls: " + string.Join(", ", Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll").Select(Path.GetFileName)) + "\n"); } catch { }
                throw;
            }
        }

        static void LogCrash(Exception ex)
        {
            try { EventLog.WriteEntry("TicTackSv", "Main failed: " + ex, EventLogEntryType.Error); } catch { }
        }

        internal static ISyncPipeline BuildPipeline(SourceConfig src, TicTackConfig cfg, ILogger log)
        {
            var accessor = new FileAccessor();
            var level = Config.ParseVerification(src.Sync != null ? src.Sync.Verification : null);
            var comparer = ComparerFactory.Create(level, accessor);
            var validator = ValidatorFactory.Create(level, accessor);
            var retry = src.Sync != null && src.Sync.Retry != null
                ? new ExponentialBackoffRetry(src.Sync.Retry.MaxAttempts, src.Sync.Retry.DelayMs, src.Sync.Retry.Backoff)
                : new ExponentialBackoffRetry();
            var versioning = VersioningFactory.Create(src.Sync != null ? src.Sync.Versioning : null, src.Destination);
            var deletion = DeletionStrategyFactory.Create(src.Sync != null ? src.Sync.Deletion : null, src.Destination);

            var copyAction = new CopyAction(accessor);
            var deleteAction = new DeleteAction();
            var renameAction = new RenameAction();

            StateDb stateDb = null;
            try
            {
                var dbPath = src.StateDbPath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TicTack",
                    Path.GetFileName(src.Path.TrimEnd('\\', '/')) + ".db");
                stateDb = new StateDb(dbPath);
            }
            catch (Exception ex)
            {
                log.Warn("StateDB init failed, continuing without: " + ex.Message);
            }

            IFileMonitor monitor;
            switch (cfg.Monitor != null && cfg.Monitor.Type != null ? cfg.Monitor.Type.ToLowerInvariant() : "")
            {
                case "watcher":
                    monitor = new FileWatcherMonitor(src.Path, cfg.Monitor.WatcherBufferKb, cfg.Monitor.RestartDelaySeconds);
                    break;
                case "polling":
                    monitor = new PollingMonitor(src.Path, cfg.Monitor.PollingIntervalSeconds);
                    break;
                default:
                    monitor = new CompositeMonitor(
                        new FileWatcherMonitor(src.Path, cfg.Monitor.WatcherBufferKb, cfg.Monitor.RestartDelaySeconds),
                        new PollingMonitor(src.Path, cfg.Monitor.PollingIntervalSeconds)
                    );
                    break;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + '\\';
            var srcDir = src.Path.TrimEnd('\\', '/') + '\\';
            var excludes = new List<string>();
            if (baseDir.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase))
                excludes.Add(baseDir);
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(baseDir, "tictack.log")
                : cfg.Logging.Path;
            var logDir = Path.GetDirectoryName(logPath).TrimEnd('\\', '/') + '\\';
            if (logDir.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && !excludes.Contains(logDir))
                excludes.Add(logDir);

            return new SyncPipeline(src, monitor, comparer, copyAction, deleteAction, renameAction,
                retry, validator, versioning, deletion, log, stateDb,
                autoExcludePrefixes: excludes.ToArray());
        }
    }
}
