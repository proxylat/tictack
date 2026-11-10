using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace TicTack
{
    public class TicTackService : ServiceBase
    {
        private readonly string _configPath;
        private ILogger? _log;
        private List<SyncPipeline>? _pipelines;
        private TimerScheduler? _scheduler;
        private Timer? _heartbeat;

        public TicTackService(string configPath)
        {
            _configPath = configPath;
            // Optional side-by-side installs (test services): the SCM name
            // must match before Run registers us, so read it here. Failures
            // fall through to the default; OnStart validates fully.
            var name = "TicTackSv";
            try
            {
                var early = Config.Load(configPath);
                if (early != null && !string.IsNullOrWhiteSpace(early.ServiceName))
                    name = early.ServiceName.Trim();
            }
            catch { }
            ServiceName = name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var cfg = Config.Load(_configPath);
                if (cfg == null)
                    throw new InvalidOperationException("Config not found: " + _configPath);

                // Pre-logger pass so the log/alert files themselves land on
                // resolved volumes; the post-logger EnsureResolved below
                // adds the wait-for-volume gate once logging exists.
                VolumeResolver.ResolveConfig(cfg);

                var logDir = Path.GetDirectoryName(_configPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                    ? Path.Combine(logDir, "tictack.log")
                    : cfg.Logging.Path;
                _log = LoggerFactory.Create(cfg.Logging.Level, logPath, cfg.Logging.MaxSizeMb,
                    cfg.Logging.MaxFiles, cfg.Logging.AlertPath, console: cfg.Logging.Console, eventLog: true,
                    backupPath: Path.Combine(Program.StateDbRoot(cfg), "tictack.log.bak"),
                    alertEnabled: cfg.Logging.AlertEnabled);

                if (!VolumeResolver.EnsureResolved(cfg, cfg.VolumeWaitMinutes, _log))
                    throw new InvalidOperationException("Volume(s) not available; see log for labels.");

                PowerGuard.Cleanup(cfg, _log);

                if (!Config.Validate(cfg, _log))
                    throw new InvalidOperationException("Invalid configuration");

                ApplyDelayedAutostart(cfg, _log);
                ServiceTuning.Apply(cfg, _log, ServiceName);

                _log.Info("TicTack Service starting");

                StartSyncEngine(cfg, _log);
            }
            catch (Exception ex)
            {
                FailStart(ex);
                throw;
            }
        }

        // Pipelines + scheduler + watchdog stall timer. Deferred: none here —
        // TimerScheduler owns job timing; pipelines own their own deferreds.
        private void StartSyncEngine(TicTackConfig cfg, ILogger log)
        {
            _pipelines = new List<SyncPipeline>();
            foreach (var src in cfg.Sources)
            {
                var pipeline = Program.BuildPipeline(src, cfg, log, logMonitorStartup: true);
                pipeline.Start();
                _pipelines.Add(pipeline);
            }

            _scheduler = new TimerScheduler(cfg, log);
            _scheduler.Start();

            if (cfg.Watchdog != null && cfg.Watchdog.Enabled)
            {
                var ms = Math.Max(60000, cfg.Watchdog.IntervalMinutes * 60000);
                var stallAfter = TimeSpan.FromMilliseconds(ms);
                _heartbeat = new Timer(_ =>
                {
                    log.Debug("[HEARTBEAT] Service running");
                    // Stall-while-alive: a faulted processor or a
                    // non-empty queue with no progress for a full
                    // interval means sync is wedged. Error fans out to
                    // tictack.log, the alert_path .txt, and EventLog
                    // via the MultiLogger — no new plumbing.
                    if (_pipelines != null)
                        foreach (var pipeline in _pipelines)
                        {
                            var fault = pipeline.CheckHealth(stallAfter);
                            if (fault != null)
                                log.Error("[WATCHDOG] Source '" + pipeline.SourcePath + "': " + fault);
                        }
                }, null, ms, ms);
            }

            log.Info("TicTack Service started");
        }

        // Applies the config.yaml delayed_autostart toggle to our own SCM
        // record (DelayedAutostart value). Idempotent one-RPC write per
        // start, effective on the next boot; never fails startup.
        private void ApplyDelayedAutostart(TicTackConfig cfg, ILogger log)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var scm = NativeServiceConfig.OpenSCManager(null, null, 0x0001);
                if (scm == IntPtr.Zero) { log.Debug("DelayedAutostart apply skipped: OpenSCManager failed"); return; }
                try
                {
                    var svc = NativeServiceConfig.OpenService(scm, ServiceName, 0x0002);
                    if (svc == IntPtr.Zero) { log.Debug("DelayedAutostart apply skipped: OpenService failed"); return; }
                    try
                    {
                        var info = new NativeServiceConfig.DelayedAutoStartInfo
                            { Delayed = cfg.DelayedAutostart ? 1 : 0 };
                        if (!NativeServiceConfig.ChangeServiceConfig2(svc, 3, ref info))
                            log.Warn("DelayedAutostart apply failed (win32 " + Marshal.GetLastWin32Error() + ")");
                        else log.Debug("DelayedAutostart set to " + (cfg.DelayedAutostart ? "1 (delayed-auto)" : "0 (auto)"));
                    }
                    finally { NativeServiceConfig.CloseServiceHandle(svc); }
                }
                finally { NativeServiceConfig.CloseServiceHandle(scm); }
            }
            catch (Exception ex) { log.Warn("DelayedAutostart apply failed: " + ex.Message); }
        }

        private static class NativeServiceConfig
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct DelayedAutoStartInfo { public int Delayed; }

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr OpenSCManager(string? machine, string? database, int access);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr OpenService(IntPtr scm, string name, int access);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, ref DelayedAutoStartInfo info);

            [DllImport("advapi32.dll", SetLastError = true)]
            internal static extern bool CloseServiceHandle(IntPtr handle);
        }

        // Best-effort crash trail: EventLog + temp crash log, then release
        // anything OnStart opened before the throw propagates.
        private void FailStart(Exception ex)
        {
            try
            {
                EventLog.WriteEntry("TicTackSv", "OnStart failed: " + ex.ToString(),
                    EventLogEntryType.Error);
            }
            catch { }
            try
            {
                var crashLog = Path.Combine(Path.GetTempPath(), "TicTackSv-crash.log");
                File.WriteAllText(crashLog, DateTime.Now + " OnStart failed:\r\n" + ex);
            }
            catch { }
            try
            {
                if (_pipelines != null) foreach (var pipeline in _pipelines) pipeline.Dispose();
            }
            catch { }
            if (_log is IDisposable disposable) disposable.Dispose();
        }

        protected override void OnStop()
        {
            if (_log != null) _log.Info("TicTack Service stopping");
            if (_heartbeat != null) _heartbeat.Dispose();
            if (_scheduler != null) _scheduler.Dispose();
            if (_pipelines != null) foreach (var p in _pipelines) p.Dispose();
            if (_log != null) _log.Info("TicTack Service stopped");
            if (_log is IDisposable disposable) disposable.Dispose();
        }

        protected override void OnShutdown()
        {
            OnStop();
        }
    }
}
