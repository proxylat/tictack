using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
        public void Error(string msg, Exception ex = null)
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
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public void Debug(string msg) { if (_minLevel <= LogLevel.Debug) Write("DBG", msg); }
        public void Info(string msg) { if (_minLevel <= LogLevel.Info) Write("INF", msg); }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) Write("WRN", msg); }
        public void Error(string msg, Exception ex = null)
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

    public class DesktopAlertLogger : ILogger
    {
        private readonly LogLevel _minLevel;
        public DesktopAlertLogger(LogLevel minLevel = LogLevel.Warn, string alertPath = null)
        {
            _minLevel = minLevel;
            if (!string.IsNullOrEmpty(alertPath))
                DesktopAlert.Configure(alertPath);
        }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) DesktopAlert.Write("WARN", msg); }
        public void Error(string msg, Exception ex = null) { if (_minLevel <= LogLevel.Error) DesktopAlert.Write("ERROR", msg, ex); }
    }

    public class EventLogLogger : ILogger
    {
        private readonly LogLevel _minLevel;
        public EventLogLogger(LogLevel minLevel = LogLevel.Error) { _minLevel = minLevel; }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg, Exception ex = null)
        {
            if (_minLevel > LogLevel.Error) return;
            try { EventLog.WriteEntry("TicTackSv", msg + (ex != null ? "\n" + ex : ""), EventLogEntryType.Error); }
            catch { }
        }
    }

    public class MultiLogger : ILogger
    {
        private readonly ILogger[] _loggers;
        public MultiLogger(IEnumerable<ILogger> loggers)
        {
            _loggers = loggers != null ? loggers.ToArray() : new ILogger[0];
        }

        public void Debug(string msg) { foreach (var l in _loggers) l.Debug(msg); }
        public void Info(string msg) { foreach (var l in _loggers) l.Info(msg); }
        public void Warn(string msg) { foreach (var l in _loggers) l.Warn(msg); }
        public void Error(string msg, Exception ex = null) { foreach (var l in _loggers) l.Error(msg, ex); }
    }

    public static class LogLevelParser
    {
        public static LogLevel Parse(string level, LogLevel defaultLevel = LogLevel.Info)
        {
            switch (level != null ? level.ToLowerInvariant() : null)
            {
                case "debug": return LogLevel.Debug;
                case "info": return LogLevel.Info;
                case "warn": case "warning": return LogLevel.Warn;
                case "error": return LogLevel.Error;
                default: return defaultLevel;
            }
        }
    }
}
