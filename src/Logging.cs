using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TicTack
{
    public enum LogLevel { Debug, Info, Warn, Error }

    public class ConsoleLogger : ILogger
    {
        private readonly LogLevel _minLevel;
        public ConsoleLogger(LogLevel minLevel = LogLevel.Info) { _minLevel = minLevel; }

        public void Debug(string msg) { if (_minLevel <= LogLevel.Debug) Write("DBG", msg, ConsoleColor.Gray); }
        public void Info(string msg) { if (_minLevel <= LogLevel.Info) Write("INF", msg, ConsoleColor.White); }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) Write("WRN", msg, ConsoleColor.Yellow); }
        public void Error(string msg, Exception? ex = null)
        {
            if (_minLevel <= LogLevel.Error)
            {
                Write("ERR", msg, ConsoleColor.Red);
                if (ex != null) Write("ERR", ex.ToString(), ConsoleColor.DarkRed);
            }
        }

        private static void Write(string level, string msg, ConsoleColor color)
        {
            var orig = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + msg);
            Console.ForegroundColor = orig;
        }
    }

    public class FileLogger : ILogger
    {
        private readonly string _logPath;
        private readonly LogLevel _minLevel;
        private readonly long _maxSizeBytes;
        private readonly int _maxFiles;
        private readonly object _lock = new object();

        public FileLogger(string path, LogLevel minLevel = LogLevel.Info, long maxSizeMb = 10, int maxFiles = 5)
        {
            _logPath = path;
            _minLevel = minLevel;
            _maxSizeBytes = maxSizeMb * 1024 * 1024;
            _maxFiles = Math.Max(1, maxFiles);
            var dir = Path.GetDirectoryName(path);
            try
            {
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public void Debug(string msg) { if (_minLevel <= LogLevel.Debug) Write("DBG", msg); }
        public void Info(string msg) { if (_minLevel <= LogLevel.Info) Write("INF", msg); }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) Write("WRN", msg); }
        public void Error(string msg, Exception? ex = null)
        {
            if (_minLevel <= LogLevel.Error)
            {
                Write("ERR", msg);
                if (ex != null) Write("ERR", ex.ToString());
            }
        }

        private void Write(string level, string msg)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + msg + Environment.NewLine;
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > _maxSizeBytes)
                        Rotate();
                    File.AppendAllText(_logPath, line);
                }
                catch { }
            }
        }

        private void Rotate()
        {
            for (int i = _maxFiles - 1; i >= 1; i--)
            {
                var oldF = _logPath + "." + i;
                var newF = _logPath + "." + (i + 1);
                if (File.Exists(oldF))
                {
                    if (File.Exists(newF)) File.Delete(newF);
                    File.Move(oldF, newF);
                }
            }
            var first = _logPath + ".1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(_logPath, first);
        }
    }

    public sealed class BufferedLogger : ILogger, IDisposable
    {
        private readonly ILogger _inner;
        private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        private readonly Task _writer;

        public BufferedLogger(ILogger inner)
        {
            _inner = inner;
            _writer = Task.Run(WriteLoopAsync);
        }

        public void Debug(string msg) => Enqueue(LogLevel.Debug, msg, null);
        public void Info(string msg) => Enqueue(LogLevel.Info, msg, null);
        public void Warn(string msg) => Enqueue(LogLevel.Warn, msg, null);
        public void Error(string msg, Exception? ex = null) => Enqueue(LogLevel.Error, msg, ex);

        private void Enqueue(LogLevel level, string msg, Exception? ex)
        {
            if (_queue.Writer.TryWrite(new Entry(level, msg, ex))) return;
            if (level >= LogLevel.Warn)
                Write(new Entry(level, msg, ex));
        }

        private async Task WriteLoopAsync()
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync())
                Write(entry);
        }

        private void Write(Entry entry)
        {
            switch (entry.Level)
            {
                case LogLevel.Debug: _inner.Debug(entry.Message); break;
                case LogLevel.Info: _inner.Info(entry.Message); break;
                case LogLevel.Warn: _inner.Warn(entry.Message); break;
                default: _inner.Error(entry.Message, entry.Exception); break;
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            try { _writer.GetAwaiter().GetResult(); } catch { }
            if (_inner is IDisposable disposable) disposable.Dispose();
        }

        private readonly record struct Entry(LogLevel Level, string Message, Exception? Exception);
    }

    public class DesktopAlertLogger : ILogger
    {
        private readonly LogLevel _minLevel;
        public DesktopAlertLogger(LogLevel minLevel = LogLevel.Warn, string? alertPath = null)
        {
            _minLevel = minLevel;
            if (!string.IsNullOrEmpty(alertPath))
                DesktopAlert.Configure(alertPath);
        }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) DesktopAlert.Write("WARN", msg); }
        public void Error(string msg, Exception? ex = null) { if (_minLevel <= LogLevel.Error) DesktopAlert.Write("ERROR", msg, ex); }
    }

    public class EventLogLogger : ILogger
    {
        private readonly LogLevel _minLevel;
        public EventLogLogger(LogLevel minLevel = LogLevel.Error) { _minLevel = minLevel; }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg, Exception? ex = null)
        {
            if (_minLevel > LogLevel.Error) return;
            try { EventLog.WriteEntry("TicTackSv", msg + (ex != null ? "\n" + ex : ""), EventLogEntryType.Error); }
            catch { }
        }
    }

    public class MultiLogger : ILogger, IDisposable
    {
        private readonly ILogger[] _loggers;
        public MultiLogger(IEnumerable<ILogger>? loggers)
        {
            _loggers = loggers != null ? loggers.ToArray() : Array.Empty<ILogger>();
        }

        public void Debug(string msg) { foreach (var l in _loggers) l.Debug(msg); }
        public void Info(string msg) { foreach (var l in _loggers) l.Info(msg); }
        public void Warn(string msg) { foreach (var l in _loggers) l.Warn(msg); }
        public void Error(string msg, Exception? ex = null) { foreach (var l in _loggers) l.Error(msg, ex); }
        public void Dispose()
        {
            foreach (var logger in _loggers)
                if (logger is IDisposable disposable) disposable.Dispose();
        }
    }

    public static class LogLevelParser
    {
        public static LogLevel Parse(string? level, LogLevel defaultLevel = LogLevel.Info) =>
            TryParse(level, out var parsed) ? parsed : defaultLevel;

        public static bool IsValid(string? level) => TryParse(level, out _);

        // Single token table: Parse and IsValid cannot drift.
        private static bool TryParse(string? level, out LogLevel result)
        {
            switch (level != null ? level.ToLowerInvariant() : null)
            {
                case "debug": result = LogLevel.Debug; return true;
                case "info": result = LogLevel.Info; return true;
                case "warn": case "warning": result = LogLevel.Warn; return true;
                case "error": result = LogLevel.Error; return true;
                default: result = LogLevel.Info; return false;
            }
        }
    }

    // Single owner for the logger stack: Program.Main and TicTackService had
    // drifted (desktop-alert level, default log directory, EventLog condition).
    internal static class LoggerFactory
    {
        public static ILogger Create(LoggingConfig cfg, string defaultLogDir, bool console, bool eventLog)
        {
            var level = LogLevelParser.Parse(cfg.Level);
            var logPath = string.IsNullOrEmpty(cfg.Path) ? Path.Combine(defaultLogDir, "tictack.log") : cfg.Path;
            var loggers = new List<ILogger>
            {
                new BufferedLogger(new FileLogger(logPath, level, cfg.MaxSizeMb, cfg.MaxFiles))
            };
            if (console) loggers.Add(new ConsoleLogger(level));
            // Alerts are a Warn/Error channel per the documented contract.
            loggers.Add(new DesktopAlertLogger(LogLevel.Warn, cfg.AlertPath));
            if (eventLog) loggers.Add(new EventLogLogger());
            var log = new MultiLogger(loggers);
            if (!string.IsNullOrEmpty(cfg.Level) && !LogLevelParser.IsValid(cfg.Level))
                log.Warn("Unknown logging.level '" + cfg.Level + "', using " + level);
            return log;
        }
    }
}
