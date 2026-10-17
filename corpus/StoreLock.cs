using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using TicTack;

namespace Corpus
{
    // Writer lock for the store root (§12.9). Mirrors TicTack SrcLock
    // semantics: exclusive lock file, heartbeat refresh, stale-steal via
    // PID-liveness + heartbeat age, backoff waits. Readers stay lock-free;
    // this API is writer-oriented (image/gc/optimize/prune call sites).
    //
    // Crash safety: the lock is a held-open exclusive handle released on
    // Dispose. A crash leaves the file behind with a frozen heartbeat, so
    // the next writer steals it (dead pid, or heartbeat older than the
    // stale timeout). The store never wedges.
    public sealed class StoreLockException : IOException
    {
        public StoreLockException(string message) : base(message) { }
    }

    public sealed class StoreLock : IDisposable
    {
        public const string LockFileName = "store.lock";

        private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultRefresh = TimeSpan.FromSeconds(30);

        private readonly string _path;
        private readonly string _identity;
        private readonly ILogger _log;
        private readonly Timer? _heartbeat;
        private FileStream? _handle;
        private bool _disposed;

        public string LockPath => _path;

        private StoreLock(string path, string identity, FileStream handle, ILogger log, TimeSpan refresh)
        {
            _path = path;
            _identity = identity;
            _handle = handle;
            _log = log;
            var held = handle;
            // Touch through the held handle, not by path (SrcLock rule: the
            // exclusive handle stays open, so a by-path touch hits a sharing
            // violation on Windows and the heartbeat silently freezes).
            _heartbeat = new Timer(_ =>
            {
                try { File.SetLastWriteTimeUtc(held.SafeFileHandle, DateTime.UtcNow); }
                catch (Exception ex) { log.Debug("Store lock heartbeat failed: " + ex.Message); }
            }, null, refresh, refresh);
        }

        // retryTimeout null (or <= 0) = fail loud immediately on contention.
        // Otherwise waits with backoff sleeps up to the duration, then throws
        // the same loud failure. Throws StoreLockException either way.
        public static StoreLock Acquire(string storeRoot, TimeSpan? retryTimeout, ILogger log,
            TimeSpan? staleAfter = null, TimeSpan? refreshInterval = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(storeRoot);
            ArgumentNullException.ThrowIfNull(log);
            var stale = staleAfter ?? DefaultStaleAfter;
            if (stale <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(staleAfter), "stale timeout must be positive");
            var refresh = refreshInterval ?? DefaultRefresh;
            if (refresh <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(refreshInterval), "refresh interval must be positive");

            var path = Path.Combine(storeRoot, LockFileName);
            var identity = BuildIdentity();
            var budget = retryTimeout.HasValue && retryTimeout.Value > TimeSpan.Zero
                ? retryTimeout.Value
                : TimeSpan.Zero;
            var deadline = budget > TimeSpan.Zero ? DateTime.UtcNow + budget : DateTime.MinValue;
            var waited = false;
            var attempt = 0;
            while (true)
            {
                if (TryCreate(path, identity, out var handle))
                    return new StoreLock(path, identity, handle!, log, refresh);
                // Lost the create race or the file is held. A vanished file
                // (steal/delete beat us here) is a retry, never a failure.
                var holder = ReadHolderSafe(path, log);
                if (holder == null)
                    continue;
                var age = AgeSafe(path);
                if (age == null)
                    continue;
                var live = IsLive(holder, age.Value, stale);
                if (!live && TrySteal(path, holder, log))
                    continue;
                // Live holder (or a steal blocked by the holder's open
                // handle on Windows): wait for it, or fail loud.
                var message = ContentionMessage(path, holder, budget);
                if (DateTime.UtcNow >= deadline)
                    throw new StoreLockException(message);
                attempt++;
                if (!waited)
                {
                    waited = true;
                    log.Info(message + $" — waiting up to {budget.TotalSeconds:F0}s for the store lock (another writer is running)");
                }
                else
                {
                    log.Debug(message + " — retrying...");
                }
                var waitMs = NextWaitMs(attempt);
                var remainingMs = (long)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                    throw new StoreLockException(message);
                Thread.Sleep((int)Math.Min(waitMs, remainingMs));
            }
        }

