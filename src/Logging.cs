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
            msg = LogLevelParser.Clean(msg);
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
        private int _emitFails; // consecutive Emit failures; first one alerts via Event Log
        private DateTime _lastFlush = DateTime.UtcNow;
        private const int FlushIntervalSeconds = 4;
        private const long FlushThresholdBytes = 128 * 1024;
        // No flush timer by design: delivery happens on the write path
        // (threshold/interval flush in EmitLocked) or at Dispose. A burst
        // followed by silence sits in buffers until the next line — stale
        // tail, never lost lines. Zero background threads in logging.

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
            msg = LogLevelParser.Clean(msg);
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
            {                EnsureOpenLocked();
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
                _emitFails = 0;
            }
            catch (Exception ex)
            {
                // A dead primary must scream through another channel: the
                // file channel itself can't report it. Once per outage
                // (counter resets on success) via the Event Log; the backup
                // logger keeps history in the meantime.
                if (_emitFails == 0)
                {
                    try { EventLog.WriteEntry("TicTackSv", "Primary log unwritable: " + _logPath + " (" + ex.Message + "). Backup log continues.", EventLogEntryType.Warning); }
                    catch { }
                }
                _emitFails++;
            }
        }

        // Caller holds the lock. Opens the append handle lazily so a
        // never-written logger creates no file, and reopens after
        // rotation/Dispose/external delete. FileShare.ReadWrite|Delete
        // keeps tail/readers working, but means a deleted path leaves the
        // handle orphaned (writes to it succeed silently — the OS reports
        // no error). So EVERY Emit stats existence first (~100ns cached,
        // no disk I/O): missing closes the orphan and the open below
        // recreates the file before the bytes land. Idle + deleted simply
        // stays absent until the next line, which then recreates it.
        private void EnsureOpenLocked()
        {
            if (_writer != null)
            {
                bool missing;
                try { missing = !File.Exists(_logPath); } catch { return; }
                if (!missing) return;
                CloseStreamLocked();
            }
            long len = 0;
            try { if (File.Exists(_logPath)) len = new FileInfo(_logPath).Length; } catch { }
            // Default 4KB FileStream buffer, no AutoFlush: per-line cost is
            // a memcpy, physical writes happen on buffer-full or flush
            // (threshold/interval in EmitLocked). Visibility is bounded by
            // the flush cadence, not by write-through.
            _stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(_stream, Encoding.UTF8);
            _baseLength = len;
            _unflushedBytes = 0;
            _lastFlush = DateTime.UtcNow;
        }

        // Caller holds the lock. Flush BOTH writers: StreamWriter.Flush
        // alone only moves bytes into the FileStream's own (4KB) buffer,
        // where small log volumes sat until Dispose — the primary looked
        // dead while the per-line open/close backup stayed complete.
        private void FlushStreamLocked()
        {
            try
            {
                _writer?.Flush();
                _stream?.Flush();
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
            msg = LogLevelParser.Clean(msg);
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
            // Always configure (null resets): otherwise a stale static path
            // from an earlier init would keep alerting after disable.
            DesktopAlert.Configure(alertPath);
        }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { if (_minLevel <= LogLevel.Warn) DesktopAlert.Write("WARN", LogLevelParser.Clean(msg)); }
        public void Error(string msg, Exception? ex = null) { if (_minLevel <= LogLevel.Error) DesktopAlert.Write("ERROR", LogLevelParser.Clean(msg), ex); }
    }

    public class EventLogLogger : ILogger
    {
        private const int LogDaemonWarn = (3 << 3) | 4;
        private const int LogDaemonErr = (3 << 3) | 3;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "syslog")]
        private static extern void LinuxSyslog(int priority, string format, string message);

        private readonly string _source;
        private readonly LogLevel _minLevel;
        public EventLogLogger(string source = "TicTackSv", LogLevel minLevel = LogLevel.Warn)
        {
            _source = string.IsNullOrEmpty(source) ? "TicTackSv" : source;
            _minLevel = minLevel;
        }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg)
        {
            if (_minLevel > LogLevel.Warn) return;
            var clean = LogLevelParser.Clean(msg);
            if (OperatingSystem.IsWindows())
            {
                try { EventLog.WriteEntry(_source, clean, EventLogEntryType.Warning); }
                catch { }
            }
            else if (OperatingSystem.IsLinux())
            {
                try { LinuxSyslog(LogDaemonWarn, "%s", _source + ": " + clean); }
                catch { }
            }
        }
        public void Error(string msg, Exception? ex = null)
        {
            if (_minLevel > LogLevel.Error) return;
            var body = LogLevelParser.Clean(msg) + (ex != null ? " " + LogLevelParser.Clean(ex.ToString()) : "");
            if (OperatingSystem.IsWindows())
            {
                try { EventLog.WriteEntry(_source, body, EventLogEntryType.Error); }
                catch { }
            }
            else if (OperatingSystem.IsLinux())
            {
                try { LinuxSyslog(LogDaemonErr, "%s", _source + ": " + body); }
                catch { }
            }
        }
    }

    public class MultiLogger : ILogger, IDisposable
    {
        private readonly ILogger[] _loggers;
        public MultiLogger(IEnumerable<ILogger>? loggers)
        {
            _loggers = loggers != null ? loggers.ToArray() : Array.Empty<ILogger>();
        }

        public void Debug(string msg) { var c = LogLevelParser.Clean(msg); foreach (var l in _loggers) l.Debug(c); }
        public void Info(string msg) { var c = LogLevelParser.Clean(msg); foreach (var l in _loggers) l.Info(c); }
        public void Warn(string msg) { var c = LogLevelParser.Clean(msg); foreach (var l in _loggers) l.Warn(c); }
        public void Error(string msg, Exception? ex = null) { var c = LogLevelParser.Clean(msg); foreach (var l in _loggers) l.Error(c, ex); }
        public void Dispose()
        {
            foreach (var logger in _loggers)
                if (logger is IDisposable disposable) disposable.Dispose();
        }
    }

    public static class LogLevelParser
    {
        internal static string Clean(string? msg) =>
            string.IsNullOrEmpty(msg) ? string.Empty : msg.Replace('\r', ' ').Replace('\n', ' ');

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
            string? alertPath, bool console, bool eventLog, string? backupPath = null, bool? alertEnabled = null,
            string eventLogSource = "TicTackSv", bool osNotifications = true, bool cmdPrompts = false,
            bool osPrompts = true, int alertTimeoutSeconds = 15)
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
            // alert_enabled: false is a hard off for .txt files even if alert_path is set.
            var effectiveAlertPath = alertEnabled == false ? null : alertPath;
            DesktopAlert.Configure(effectiveAlertPath, osNotifications, cmdPrompts, osPrompts, alertTimeoutSeconds);
            loggers.Add(new DesktopAlertLogger(LogLevel.Warn, effectiveAlertPath));
            if (eventLog) loggers.Add(new EventLogLogger(eventLogSource));
            var log = new MultiLogger(loggers);
            if (!string.IsNullOrEmpty(levelName) && !LogLevelParser.IsValid(levelName))
                log.Warn("Unknown logging.level '" + levelName + "', using " + level);
            return log;
        }
    }
}
