using System;
using System.Collections.Generic;
using System.IO;

namespace TicTack
{
    // Sliding-window anomaly brake. Copies, deletes, and move-replays feed
    // (bytes, files, renames) into a time window; any single configured wire
    // tripping freezes all three funnels until a human clears it. Clearing is
    // file-is-truth: the user deletes the marker file, the next enforcement
    // check sees it gone and resumes with an Info line. No UI, no RPC.
    // Config keys are spike_*; the class/marker names keep the old word so
    // pre-rename holds survive an upgrade.
    public sealed class ChurnMonitor
    {
        private readonly string _markerPath;
        private readonly TimeSpan _window;
        private readonly long _bytesTrip;
        private readonly int _filesTrip;
        private readonly int _renamesTrip;
        private readonly ILogger _log;
        private readonly Func<DateTime> _clock;
        private readonly Func<bool>? _isExempt;
        private readonly object _lock = new object();
        private readonly Queue<(DateTime At, long Bytes, int Files, int Renames)> _events = new Queue<(DateTime, long, int, int)>();
        private bool _holdLogged;

        public ChurnMonitor(string markerPath, TimeSpan window, long bytesTrip, int filesTrip, int renamesTrip,
            ILogger log, Func<DateTime>? clock = null, Func<bool>? isExempt = null)
        {
            _markerPath = markerPath;
            _window = window;
            _bytesTrip = bytesTrip;
            _filesTrip = filesTrip;
            _renamesTrip = renamesTrip;
            _log = log;
            _clock = clock ?? (() => DateTime.UtcNow);
            _isExempt = isExempt;
        }

        public void ReportCopy(long bytes)
        {
            lock (_lock) Add(bytes, 1, 0);
        }

        public void ReportDelete()
        {
            lock (_lock) Add(0, 1, 0);
        }

        public void ReportRename()
        {
            lock (_lock) Add(0, 0, 1);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _events.Clear();
                _holdLogged = false;
            }
        }

        public void ReportBulkDelete(long bytes, int files)
        {
            if (files <= 0) return;
            lock (_lock) Add(bytes, files, 0);
        }

        private bool IsExempt()
        {
            try { return _isExempt != null && _isExempt(); }
            catch { return false; }
        }

        private void Add(long bytes, int files, int renames)
        {
            if (IsExempt()) return;
            var now = _clock();
            _events.Enqueue((now, bytes, files, renames));
            Prune(now);
            long b = 0;
            long f = 0;
            long r = 0;
            foreach (var e in _events)
            {
                b += e.Bytes;
                f += e.Files;
                r += e.Renames;
            }
            if ((_bytesTrip > 0 && b >= _bytesTrip)
                || (_filesTrip > 0 && f >= _filesTrip)
                || (_renamesTrip > 0 && r >= _renamesTrip))
                Trip(now, b, f, r);
        }

        private void Prune(DateTime now)
        {
            while (_events.Count > 0 && (now - _events.Peek().At) > _window)
                _events.Dequeue();
        }

        private void Trip(DateTime now, long bytes, long files, long renames)
        {
            try
            {
                var dir = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_markerPath,
                    "Spike guard tripped at " + now.ToString("O") + ": "
                    + bytes + " bytes, " + files + " files, " + renames + " renames in window. "
                    + "Delete this file to resume syncing.");
            }
            catch (Exception ex) { _log.Debug("Spike marker write failed: " + ex.Message); }
            if (!_holdLogged)
            {
                _holdLogged = true;
                _log.Error("Spike guard tripped (" + bytes + " bytes, " + files + " files, " + renames
                    + " renames in window) — syncing frozen until " + _markerPath + " is deleted");
            }
        }

        // True while the hold is armed. Marker deleted externally = cleared.
        public bool IsHeld()
        {
            if (IsExempt()) return false;
            lock (_lock)
            {
                bool held;
                try { held = File.Exists(_markerPath); }
                catch { held = true; }
                if (!held && _holdLogged)
                {
                    _holdLogged = false;
                    Reset();
                    _log.Info("Spike hold cleared, resuming");
                }
                return held;
            }
        }
    }
}
