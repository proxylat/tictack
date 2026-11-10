using System;
using System.Collections.Generic;
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
        private readonly int _ownPid;
        private readonly ILogger _log;
        private readonly Timer? _refreshTimer;
        private readonly string? _heldPath;
        private FileStream? _handle;
        private bool _disposed;
        private int _refreshFailures;

        public bool IsHeld { get; private set; }

        // Identities of the holders that blocked a failed acquisition ("" when
        // none or when the attempt succeeded). Lets a best-effort caller say
        // who is in the way instead of a bare "unavailable".
        public string BlockerSummary { get; private set; } = "";

        private readonly bool _quietFailure;

        public SrcLock(string path, ILogger log, TimeSpan? retryTimeout = null, TimeSpan? refreshInterval = null,
            LockMode mode = LockMode.Exclusive, bool quietFailure = false)
        {
            _path = path;
            _log = log;
            _quietFailure = quietFailure;
            _ownPid = Process.GetCurrentProcess().Id;
            _identity = Environment.MachineName + ":" + _ownPid;
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
                    // Touch through the held handle, not by path: the
                    // exclusive handle stays open with FileShare.Read, so a
                    // by-path touch hits a sharing violation on Windows
                    // (swallowed by the catch below → mtime frozen, holder
                    // looks stale). Shared mode closed its create stream,
                    // so by-path is safe there.
                    var handle = _handle;
                    var touch = held;
                    _refreshTimer = new Timer(_ =>
                    {
                        try
                        {
                            if (handle != null)
                                File.SetLastWriteTimeUtc(handle.SafeFileHandle, DateTime.UtcNow);
                            else
                                File.SetLastWriteTimeUtc(touch, DateTime.UtcNow);
                            Interlocked.Exchange(ref _refreshFailures, 0);
                        }
                        catch (Exception ex)
                        {
                            if (Interlocked.Increment(ref _refreshFailures) == 2)
                            {
                                IsHeld = false;
                                _log.Warn("Lock refresh failed twice, running unprotected: " + touch + " (holder " + _identity + "): " + ex.Message);
                            }
                            else _log.Debug("Lock refresh failed: " + ex.Message);
                        }
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
                if (LiveRosterCount(staleAfter, out var holders) > 0)
                {
                    BlockerSummary = holders.Count > 0 ? string.Join(", ", holders) : "unknown shared holder";
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
                    // Self-ghost fast path: only our own STALE orphan. A live
                    // same-process holder (undisposed SrcLock in this process)
                    // is a real blocker, not an orphan.
                    if (IsSelf(content) && age >= staleAfter)
                    {
                        try { File.Delete(_path); _log.Debug("Self orphan lock deleted: " + _path + " (" + content + ")"); continue; }
                        catch (Exception ex) { _log.Debug("Self orphan delete failed: " + _path + ": " + ex.Message); }
                    }
                    string? deleteError = null;
                    if (age >= staleAfter)
                    {
                        try { File.Delete(_path); continue; }
                        catch (Exception ex) { _log.Warn("Stale lock delete failed: " + _path + " (held by " + content + ", deleter " + _identity + "): " + ex.Message); deleteError = ex.Message; }
                    }
                    BlockerSummary = DescribeBlocker(content, age, staleAfter, deleteError);
                    if (!WaitOne("lock held by " + BlockerSummary, deadline, retryTimeout, ref waited, ref attempt))
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
                    BlockerSummary = holder;
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
                if (LiveExclusive(staleAfter, out var raceHolder))
                {
                    BlockerSummary = raceHolder;
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
                // Self-ghost fast path: only our own STALE orphan. A live
                // same-process holder is a real blocker — report it.
                if (IsSelf(holder) && age >= staleAfter)
                {
                    try { File.Delete(_path); }
                    catch (Exception ex) { _log.Debug("Self orphan delete failed: " + _path + ": " + ex.Message); return true; }
                    _log.Debug("Self orphan lock deleted: " + _path + " (" + holder + ")");
                    return false;
                }
                if (age >= staleAfter)
                {
                    try { File.Delete(_path); } catch (Exception ex) { _log.Warn("Stale lock delete failed: " + _path + " (held by " + holder + ", deleter " + _identity + "): " + ex.Message); }
                    return false;
                }
                holder = DescribeBlocker(holder, age, staleAfter);
                return true;
            }
            catch { return false; }
        }

        private int LiveRosterCount(TimeSpan staleAfter, out List<string> holders)
        {
            holders = new List<string>();
            var count = 0;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
                foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(_path) + ".shared.*"))
                {
                    try
                    {
                        var id = ReadIdentitySafe(f);
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                        // Self-ghost fast path: only our own STALE orphan
                        // roster. A live same-process roster is a real blocker.
                        if (IsSelf(id) && age >= staleAfter)
                        {
                            try { File.Delete(f); _log.Debug("Self orphan roster deleted: " + f + " (" + id + ")"); }
                            catch (Exception ex) { _log.Debug("Self orphan roster delete failed: " + f + ": " + ex.Message); holders.Add(DescribeBlocker(id, age, staleAfter, ex.Message)); count++; }
                            continue;
                        }
                        if (age >= staleAfter)
                        {
                            try { File.Delete(f); continue; }
                            catch (Exception ex) { _log.Warn("Stale roster delete failed: " + f + " (deleter " + _identity + "): " + ex.Message); holders.Add(DescribeBlocker(id, age, staleAfter, ex.Message)); count++; continue; }
                        }
                        holders.Add(DescribeBlocker(id, age, staleAfter));
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

        // Self-ghost test: blocker PID == our PID means our own orphan
        // (same process, earlier pipeline in this run). Strips any
        // " (age/verdict)" suffix BlockerSummary formatting appends.
        internal static bool IsSelfIdentity(string identity, int ownPid)
        {
            var pid = identity;
            var paren = pid.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0) pid = pid.Substring(0, paren);
            var colon = pid.LastIndexOf(':');
            if (colon >= 0) pid = pid.Substring(colon + 1);
            return int.TryParse(pid.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) && n == ownPid;
        }

        private bool IsSelf(string identity) => IsSelfIdentity(identity, _ownPid);

        // Sibling test: every named blocker is our own PID. Empty or
        // unparseable ("unknown") summaries are NOT siblings — Warn.
        internal bool AllBlockersSelf() => AllSelfIdentities(BlockerSummary, _ownPid);

        internal static bool AllSelfIdentities(string summary, int ownPid)
        {
            if (string.IsNullOrWhiteSpace(summary)) return false;
            var any = false;
            foreach (var part in summary.Split(','))
            {
                var entry = part.Trim();
                if (entry.Length == 0) continue;
                if (!IsSelfIdentity(entry, ownPid)) return false;
                any = true;
            }
            return any;
        }

        // Actionable blocker line: identity + file age + live/dead/stale
        // verdict + delete error if any. One line per blocker; entries
        // stay comma-joined in BlockerSummary (no commas emitted here).
        internal static string DescribeBlocker(string identity, TimeSpan age, TimeSpan staleAfter, string? deleteError = null)
        {
            string verdict;
            string? procName = null;
            if (age >= staleAfter)
            {
                verdict = "stale";
                procName = TryGetLocalProcessName(identity);
            }
            else if (TryGetLocalPid(identity, out var pid))
            {
                verdict = "live";
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    try { procName = proc.ProcessName; } catch { procName = null; }
                    DateTime startUtc;
                    try { startUtc = proc.StartTime.ToUniversalTime(); }
                    catch { startUtc = DateTime.MinValue; }
                    if (startUtc != DateTime.MinValue && startUtc > (DateTime.UtcNow - age).AddSeconds(2))
                        verdict = "recycled";
                }
                catch { verdict = "dead"; procName = null; }
            }
            else verdict = IsPidDead(identity) ? "dead" : "live";
            var desc = identity + " (age " + FormatAge(age) + ", " + verdict + ")";
            if (procName != null) desc += " [" + procName.Replace(',', '_') + "]";
            if (!string.IsNullOrEmpty(deleteError)) desc += " [delete failed: " + deleteError + "]";
            return desc;
        }

        // Local-PID parse: strips any " (...)" display suffix, requires the
        // machine prefix (when present) to match this machine. Remote,
        // unparseable, or missing PIDs return false.
        private static bool TryGetLocalPid(string identity, out int pid)
        {
            pid = 0;
            try
            {
                var s = identity;
                var paren = s.IndexOf(" (", StringComparison.Ordinal);
                if (paren >= 0) s = s.Substring(0, paren);
                var colon = s.LastIndexOf(':');
                string pidPart;
                if (colon >= 0)
                {
                    var machine = s.Substring(0, colon).Trim();
                    if (!string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
                    pidPart = s.Substring(colon + 1);
                }
                else pidPart = s;
                return int.TryParse(pidPart.Trim(), System.Globalization.CultureInfo.InvariantCulture, out pid);
            }
            catch { return false; }
        }

        private static string? TryGetLocalProcessName(string identity)
        {
            if (!TryGetLocalPid(identity, out var pid)) return null;
            try { using var proc = Process.GetProcessById(pid); return proc.ProcessName; }
            catch { return null; }
        }

        internal static string FormatAge(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return ((int)age.TotalSeconds) + "s";
            if (age.TotalHours < 1) return ((int)age.TotalMinutes) + "m";
            if (age.TotalDays < 1) return ((int)age.TotalHours) + "h" + ((int)age.Minutes) + "m";
            return ((int)age.TotalDays) + "d" + ((int)age.Hours) + "h";
        }

        // Provably-dead test: the PID parses and GetProcessById throws (no
        // such process). Unparseable identities count as live (conservative).
        internal static bool IsPidDead(string identity)
        {
            var pid = identity;
            var paren = pid.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0) pid = pid.Substring(0, paren);
            var colon = pid.LastIndexOf(':');
            if (colon >= 0) pid = pid.Substring(colon + 1);
            if (!int.TryParse(pid.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
            try { Process.GetProcessById(n).Dispose(); return false; }
            catch { return true; }
        }

        // Boot takeover: delete lock/roster files whose holder PID is
        // provably dead, so the caller skips the wait budget. Live or
        // unparseable holders are left alone.
        internal static void TakeOverDead(string lockPath, ILogger log)
        {
            try
            {
                if (File.Exists(lockPath))
                {
                    string holder;
                    try { holder = ReadIdentity(lockPath); }
                    catch { holder = "unknown"; }
                    if (IsPidDead(holder))
                    {
                        try { File.Delete(lockPath); log.Debug("Boot takeover deleted dead holder lock: " + lockPath + " (" + holder + ")"); }
                        catch (Exception ex) { log.Debug("Boot takeover delete failed: " + lockPath + ": " + ex.Message); }
                    }
                }
                var dir = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(lockPath) + ".shared.*"))
                    {
                        string holder;
                        try { holder = ReadIdentity(f); }
                        catch { continue; }
                        if (!IsPidDead(holder)) continue;
                        try { File.Delete(f); log.Debug("Boot takeover deleted dead holder roster: " + f + " (" + holder + ")"); }
                        catch (Exception ex) { log.Debug("Boot takeover delete failed: " + f + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { log.Debug("Boot takeover sweep failed: " + ex.Message); }
        }

        // Shared wait body: false means the deadline passed (warned, give up).
        private bool WaitOne(string what, DateTime deadline, TimeSpan? retryTimeout, ref bool waited, ref int attempt)
        {
            if (DateTime.UtcNow >= deadline)
            {
                if (_quietFailure)
                {
                    // Best-effort callers (destructive sweeps) log their own
                    // skip line; a Warn here would just duplicate it.
                    _log.Debug("Lock unavailable (best-effort): held by " + what);
                }
                else
                {
                    var waitedFor = retryTimeout.HasValue ? $" after {retryTimeout.Value.TotalSeconds:F0}s" : "";
                    _log.Warn("Lock held by " + what + " — lock acquisition failed" + waitedFor
                              + "; stop the other TicTack instance or raise lock_wait_seconds");
                }
                TicTackEventSource.Log.LockTimedOut();
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
            TicTackEventSource.Log.LockWaited();
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
                    if (_heldPath != null && File.Exists(_heldPath)) { File.Delete(_heldPath); }
                }
                catch (Exception ex) { _log.Warn("Lock release failed: " + _heldPath + " (holder " + _identity + "): " + ex.Message); }
            }
        }
    }
}