        // --retry-lock duration: Ns / Nm / Nh, bare number = seconds.
        // Garbage refuses loud (StoreLockException) for the CLI to report.
        public static TimeSpan ParseRetryTimeout(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw Refuse(raw);
            var t = raw.Trim();
            var i = 0;
            while (i < t.Length && t[i] >= '0' && t[i] <= '9')
                i++;
            if (i == 0)
                throw Refuse(raw);
            if (!long.TryParse(t.Substring(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw Refuse(raw);
            var suffix = t.Substring(i).ToLowerInvariant();
            try
            {
                return suffix switch
                {
                    "" => TimeSpan.FromSeconds(n),
                    "s" => TimeSpan.FromSeconds(n),
                    "m" => TimeSpan.FromMinutes(n),
                    "h" => TimeSpan.FromHours(n),
                    _ => throw Refuse(raw),
                };
            }
            catch (OverflowException)
            {
                throw Refuse(raw);
            }
        }

        private static StoreLockException Refuse(string? raw) =>
            new($"invalid --retry-lock duration: '{raw}' (want Ns, Nm, Nh, or bare seconds, e.g. 90, 10m, 2h)");

        private static string ContentionMessage(string path, string holder, TimeSpan budget)
        {
            var who = TryParseIdentity(holder, out var pid, out _, out var since)
                ? $"store locked by pid {pid} since {since}"
                : $"store locked by unknown holder '{holder}'";
            var waited = budget > TimeSpan.Zero ? $" (waited {budget.TotalSeconds:F0}s)" : "";
            return $"{who} ({path}){waited}";
        }

        private static string BuildIdentity() =>
            $"pid={Process.GetCurrentProcess().Id} host={Environment.MachineName} since={DateTime.UtcNow:o} id={Guid.NewGuid():N}";

        private static bool TryCreate(string path, string identity, out FileStream? handle)
        {
            handle = null;
            try
            {
                handle = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
                using (var writer = new StreamWriter(handle, leaveOpen: true))
                    writer.Write(identity);
                handle.Flush(true);
                return true;
            }
            catch (DirectoryNotFoundException ex)
            {
                handle?.Dispose();
                handle = null;
                throw new StoreLockException($"store root not found: {Path.GetDirectoryName(path)} ({ex.Message})");
            }
            catch (IOException)
            {
                handle?.Dispose();
                handle = null;
                // CreateNew fails when the path exists; anything else (a torn
                // steal can leave it untrue) just retries the create above.
                return false;
            }
        }

        // Null = file vanished mid-race (retry the create). Empty/unreadable
        // = contested but opaque (fail or wait naming an unknown holder).
        private static string? ReadHolderSafe(string path, ILogger log)
        {
            try
            {
                // The holder keeps Write access, so readers must share Write
                // (SrcLock rule — default readers get a sharing violation).
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex)
            {
                log.Debug("Store lock read failed: " + ex.Message);
                return "";
            }
        }

        private static TimeSpan? AgeSafe(string path)
        {
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path); }
            catch { return null; }
        }

        // Stale = heartbeat dead (age past the timeout — covers crash and
        // kill -9), or the holder pid is gone on this box. Anything we
        // cannot verify (foreign host, opaque file) counts as live:
        // stealing what we cannot see risks two writers.
        internal static bool IsLive(string holder, TimeSpan age, TimeSpan stale)
        {
            if (age >= stale)
                return false;
            if (!TryParseIdentity(holder, out var pid, out var host, out _))
                return true;
            if (pid <= 0)
                return false;
            if (!host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (pid == Process.GetCurrentProcess().Id)
                return true; // fresh own-pid file: a live re-entrant attempt, never steal
            return IsPidAlive(pid);
        }

        private static bool IsPidAlive(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool TrySteal(string path, string holder, ILogger log)
        {
            try
            {
                File.Delete(path);
                log.Info(ContentionMessage(path, holder, TimeSpan.Zero) + " — stale, stealing the store lock");
                return true;
            }
            catch (Exception ex)
            {
                // Windows: the live holder's open handle blocks the delete.
                log.Debug("Store lock steal failed: " + ex.Message);
                return false;
            }
        }

        internal static bool TryParseIdentity(string holder, out int pid, out string host, out string since)
        {
            pid = 0;
            host = "";
            since = "";
            try
            {
                foreach (var token in holder.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith("pid=", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(token.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
                            return false;
                    }
                    else if (token.StartsWith("host=", StringComparison.Ordinal))
                    {
                        host = token.Substring(5);
                    }
                    else if (token.StartsWith("since=", StringComparison.Ordinal))
                    {
                        since = token.Substring(6);
                    }
                }
                return pid != 0 && host.Length > 0 && since.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // SrcLock backoff ladder (2s→60s cap).
        internal static int NextWaitMs(int attempt) => attempt switch
        {
            <= 1 => 2000,
            2 => 5000,
            3 => 15000,
            4 => 30000,
            _ => 60000,
        };

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _heartbeat?.Dispose();
            var handle = _handle;
            _handle = null;
            if (handle == null)
                return;
            try
            {
                handle.Dispose();
                // Delete only our own file: a stealer may own the path now.
                if (ReadHolderSafe(_path, _log) == _identity && File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex) { _log.Debug("Store lock release failed: " + ex.Message); }
        }
    }
}
