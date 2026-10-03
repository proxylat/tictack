using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TicTack
{
    public enum LockMode
    {
        // Single holder. Unchanged legacy behavior.
        Exclusive,
        // Roster file per holder; coexists with other Shared holders,
        // blocked by (and blocking) Exclusive.
        Shared,
    }

    public sealed class SrcLock : IDisposable
    {
        private readonly string _path;
        private readonly string _identity;
        private readonly ILogger _log;
        private readonly Timer? _refreshTimer;
        private readonly string? _heldPath;
        private FileStream? _handle;
        private bool _disposed;

        public bool IsHeld { get; private set; }

        public SrcLock(string path, ILogger log, TimeSpan? retryTimeout = null, TimeSpan? refreshInterval = null,
            LockMode mode = LockMode.Exclusive)
        {
            _path = path;
            _log = log;
            _identity = Environment.MachineName + ":" + Process.GetCurrentProcess().Id;
            var deadline = retryTimeout.HasValue ? DateTime.UtcNow + retryTimeout.Value : DateTime.MinValue;
            var refresh = refreshInterval ?? TimeSpan.FromSeconds(30);
            // Stale threshold must exceed the refresh cadence, or a live holder
            // whose refreshInterval is set above 5 minutes looks stale.
            var staleAfter = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, refresh.Ticks * 10));

            try
            {
                string? held = mode == LockMode.Shared
                    ? AcquireShared(deadline, staleAfter, retryTimeout)
                    : AcquireExclusive(deadline, staleAfter, retryTimeout);
                if (held != null)
                {
                    _heldPath = held;
                    IsHeld = true;
                    var touch = held;
                    _refreshTimer = new Timer(_ =>
                    {
                        try { File.SetLastWriteTimeUtc(touch, DateTime.UtcNow); }
                        catch (Exception ex) { _log.Debug("Lock refresh failed: " + ex.Message); }
                    }, null, refresh, refresh);
                }
            }
            catch (Exception ex)
            {
                _handle?.Dispose();
                _handle = null;
                log.Warn("Lock init failed: " + ex.Message);
            }
        }

        private string? AcquireExclusive(DateTime deadline, TimeSpan staleAfter, TimeSpan? retryTimeout)
        {
            var waited = false;
            var attempt = 0;
            while (true)
            {
                // A live shared roster blocks exclusive; stale ones are swept.
                if (LiveRosterCount(staleAfter) > 0)
                {
                    if (!WaitOne("shared holders", deadline, retryTimeout, ref waited, ref attempt))
                        return null;
                    continue;
                }
                try
                {
                    _handle = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
                    using (var writer = new StreamWriter(_handle, leaveOpen: true))
                        writer.Write(_identity);
                    _handle.Flush(true);
                    return _path;
                }
                catch (IOException)
                {
                    _handle?.Dispose();
                    _handle = null;
                    if (!File.Exists(_path)) throw;
                    var content = ReadIdentitySafe(_path);
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_path);
                    if (age >= staleAfter)
                    {
                        try { File.Delete(_path); continue; }
                        catch (Exception ex) { _log.Debug("Stale lock delete failed: " + ex.Message); }
                    }
                    if (!WaitOne("lock held by " + content, deadline, retryTimeout, ref waited, ref attempt))
                        return null;
                }
            }
        }

        private string? AcquireShared(DateTime deadline, TimeSpan staleAfter, TimeSpan? retryTimeout)
        {
            var waited = false;
            var attempt = 0;
            while (true)
            {
                if (LiveExclusive(staleAfter, out var holder))
                {
                    if (!WaitOne("exclusive lock held by " + holder, deadline, retryTimeout, ref waited, ref attempt))
                        return null;
                    continue;
                }
                var roster = NewRosterPath();
                try
                {
                    using (var stream = new FileStream(roster, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream))
                        writer.Write(_identity);
                    File.SetLastWriteTimeUtc(roster, DateTime.UtcNow);
                }
                catch (IOException)
                {
                    // Guid collision (or a torn delete); mint another name.
                    continue;
                }
                // Lost the race: an exclusive holder appeared between the
                // check and our create. Yield and retry.
                if (LiveExclusive(staleAfter, out _))
                {
                    try { File.Delete(roster); } catch { }
                    if (!WaitOne("exclusive lock", deadline, retryTimeout, ref waited, ref attempt))
                        return null;
                    continue;
                }
                return roster;
            }
        }

        // True when a non-stale exclusive lock file exists (sweeping a stale one).
        private bool LiveExclusive(TimeSpan staleAfter, out string holder)
        {
            holder = "unknown";
            try
            {
                if (!File.Exists(_path)) return false;
                holder = ReadIdentitySafe(_path);
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_path);
                if (age >= staleAfter)
                {
                    try { File.Delete(_path); } catch (Exception ex) { _log.Debug("Stale lock delete failed: " + ex.Message); }
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        private int LiveRosterCount(TimeSpan staleAfter)
        {
            var count = 0;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
                foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(_path) + ".shared.*"))
                {
                    try
                    {
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                        if (age >= staleAfter)
                        {
                            try { File.Delete(f); } catch { }
                            continue;
                        }
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        private string NewRosterPath()
        {
            var safe = _identity;
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return _path + ".shared." + safe + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private string ReadIdentitySafe(string path)
        {
            try { return ReadIdentity(path); }
            catch (Exception ex)
            {
                _log.Debug("Lock identity read failed: " + ex.Message);
                return "unknown";
            }
        }

        // Shared wait body: false means the deadline passed (warned, give up).
        private bool WaitOne(string what, DateTime deadline, TimeSpan? retryTimeout, ref bool waited, ref int attempt)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var waitedFor = retryTimeout.HasValue ? $" after {retryTimeout.Value.TotalSeconds:F0}s" : "";
                _log.Warn("Lock held by " + what + " — lock acquisition failed" + waitedFor
                          + "; stop the other TicTack instance or raise lock_wait_seconds");
                return false;
            }
            attempt++;
            if (!waited)
            {
                waited = true;
                var budget = retryTimeout.HasValue ? $" for up to {retryTimeout.Value.TotalSeconds:F0}s" : "";
                _log.Info("Waiting for lock held by " + what + " — retrying with backoff (2s→60s cap)" + budget
                          + "; stop the running instance to proceed immediately");
            }
            else _log.Debug("Lock held by " + what + " — retrying...");
            Thread.Sleep(NextLockWaitMs(attempt));
            return true;
        }

        internal static int NextLockWaitMs(int attempt) => attempt switch
        {
            <= 1 => 2000,
            2 => 5000,
            3 => 15000,
            4 => 30000,
            _ => 60000,
        };

        internal static string ReadIdentity(string path)
        {
            // The holder keeps Write access, so readers must share Write
            // (default FileShare.Read readers get a sharing violation on Windows).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _refreshTimer?.Dispose();
            if (IsHeld)
            {
                try
                {
                    _handle?.Dispose();
                    _handle = null;
                    if (_heldPath != null && File.Exists(_heldPath)) File.Delete(_heldPath);
                }
                catch (Exception ex) { _log.Debug("Lock release failed: " + ex.Message); }
            }
        }
    }
}
