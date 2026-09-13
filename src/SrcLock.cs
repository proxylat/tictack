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
        private readonly Timer? _refreshTimer;
        private FileStream? _handle;
        private bool _disposed;

        public bool IsHeld { get; private set; }

        public SrcLock(string path, ILogger log, TimeSpan? retryTimeout = null)
        {
            _path = path;
            _identity = Environment.MachineName + ":" + Process.GetCurrentProcess().Id;
            var deadline = retryTimeout.HasValue ? DateTime.UtcNow + retryTimeout.Value : DateTime.MinValue;

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
                        try { content = File.ReadAllText(path); } catch { }
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                        if (age.TotalMinutes >= 5)
                        {
                            try { File.Delete(path); continue; } catch { }
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            log.Warn("Lock held by " + content + " — lock acquisition failed");
                            return;
                        }
                        log.Debug("Lock held by " + content + " — retrying...");
                        Thread.Sleep(5000);
                    }
                }

                _refreshTimer = new Timer(_ =>
                {
                    try { File.SetLastWriteTimeUtc(_path, DateTime.UtcNow); }
                    catch { }
                }, null, 30000, 30000);
            }
            catch (Exception ex)
            {
                _handle?.Dispose();
                _handle = null;
                log.Warn("Lock init failed: " + ex.Message);
            }
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
                catch { }
            }
        }
    }
}
