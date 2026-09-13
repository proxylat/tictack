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

                VolumeResolver.ResolveConfig(cfg);

                var level = LogLevelParser.Parse(cfg.Logging.Level);
                var logPath = string.IsNullOrEmpty(cfg.Logging.Path)
                    ? Path.Combine(Path.GetDirectoryName(_configPath) ?? AppDomain.CurrentDomain.BaseDirectory, "tictack.log")
                    : cfg.Logging.Path;
                var loggers = new List<ILogger>();
                loggers.Add(new FileLogger(logPath, level, cfg.Logging.MaxSizeMb, cfg.Logging.MaxFiles));
                if (cfg.Logging.Console)
                    loggers.Add(new ConsoleLogger(level));
                loggers.Add(new DesktopAlertLogger(LogLevel.Warn, cfg.Logging.AlertPath));
            loggers.Add(new EventLogLogger());
                _log = new MultiLogger(loggers);

                PowerGuard.Cleanup(cfg, _log);

                if (!Config.Validate(cfg, _log))
                    throw new InvalidOperationException("Invalid configuration");

                _log.Info("TicTack Service starting");

                _pipelines = new List<SyncPipeline>();
                foreach (var src in cfg.Sources)
                {
                    var pipeline = Program.BuildPipeline(src, cfg, _log);
                    pipeline.Start();
                    _pipelines.Add(pipeline);
                }

                _scheduler = new TimerScheduler(cfg, _log);
                _scheduler.Start();

                if (cfg.Watchdog != null && cfg.Watchdog.Enabled)
                {
                    var ms = Math.Max(60000, cfg.Watchdog.IntervalMinutes * 60000);
                    _heartbeat = new Timer(_ => _log!.Info("[HEARTBEAT] Service running"), null, ms, ms);
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
                try { Stop(); } catch { }
            }
        }

        protected override void OnStop()
        {
            if (_log != null) _log.Info("TicTack Service stopping");
            if (_heartbeat != null) _heartbeat.Dispose();
            if (_scheduler != null) _scheduler.Dispose();
            if (_pipelines != null) foreach (var p in _pipelines) p.Dispose();
            if (_log != null) _log.Info("TicTack Service stopped");
        }

        protected override void OnShutdown()
        {
            OnStop();
        }
    }
}
