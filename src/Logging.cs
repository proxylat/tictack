using System.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
            Console.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] [" + level + "] " + msg);
            Console.ForegroundColor = orig;
        }
    }

    public class FileLogger : ILogger, IDisposable
    {
        private readonly string _logPath;
        private readonly LogLevel _minLevel;
        private readonly long _maxSizeBytes;
        private readonly int _maxFiles;
        private readonly object _lock = new object();
        // Repeat-suppression: identical consecutive lines collapse to one
        // "(repeated Nx)" line. A storm of the same warning must not rotate
        // real history out of the file. First occurrence always writes, so a
        // crash mid-streak loses only the tail count.
        private string _streakKey = "";
        private string _streakLevel = "";
        private string _streakMsg = "";
        private long _streakCount;
        private DateTime _streakAt;
        private const int StreakFlushSeconds = 60;
        // Held-open handle: one append stream per file instead of
        // open-append-close per line. Flush piggybacks on Write (no thread).
        private FileStream? _stream;
        private StreamWriter? _writer;
        private long _baseLength; // on-disk length when the handle was opened
        private long _unflushedBytes; // buffered since open; logical len = base + this
        private DateTime _lastFlush = DateTime.UtcNow;
        private const int FlushIntervalSeconds = 4;
        private const long FlushThresholdBytes = 128 * 1024;

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
            var now = DateTime.Now;
            lock (_lock)
            {
                var key = level + "\u0000" + msg;
                if (key == _streakKey)
                {
                    _streakCount++;
                    // Long incident: emit a heartbeat so the log still shows
                    // it is happening, then keep collapsing.
                    if ((now - _streakAt).TotalSeconds >= StreakFlushSeconds)
                    {
                        EmitLocked(level, msg + " (repeated " + _streakCount + "x, ongoing)");
                        _streakAt = now;
                        _streakCount = 0;
                    }
                    return;
                }
                FlushStreakLocked();
                _streakKey = key;
                _streakLevel = level;
                _streakMsg = msg;
                _streakCount = 0;
                _streakAt = now;
                EmitLocked(level, msg);
            }
        }

        // Caller holds the lock. Closes a run of identical lines with its count.
        private void FlushStreakLocked()
        {
            if (_streakCount > 0)
                EmitLocked(_streakLevel, _streakMsg + " (repeated " + _streakCount + "x)");
            _streakCount = 0;
            _streakKey = "";
        }

        private void EmitLocked(string level, string msg)
        {
            try
            {
                EnsureOpenLocked();
                // Rotation trigger identical to before (on-disk length > max),
                // tracked in memory so no stat/flush syscall per line. Only
                // this class rotates, so base + buffered is the true length.
                if (_baseLength + _unflushedBytes > _maxSizeBytes)
                {
                    FlushStreamLocked();
                    CloseStreamLocked();
                    Rotate();
                    EnsureOpenLocked();
                }
                var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] [" + level + "] " + msg + Environment.NewLine;
                _writer!.Write(line);
                _unflushedBytes += Encoding.UTF8.GetByteCount(line);
                if (_unflushedBytes >= FlushThresholdBytes
                    || (DateTime.UtcNow - _lastFlush).TotalSeconds >= FlushIntervalSeconds)
                    FlushStreamLocked();
            }
            catch { }
        }

        // Caller holds the lock. Opens the append handle lazily so a
        // never-written logger creates no file, and reopens after
        // rotation/Dispose. FileShare.ReadWrite|Delete keeps tail/readers working.
        private void EnsureOpenLocked()
        {
            if (_writer != null) return;
            long len = 0;
            try { if (File.Exists(_logPath)) len = new FileInfo(_logPath).Length; } catch { }
            _stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(_stream, Encoding.UTF8);
            _baseLength = len;
            _unflushedBytes = 0;
            _lastFlush = DateTime.UtcNow;
        }

        // Caller holds the lock.
        private void FlushStreamLocked()
        {
            try
            {
                _writer?.Flush();
                _unflushedBytes = 0;
                _lastFlush = DateTime.UtcNow;
            }
            catch { }
        }

        // Caller holds the lock. Close (without flush) so rotation/dispose
        // can move the file; next Emit reopens lazily.
        private void CloseStreamLocked()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _stream = null;
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

        public void Dispose()
        {
            lock (_lock)
            {
                FlushStreakLocked();
                FlushStreamLocked();
                CloseStreamLocked();
            }
            // No disposed flag: a Write after Dispose reopens lazily.
        }
    }

    // Daily-rotated backup log: one file per calendar day, the previous day
    // kept as "<path>.1" (~48h window). Time bounds the window, not line rate,
    // so no formatting/size storm can erode it the way size rotation can.
    public sealed class DailyBackupLogger : ILogger
    {
        private readonly string _path;
        private readonly LogLevel _minLevel;
        private readonly object _lock = new object();
        private string _day;

        public DailyBackupLogger(string path, LogLevel minLevel = LogLevel.Info)
        {
            _path = path;
            _minLevel = minLevel;
            _day = LastWriteDay(path);
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
            if (_minLevel > LogLevel.Error) return;
            Write("ERR", msg);
            if (ex != null) Write("ERR", ex.ToString());
        }

        private void Write(string level, string msg)
        {
            var now = DateTime.Now;
            lock (_lock)
            {
                try
                {
                    var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (today != _day)
                    {
                        var prev = _path + ".1";
                        if (File.Exists(prev)) File.Delete(prev);
                        if (File.Exists(_path)) File.Move(_path, prev);
                        _day = today;
                    }
                    File.AppendAllText(_path,
                        "[" + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] [" + level + "] " + msg + Environment.NewLine);
                }
                catch { }
            }
        }

        private static string LastWriteDay(string path)
        {
            try { if (File.Exists(path)) return File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch { }
            return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
    // Primitive params (not the app config model) so Corpus can share it.
    internal static class LoggerFactory
    {
        public static ILogger Create(string? levelName, string logPath, int maxSizeMb, int maxFiles,
            string? alertPath, bool console, bool eventLog, string? backupPath = null)
        {
            var level = LogLevelParser.Parse(levelName);
            var loggers = new List<ILogger>
            {
                new BufferedLogger(new FileLogger(logPath, level, maxSizeMb, maxFiles))
            };
            // Second, time-bounded copy next to the state DBs so a size storm
            // (which cycles the size-rotated primary in minutes) still leaves
            // ~48h of history behind.
            if (!string.IsNullOrEmpty(backupPath))
                loggers.Add(new DailyBackupLogger(backupPath, level));
            if (console) loggers.Add(new ConsoleLogger(level));
            // Alerts are a Warn/Error channel per the documented contract.
            loggers.Add(new DesktopAlertLogger(LogLevel.Warn, alertPath));
            if (eventLog) loggers.Add(new EventLogLogger());
            var log = new MultiLogger(loggers);
            if (!string.IsNullOrEmpty(levelName) && !LogLevelParser.IsValid(levelName))
                log.Warn("Unknown logging.level '" + levelName + "', using " + level);
            return log;
        }
    }
}
