using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            ServiceName = "TicTackSv";
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
                _log = LoggerFactory.Create(cfg.Logging, logDir, console: cfg.Logging.Console, eventLog: true);

                if (!VolumeResolver.EnsureResolved(cfg, cfg.VolumeWaitMinutes, _log))
                    throw new InvalidOperationException("Volume(s) not available; see log for labels.");

                PowerGuard.Cleanup(cfg, _log);

                if (!Config.Validate(cfg, _log))
                    throw new InvalidOperationException("Invalid configuration");

                _log.Info("TicTack Service starting");

                _pipelines = new List<SyncPipeline>();
                foreach (var src in cfg.Sources)
                {
                    var pipeline = Program.BuildPipeline(src, cfg, _log, logMonitorStartup: true);
                    pipeline.Start();
                    _pipelines.Add(pipeline);
                }

                _scheduler = new TimerScheduler(cfg, _log);
                _scheduler.Start();

                if (cfg.Watchdog != null && cfg.Watchdog.Enabled)
                {
                    var ms = Math.Max(60000, cfg.Watchdog.IntervalMinutes * 60000);
                    var stallAfter = TimeSpan.FromMilliseconds(ms);
                    _heartbeat = new Timer(_ =>
                    {
                        _log!.Debug("[HEARTBEAT] Service running");
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
                                    _log.Error("[WATCHDOG] Source '" + pipeline.SourcePath + "': " + fault);
                            }
                    }, null, ms, ms);
                }

                _log.Info("TicTack Service started");
            }
            catch (Exception ex)
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
                throw;
            }
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
