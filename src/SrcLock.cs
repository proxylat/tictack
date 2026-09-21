using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TicTack
{
    public sealed class SrcLock : IDisposable
    {
        private readonly string _path;
        private readonly string _identity;
        private readonly ILogger _log;
        private readonly Timer? _refreshTimer;
        private FileStream? _handle;
        private bool _disposed;

        public bool IsHeld { get; private set; }

        public SrcLock(string path, ILogger log, TimeSpan? retryTimeout = null, TimeSpan? refreshInterval = null)
        {
            _path = path;
            _log = log;
            _identity = Environment.MachineName + ":" + Process.GetCurrentProcess().Id;
            var deadline = retryTimeout.HasValue ? DateTime.UtcNow + retryTimeout.Value : DateTime.MinValue;
            var refresh = refreshInterval ?? TimeSpan.FromSeconds(30);
            // Stale threshold must exceed the refresh cadence, or a live holder
            // whose refreshInterval is set above 5 minutes looks stale.
            var staleAfter = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, refresh.Ticks * 10));
            var waited = false;

            try
            {
                while (true)
                {
                    try
                    {
                        _handle = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
                        using (var writer = new StreamWriter(_handle, leaveOpen: true))
                            writer.Write(_identity);
                        _handle.Flush(true);
                        IsHeld = true;
                        break;
                    }
                    catch (IOException)
                    {
                        _handle?.Dispose();
                        _handle = null;
                        if (!File.Exists(path)) throw;
                        var content = "unknown";
                        try { content = ReadIdentity(path); }
                        catch (Exception ex) { log.Debug("Lock identity read failed: " + ex.Message); }
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                        if (age >= staleAfter)
                        {
                            try { File.Delete(path); continue; }
                            catch (Exception ex) { log.Debug("Stale lock delete failed: " + ex.Message); }
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            var waitedFor = retryTimeout.HasValue ? $" after {retryTimeout.Value.TotalMinutes:F0} min" : "";
                            log.Warn("Lock held by " + content + " — lock acquisition failed" + waitedFor
                                     + "; stop the other TicTack instance or raise retry_lock_minutes");
                            return;
                        }
                        if (!waited)
                        {
                            waited = true;
                            var budget = retryTimeout.HasValue ? $" for up to {retryTimeout.Value.TotalMinutes:F0} min" : "";
                            log.Info("Waiting for lock held by " + content + " — retrying every 5s" + budget
                                     + "; stop the running instance to proceed immediately");
                        }
                        else log.Debug("Lock held by " + content + " — retrying...");
                        Thread.Sleep(5000);
                    }
                }

                _refreshTimer = new Timer(_ =>
                {
                    try { File.SetLastWriteTimeUtc(_path, DateTime.UtcNow); }
                    catch (Exception ex) { _log.Debug("Lock refresh failed: " + ex.Message); }
                }, null, refresh, refresh);
            }
            catch (Exception ex)
            {
                _handle?.Dispose();
                _handle = null;
                log.Warn("Lock init failed: " + ex.Message);
            }
        }

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
                    if (File.Exists(_path)) File.Delete(_path);
                }
                catch (Exception ex) { _log.Debug("Lock release failed: " + ex.Message); }
            }
        }
    }
}
