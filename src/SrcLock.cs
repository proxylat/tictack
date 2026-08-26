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
                    if (File.Exists(path))
                    {
                        var content = File.ReadAllText(path);
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                        if (age.TotalMinutes < 5)
                        {
                            if (DateTime.UtcNow >= deadline)
                            {
                                log.Warn("Lock held by " + content + " — retry timeout, continuing without it");
                                return;
                            }
                            log.Debug("Lock held by " + content + " — retrying...");
                            Thread.Sleep(5000);
                            continue;
                        }
                        File.Delete(path);
                    }

                    File.WriteAllText(path, _identity);
                    Thread.Sleep(200);
                    if (File.ReadAllText(path) != _identity)
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            File.Delete(path);
                            log.Warn("Lock stolen — retry timeout, continuing without it");
                            return;
                        }
                        log.Debug("Lock stolen — retrying...");
                        Thread.Sleep(5000);
                        continue;
                    }

                    break;
                }

                IsHeld = true;
                _refreshTimer = new Timer(_ =>
                {
                    try { File.SetLastWriteTimeUtc(_path, DateTime.UtcNow); }
                    catch { }
                }, null, 30000, 30000);
            }
            catch (Exception ex)
            {
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
                    if (File.Exists(_path) && File.ReadAllText(_path) == _identity)
                        File.Delete(_path);
                }
                catch { }
            }
        }
    }
}
