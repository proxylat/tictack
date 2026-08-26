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

        public DeferredDeletion(string dbPath, int holdDays, ILogger log)
        {
            _dbPath = dbPath;
            _holdDays = holdDays;
            _log = log;
            Load();
        }

        public void RecordPending(List<string> files, long totalSizeBytes)
        {
            lock (_lock)
            {
                if (_state == null)
                    _state = new DeferredState();

                _state.PendingFiles = files;
                _state.TotalSizeBytes = totalSizeBytes;
                _state.BlockedAt = DateTime.UtcNow;
                _state.LastWarningAt = DateTime.MinValue;
                Save();
                _log.Warn("Deferred deletion: " + files.Count + " files, hold for " + _holdDays + " days");
                LogSample(files);
            }
        }

        public DeferredAction Check()
        {
            lock (_lock)
            {
                if (_state == null || _state.PendingFiles == null || _state.PendingFiles.Count == 0)
                    return DeferredAction.None;

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
                    return DeferredAction.Waiting;
                }

                var filesStillDeleted = new List<string>();
                foreach (var f in _state.PendingFiles)
                {
                    if (!File.Exists(f))
                        filesStillDeleted.Add(f);
                }

                var result = new DeferredAction
                {
                    Type = filesStillDeleted.Count > 0 ? DeferredActionType.Proceed : DeferredActionType.Cancel,
                    Files = filesStillDeleted,
                    TotalSizeBytes = _state.TotalSizeBytes
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
            catch { _state = null; }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_state ?? new DeferredState(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dbPath, json);
            }
            catch { }
        }

        private class DeferredState
        {
            public DateTime BlockedAt { get; set; }
            public DateTime LastWarningAt { get; set; }
            public List<string>? PendingFiles { get; set; }
            public long TotalSizeBytes { get; set; }
        }
    }

    public class DeferredAction
    {
        public DeferredActionType Type { get; set; }
        public List<string>? Files { get; set; }
        public long TotalSizeBytes { get; set; }

        public static DeferredAction None = new DeferredAction { Type = DeferredActionType.None };
        public static DeferredAction Waiting = new DeferredAction { Type = DeferredActionType.Waiting };
    }

    public enum DeferredActionType
    {
        None,
        Waiting,
        Proceed,
        Cancel
    }
}
