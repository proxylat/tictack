using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TicTack
{
    public class ExponentialBackoffRetry
    {
        private readonly int _maxAttempts;
        private readonly int _initialDelayMs;
        private readonly double _backoff;
        private readonly ILogger? _log;

        public ExponentialBackoffRetry(int maxAttempts = 5, int initialDelayMs = 1000, double backoff = 2.0, ILogger? log = null)
        {
            _maxAttempts = Math.Max(1, maxAttempts);
            _initialDelayMs = Math.Max(0, initialDelayMs);
            _backoff = Math.Max(1.0, backoff);
            _log = log;
        }

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                try { return await action(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (attempt >= _maxAttempts) throw;
                    _log?.Debug("Retry " + attempt + "/" + _maxAttempts + " after failure: " + ex.Message);
                }
                // Cap the delay so a large user-configured backoff cannot overflow
                // the int narrowing and hand Task.Delay a negative value.
                var delay = (int)Math.Min(_initialDelayMs * Math.Pow(_backoff, attempt - 1), 60000);
                await Task.Delay(delay, ct);
            }
        }
    }

    public class FileAccessor : IFileAccessor
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        static extern int OpenFile(string path, int flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        const int O_RDONLY = 0;
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint FILE_SHARE_DELETE = 0x00000004;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x00000008;

        public Stream OpenRead(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Bypass managed Unix sharing locks so active files remain readable.
                var fd = OpenFile(path, O_RDONLY);
                if (fd < 0)
                    throw new IOException("Failed to open source file: " + path + " (errno " + Marshal.GetLastWin32Error() + ")");
                return new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read);
            }

            path = PathUtil.EnsureExtended(path);

            var rawHandle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            if (rawHandle == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new FileStream(new SafeFileHandle(rawHandle, true), FileAccess.Read);
        }
    }
}
