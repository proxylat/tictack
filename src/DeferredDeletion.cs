using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TicTack
{
    public sealed class DeferredDeletion
    {
        private readonly string _dbPath;
        private readonly int _holdDays;
        private readonly ILogger _log;
        private readonly object _lock = new object();
        private DeferredState? _state;
        // Windows paths are case-insensitive; Linux is not, so 'a.txt' and
        // 'A.txt' must both stay in the pending set there.
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public DeferredDeletion(string dbPath, int holdDays, ILogger log)
        {
            _dbPath = dbPath;
            _holdDays = holdDays;
            _log = log;
            Load();
        }

        public void RecordPending(IEnumerable<string> files, string? sourceRoot = null)
        {
            lock (_lock)
            {
                if (_state == null)
                    _state = new DeferredState();

                // Union with any batch still held: replacing it would silently
                // drop the earlier deletions, which would then never be applied.
                if (_state.PendingFiles == null)
                    _state.PendingFiles = new List<string>();
                var pending = _state.PendingFiles;
                var wasEmpty = pending.Count == 0;
                var seen = new HashSet<string>(pending, PathComparer);
                var added = new List<string>();
                foreach (var f in files)
                {
                    if (seen.Add(f))
                    {
                        pending.Add(f);
                        added.Add(f);
                    }
                }

                _state.SourceRoot = sourceRoot;
                // Keep the earliest hold start: resetting it on every blocked
                // batch would restart the clock for already-aged entries.
                if (wasEmpty)
                {
                    _state.BlockedAt = DateTime.UtcNow;
                    _state.LastWarningAt = DateTime.MinValue;
                }
                Save();
                _log.Warn("Deferred deletion: " + pending.Count + " files, hold for " + _holdDays + " days");
                if (added.Count > 0) LogSample(added);
            }
        }

        public DeferredAction Check()
        {
            lock (_lock)
            {
                if (_state == null || _state.PendingFiles == null || _state.PendingFiles.Count == 0)
                    return new DeferredAction { Type = DeferredActionType.None };

                var elapsed = (DateTime.UtcNow - _state.BlockedAt).TotalDays;

                if (elapsed < _holdDays)
                {
                    if ((DateTime.UtcNow - _state.LastWarningAt).TotalDays >= 1)
                    {
                        var remaining = _holdDays - (int)elapsed;
                        _log.Warn("Deferred deletion: " + _state.PendingFiles.Count + " files pending, " + remaining + " day(s) remaining (full list: " + _dbPath + ")");
                        _state.LastWarningAt = DateTime.UtcNow;
                        Save();
                    }
                    return new DeferredAction { Type = DeferredActionType.Waiting };
                }

                var filesStillDeleted = new List<string>();
                if (!string.IsNullOrEmpty(_state.SourceRoot) && !Directory.Exists(_state.SourceRoot))
                {
                    // Same 1/day throttle as the hold branch: the pipeline
                    // rechecks hourly and must not warn every hour.
                    if ((DateTime.UtcNow - _state.LastWarningAt).TotalDays >= 1)
                    {
                        _log.Warn("Deferred deletion: source unavailable, holding pending deletions");
                        _state.LastWarningAt = DateTime.UtcNow;
                        Save();
                    }
                    return new DeferredAction { Type = DeferredActionType.Waiting };
                }
                foreach (var f in _state.PendingFiles)
                {
                    if (!File.Exists(f))
                        filesStillDeleted.Add(f);
                }

                var result = new DeferredAction
                {
                    Type = filesStillDeleted.Count > 0 ? DeferredActionType.Proceed : DeferredActionType.Cancel,
                    Files = filesStillDeleted
                };

                if (filesStillDeleted.Count > 0)
                {
                    _log.Warn("Deferred deletion: " + filesStillDeleted.Count + " files still deleted after hold, syncing");
                    LogSample(filesStillDeleted);
                }
                else
                {
                    _log.Info("Deferred deletion: files reappeared, cancelling");
                }

                _state = null;
                Save();
                return result;
            }
        }

        public bool HasPending
        {
            get { lock (_lock) { return _state != null && _state.PendingFiles != null && _state.PendingFiles.Count > 0; } }
        }

        private void LogSample(List<string> files)
        {
            var shown = files.Take(20).ToList();
            var msg = string.Join(Environment.NewLine, shown.Select(f => "  " + f));
            if (files.Count > shown.Count)
                msg += Environment.NewLine + "  ... and " + (files.Count - shown.Count) + " more";
            msg += Environment.NewLine + "Full list: " + _dbPath;
            _log.Warn(msg);
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    var json = File.ReadAllText(_dbPath);
                    _state = JsonSerializer.Deserialize<DeferredState>(json);
                }
            }
            catch { _log.Warn("Deferred deletion state could not be loaded; preserving it for recovery: " + _dbPath); }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_state ?? new DeferredState(), new JsonSerializerOptions { WriteIndented = true });
                var temp = _dbPath + ".tictack.tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temp, _dbPath, true);
            }
            catch (Exception ex) { _log.Warn("Deferred deletion state could not be saved: " + _dbPath + " (" + ex.Message + ")"); }
        }

        private class DeferredState
        {
            public DateTime BlockedAt { get; set; }
            public DateTime LastWarningAt { get; set; }
            public List<string>? PendingFiles { get; set; }
            public string? SourceRoot { get; set; }
        }
    }

    public class DeferredAction
    {
        public DeferredActionType Type { get; set; }
        public List<string>? Files { get; set; }
    }

    public enum DeferredActionType
    {
        None,
        Waiting,
        Proceed,
        Cancel
    }
}
