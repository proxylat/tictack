using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    static class Program
    {
        static Program()
        {
            // .NET 10 SDK marks these as "type: platform" in deps.json
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
            if (OperatingSystem.IsWindows() && (isService || !Environment.UserInteractive))
                loggers.Add(new EventLogLogger());
            var log = new MultiLogger(loggers);

            PowerGuard.Cleanup(cfg, log);

            if (isExternalDrives)
            {
                RunExternalDrives(cfg, log);
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
        }

        static void RunExternalDrives(TicTackConfig cfg, ILogger log)
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

                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                if (OperatingSystem.IsWindows())
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + fullCmd;
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(fullCmd);
                }

                    try
                    {
                    using (var p = Process.Start(psi))
                    {
                        if (p == null)
                        {
                            log.Error("Backup on " + drive + " failed to start");
                            continue;
                        }
                        if (!p.WaitForExit(600000))
                        {
                            try { p.Kill(); } catch { }
                            log.Error("Backup on " + drive + " timed out after 10 minutes, killed");
                        }
                        else if (p.ExitCode != 0)
                            log.Error("Backup on " + drive + " exited " + p.ExitCode);
                        else
                            log.Info("Backup on " + drive + " completed");
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Backup on " + drive + " failed", ex);
                }
            }

            log.Info("All external drive backups complete");
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
                var copy = new CopyAction(accessor, src.Sync == null || !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase));

                var filters = new List<IFileFilter>();
                if (src.Filter != null && src.Filter.Exclude != null && src.Filter.Exclude.Count > 0)
                    filters.Add(new PatternFilter(src.Filter.Exclude.ToArray()));
                long? maxSize = src.Filter != null ? Config.ParseFileSizeLimit(src.Filter.MaxFileSizeMb) : null;
                if (maxSize.HasValue && maxSize.Value > 0)
                    filters.Add(new SizeFilter(maxSize.Value));
                var filter = filters.Count > 0 ? new CompositeFilter(filters) : null;

                log.Info("Syncing: " + srcPath + " -> " + dstBase);

                StateDb? stateDb = null;
                Dictionary<string, (long size, long mtime)>? cache = null;
                try
                {
                    var dbPath = GetStateDbPath(src);
                    stateDb = new StateDb(dbPath);
                    cache = stateDb.LoadAll();
                }
                catch (Exception ex) { log.Warn("StateDB init failed, continuing without: " + ex.Message); }

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

                var pendingState = new List<(string path, long size, long mtime)>();
                var stateLock = new object();
                void FlushState()
                {
                    if (stateDb == null || pendingState.Count == 0) return;
                    lock (stateLock)
                    {
                        try
                        {
                            stateDb.UpsertBatch(pendingState);
                            pendingState.Clear();
                        }
                        catch (Exception ex) { log.Debug("StateDb batch upsert failed: " + ex.Message); }
                    }
                }

                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, src.Sync != null ? src.Sync.InitialSyncWorkers : 2)
                };
                Parallel.ForEach(sourceFiles, options, kv =>
                {
                    try
                    {
                        var rel = kv.Key;
                        var srcFile = kv.Value;
                        var dstFile = Path.Combine(dstBase, rel);
                        if (!FileSnapshot.TryRead(srcFile, out var sourceSnapshot)) return;
                        var e = new FileChangedEventArgs(ChangeType.Created, srcFile);
                        var actionArgs = new FileActionArgs(e, srcPath, dstBase, sourceSnapshot);

                        if (cache != null && cache.TryGetValue(rel, out var cached)
                            && sourceSnapshot.Length == cached.size && sourceSnapshot.LastWriteTimeUtcTicks == cached.mtime)
                            return;

                        if (comparer.AreEqual(srcFile, dstFile, sourceSnapshot)) return;

                        if (versioning != null)
                            versioning.ArchivePreviousVersionAsync(dstFile, CancellationToken.None).GetAwaiter().GetResult();

                        var result = retry.ExecuteAsync(() => copy.ExecuteAsync(actionArgs, CancellationToken.None), CancellationToken.None).GetAwaiter().GetResult();
                        if (!result.Success)
                        {
                            log.Error("Copy failed: " + srcFile + ": " + result.ErrorMessage);
                            return;
                        }

                        if (!FileSnapshot.TryRead(srcFile, out var freshSnapshot))
                        {
                            log.Error("Validation FAILED: source disappeared: " + srcFile);
                            return;
                        }
                        if (validator != null)
                        {
                            var valid = validator.ValidateAsync(srcFile, dstFile, freshSnapshot).GetAwaiter().GetResult();
                            if (!valid)
                            {
                                log.Error("Validation FAILED: " + srcFile + " -> " + dstFile);
                                return;
                            }
                        }

                        if (stateDb != null)
                        {
                            lock (stateLock)
                            {
                                pendingState.Add((rel, freshSnapshot.Length, freshSnapshot.LastWriteTimeUtcTicks));
                                if (pendingState.Count >= 500) FlushState();
                            }
                        }

                        log.Info("Synced: " + srcFile);
                    }
                    catch (Exception ex) { log.Error("Initial sync failed: " + kv.Value, ex); }
                });

                FlushState();

                if (Directory.Exists(dstBase))
                {
                    try
                    {
                        var stale = new List<string>();
                        foreach (var dstFile in Directory.EnumerateFiles(dstBase, "*", SearchOption.AllDirectories))
                        {
                            var rel = dstFile.Substring(dstBase.Length).TrimStart('\\', '/');
                            var name = Path.GetFileName(dstFile);
                            if (name == ".tictack.lock" || name == ".tictack-deferred.json") continue;
                            if (!sourceFiles.ContainsKey(rel))
                                stale.Add(dstFile);
                        }

                        long stateCount = 0;
                        if (stateDb != null) { try { stateCount = stateDb.Count(); } catch { } }
                        long totalSize = 0;
                        foreach (var sf in stale) { try { totalSize += new FileInfo(sf).Length; } catch { } }
                        var sync = src.Sync;

                        bool blocked =
                            (sync != null && sync.DeleteThresholdCount > 0 && stale.Count >= sync.DeleteThresholdCount) ||
                            (sync != null && sync.DeleteThresholdSizeGb.HasValue && sync.DeleteThresholdSizeGb.Value > 0 &&
                             totalSize >= sync.DeleteThresholdSizeGb.Value * 1024L * 1024L * 1024L) ||
                            (sync != null && sync.DeleteThresholdPercent > 0 && stateCount > 50 &&
                             (double)stale.Count / stateCount * 100 >= sync.DeleteThresholdPercent);

                        if (blocked)
                        {
                            log.Error("Delete guard: " + stale.Count + " stale files would be removed, skipping. Run --rebuild to force.");
                        }
                        else
                        {
                            foreach (var dstFile in stale)
                            {
                                deletion.HandleDeletionAsync(null, dstFile, CancellationToken.None).GetAwaiter().GetResult();
                                if (stateDb != null)
                                {
                                    try
                                    {
                                        var rel = dstFile.Substring(dstBase.Length).TrimStart('\\', '/');
                                        stateDb.Delete(rel);
                                    }
                                    catch { }
                                }
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
            foreach (var src in cfg.Sources)
            {
                var dbPath = GetStateDbPath(src);
                try
                {
                    if (Directory.Exists(dbPath))
                    {
                        log.Warn("State DB path is a directory, expected a .db file: " + dbPath);
                        continue;
                    }
                    if (!File.Exists(dbPath) && !File.Exists(dbPath + "-wal") && !File.Exists(dbPath + "-shm"))
                        continue;
                    DeleteWithRetry(dbPath);
                    DeleteWithRetry(dbPath + "-wal");
                    DeleteWithRetry(dbPath + "-shm");
                    log.Info("Cleared state DB: " + dbPath);
                }
                catch (Exception ex)
                {
                    log.Error("Failed to delete state DB: " + dbPath + ": " + ex.Message);
                    log.Error("Another process (likely the running TicTackSv service) holds this file. Stop the service first ('sc stop TicTackSv' on Windows, 'sudo systemctl stop tictack' on Linux), then re-run --rebuild. Nothing was modified.");
                    Environment.Exit(1);
                }
            }

            var rbBaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                ? Path.Combine(rbBaseDir, "tictack.log")
                : cfg.Logging.Path;
            foreach (var src in cfg.Sources)
            {
                var dname = Path.GetFileName(src.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(dname)) dname = "default";
                var deferredFile = Path.Combine(Path.GetDirectoryName(logPath) ?? rbBaseDir, "tictack-deferred-" + dname.ToLowerInvariant() + ".json");
                try
                {
                    if (File.Exists(deferredFile))
                    {
                        File.Delete(deferredFile);
                        log.Info("Cleared deferred deletions: " + deferredFile);
                    }
                }
                catch { }
                try
                {
                    var legacyDeferred = Path.Combine(src.Destination, ".tictack-deferred.json");
                    if (File.Exists(legacyDeferred)) File.Delete(legacyDeferred);
                }
                catch { }
            }

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
                var copy = new CopyAction(accessor, src.Sync == null || !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase));

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
                         if (!FileSnapshot.TryRead(f, out var sourceSnapshot)) continue;
                         var e = new FileChangedEventArgs(ChangeType.Created, f);
                         var actionArgs = new FileActionArgs(e, srcPath, dstBase, sourceSnapshot);

                         if (comparer.AreEqual(f, dstFile, sourceSnapshot)) continue;

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
                             if (!FileSnapshot.TryRead(f, out var freshSnapshot))
                             {
                                 log.Error("Validation FAILED: source disappeared: " + f);
                                 continue;
                             }
                             var valid = validator.ValidateAsync(f, dstFile, freshSnapshot).GetAwaiter().GetResult();
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
                        if (UnderDir(rel, ".archive") || UnderDir(rel, ".versions")) continue;
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
                        if (rel == ".archive" || UnderDir(rel, ".archive")) continue;
                        if (rel == ".versions" || UnderDir(rel, ".versions")) continue;
                        if (rel == ".tictack.lock" || rel == ".tictack-deferred.json") continue;
                        if (sourceFiles.Any(f => UnderDir(f, rel))) continue;
                        deletion.HandleDeletionAsync(null, dir, CancellationToken.None).GetAwaiter().GetResult();
                        log.Info("Archived stale dir: " + dir);
                    }
                }

                log.Info("Rebuild complete: " + srcPath);
            }

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

        static bool UnderDir(string path, string prefix)
        {
            return path.StartsWith(prefix + '/', StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + '\\', StringComparison.OrdinalIgnoreCase);
        }

        static void RunCli(TicTackConfig cfg, ILogger log)
        {
            var valid = Config.Validate(cfg, log);
            if (!valid)
            {
                log.Error("Configuration invalid, exiting.");
                return;
            }

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
            try { EventLog.WriteEntry("TicTackSv", "Main failed: " + ex, EventLogEntryType.Error); } catch { }
        }

        static string GetStateDbPath(SourceConfig src)
        {
            var name = Path.GetFileName(src.Path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = "default";
            var root = !string.IsNullOrEmpty(src.StateDbPath)
                ? src.StateDbPath
                : OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TicTack")
                    : "/var/lib/tictack";
            return Path.Combine(root, name + ".db");
        }

        internal static SyncPipeline BuildPipeline(SourceConfig src, TicTackConfig cfg, ILogger log)
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

            var copyAction = new CopyAction(accessor, src.Sync == null || !string.Equals(src.Sync.Durability, "rename-only", StringComparison.OrdinalIgnoreCase));
            var renameAction = new RenameAction();

            StateDb? stateDb = null;
            try
            {
                var dbPath = GetStateDbPath(src);
                stateDb = new StateDb(dbPath);
            }
            catch (Exception ex)
            {
                log.Warn("StateDB init failed, continuing without: " + ex.Message);
            }

            IFileMonitor monitor;
            var monitorConfig = cfg.Monitor ?? new MonitorConfig();
            IFileMonitor CreateWatcher() => OperatingSystem.IsWindows()
                ? new FileWatcherMonitor(src.Path, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds)
                : new FsWatchMonitor(src.Path, monitorConfig.WatcherBufferKb, monitorConfig.RestartDelaySeconds);
            switch (monitorConfig.Type != null ? monitorConfig.Type.ToLowerInvariant() : "")
            {
                case "watcher":
                    monitor = CreateWatcher();
                    break;
                case "polling":
                    monitor = new PollingMonitor(src.Path, monitorConfig.PollingIntervalSeconds);
                    break;
                default:
                    monitor = new CompositeMonitor(
                        CreateWatcher(),
                        new PollingMonitor(src.Path, monitorConfig.PollingIntervalSeconds)
                    );
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

            var dname = Path.GetFileName(src.Path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(dname)) dname = "default";
            var deferredPath = Path.Combine(Path.GetDirectoryName(logPath) ?? baseDir, "tictack-deferred-" + dname.ToLowerInvariant() + ".json");
            var legacyDeferred = Path.Combine(src.Destination, ".tictack-deferred.json");
            try
            {
                if (!File.Exists(deferredPath) && File.Exists(legacyDeferred))
                {
                    var dd = Path.GetDirectoryName(deferredPath);
                    if (!string.IsNullOrEmpty(dd) && !Directory.Exists(dd))
                        Directory.CreateDirectory(dd);
                    File.Move(legacyDeferred, deferredPath);
                }
            }
            catch { }

            return new SyncPipeline(src, monitor, comparer, copyAction, renameAction,
                retry, validator, versioning, deletion, log, stateDb,
                autoExcludePrefixes: excludes.ToArray(),
                deferredPath: deferredPath);
        }
    }
}
