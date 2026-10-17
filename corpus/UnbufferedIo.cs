using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Corpus
{
    // §12.12 unbuffered capture I/O: bulk extent-stream reads bypass the page
    // cache (no double-copy, no evicting the guest working set mid-backup).
    // Precedent: partclone --read-direct-io. Default ON; --buffered-io opts
    // out through the useUnbuffered seam (here plus the FileBlockSource /
    // VssBlockSource / DiskSnapshotSource / EspLiveSource ctors and the
    // DiskCapture.BuildPlan factory). Alignment (sector-multiple offsets,
    // lengths, AND buffer addresses — the NO_BUFFERING/O_DIRECT contract) is
    // handled ONCE in UnbufferedReadStream: callers keep issuing ordinary
    // arbitrary Read/Seek calls. MFT-record and bitmap IOCTL reads stay
    // buffered — this touches the extent stream only.
    // Linux: O_DIRECT via libc open()/pread(); anything that rejects the flag
    // falls back to an honest buffered FileStream behind the same API —
    // byte-identical either way. 4096 covers 512e + 4Kn (both divide it);
    // larger physical sectors don't exist on supported media.
    internal static class ExtentStreams
    {
        internal const int SectorBytes = 4096;
        private const int BufBytes = 1024 * 1024;

        // Extent-stream open: the single seam every IBlockSource bulk read
        // flows through. useUnbuffered=false is the --buffered-io escape.
        public static Stream OpenExtentRead(string path, bool useUnbuffered = true)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (!useUnbuffered)
                return OpenBuffered(path);
            if (OperatingSystem.IsWindows())
                return OpenUnbuffered(path, withFlag: true);
            return OpenLinux(path);
        }

        // Deterministic alignment-path seam for tests: the aligned wrapper
        // over a plain handle, so misalignment handling pins on every OS
        // even where the kernel flag falls back to buffered.
        internal static Stream OpenAlignedBuffered(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return OpenUnbuffered(path, withFlag: false);
        }

        private static Stream OpenUnbuffered(string path, bool withFlag)
        {
            var length = ReadableLength(path);
            var handle = OperatingSystem.IsWindows() ? OpenWindows(path, withFlag) : OpenPlainLinux(path);
            try
            {
                var stream = new UnbufferedReadStream(handle, length);
                if (!withFlag || OperatingSystem.IsWindows() || stream.Probe())
                    return stream;
                stream.Dispose();
                return OpenBuffered(path);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        // Linux: O_DIRECT attempt with a 1-sector probe (open may succeed
        // where I/O still rejects the flag); any refusal falls back loud-
        // equivalent buffered. A missing file still throws FileNotFound from
        // the fallback open — never masked.
        private static Stream OpenLinux(string path)
        {
            try
            {
                var length = new FileInfo(path).Length;
                var handle = OpenDirectLinux(path);
                try
                {
                    var stream = new UnbufferedReadStream(handle, length);
                    if (stream.Probe())
                        return stream;
                    stream.Dispose();
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                return OpenBuffered(path);
            }
            catch (DllNotFoundException)
            {
                return OpenBuffered(path);
            }
            catch (EntryPointNotFoundException)
            {
                return OpenBuffered(path);
            }
            catch (IOException)
            {
                return OpenBuffered(path);
            }
        }

        private static Stream OpenBuffered(string path)
            => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Regular files report via FileInfo; Windows device paths
        // (\\.\ / \\?\) fail that and report via IOCTL length instead.
        private static long ReadableLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
                    return NativeDisk.GetLengthBytes(path);
                throw;
            }
        }

        private static SafeFileHandle OpenWindows(string path, bool withFlag)
        {
            const uint GenericRead = 0x80000000;
            const uint ShareAll = 0x00000007; // read|write|delete — matches the old FileStream share
            const uint OpenExisting = 3;
            const uint FlagNoBuffering = 0x20000000;
            const uint FlagSequential = 0x08000000;
            var flags = withFlag ? FlagNoBuffering | FlagSequential : FlagSequential;
            var raw = CreateFile(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (raw == new IntPtr(-1))
                throw new IOException($"cannot open {path} (win32 {Marshal.GetLastWin32Error()})");
            return new SafeFileHandle(raw, true);
        }

        private static SafeFileHandle OpenPlainLinux(string path)
        {
            var fd = LibcOpen(path, 0, 0);
            if (fd < 0)
                throw new IOException($"cannot open {path} (errno {Marshal.GetLastSystemError()})");
            return new SafeFileHandle(new IntPtr(fd), true);
        }

        private static SafeFileHandle OpenDirectLinux(string path)
        {
            const int ODirect = 0x4000;
            var fd = LibcOpen(path, ODirect, 0);
            if (fd < 0)
                throw new IOException($"O_DIRECT open refused for {path} (errno {Marshal.GetLastSystemError()})");
            return new SafeFileHandle(new IntPtr(fd), true);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFileRaw(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SeekFileRaw(IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

        [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
        private static extern int LibcOpen([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags, int mode);

        [DllImport("libc.so.6", EntryPoint = "pread", SetLastError = true)]
        internal static extern nint LibcPread(int fd, IntPtr buf, nuint count, long offset);
    }

    // Seekable read-only stream over a raw handle that satisfies the
    // unbuffered contract internally: every raw read starts at a
    // sector-aligned offset, spans a sector multiple, and lands in a
    // sector-aligned native buffer; callers' arbitrary slices are served
    // from it via memcpy. Short raw reads happen only at the tail.
    internal sealed class UnbufferedReadStream : Stream
    {
        private const int SectorBytes = ExtentStreams.SectorBytes;
        private const int BufBytes = 1024 * 1024;

        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private readonly AlignedBuffer _buffer;
        private long _pos;
        private int _lastError;
        private bool _disposed;

        internal UnbufferedReadStream(SafeFileHandle handle, long length)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            _handle = handle;
            _length = length;
            _buffer = new AlignedBuffer(BufBytes, SectorBytes);
        }

        // Sector-aligned native scratch behind a SafeHandle (frees even if the
        // stream is never disposed — no user finalizer, per analyzer rule).
        private sealed class AlignedBuffer : SafeHandleZeroOrMinusOneIsInvalid
        {
            private readonly IntPtr _base;

            public IntPtr Aligned { get; }

            public AlignedBuffer(int size, int align)
                : base(true)
            {
                _base = Marshal.AllocHGlobal(size + align);
                var off = (int)((align - (_base.ToInt64() % align)) % align);
                Aligned = _base + off;
                SetHandle(Aligned);
            }

            protected override bool ReleaseHandle()
            {
                Marshal.FreeHGlobal(_base);
                return true;
            }
        }

        // One positioned raw read; -1 on hard error (errno/win32 in
        // _lastError), short only at the tail. EINTR retries on Linux.
        private int ReadAt(long offset, IntPtr dst, int count)
        {
            if (OperatingSystem.IsWindows())
            {
                if (!ExtentStreams.SeekFileRaw(_handle.DangerousGetHandle(), offset, out _, 0))
                {
                    _lastError = Marshal.GetLastWin32Error();
                    return -1;
                }
                var got = 0;
                while (got < count)
                {
                    if (!ExtentStreams.ReadFileRaw(_handle.DangerousGetHandle(), dst + got, count - got, out var n, IntPtr.Zero))
                    {
                        _lastError = Marshal.GetLastWin32Error();
                        return -1;
                    }
                    if (n == 0)
                        break;
                    got += n;
                }
                return got;
            }
            var fd = unchecked((int)_handle.DangerousGetHandle().ToInt64());
            var total = 0;
            while (total < count)
            {
                var n = (long)ExtentStreams.LibcPread(fd, dst + total, (nuint)(count - total), offset + total);
                if (n < 0)
                {
                    var err = Marshal.GetLastSystemError();
                    if (err == 4)
                        continue; // EINTR
                    _lastError = err;
                    return -1;
                }
                if (n == 0)
                    break;
                total += (int)n;
            }
            return total;
        }

        // O_DIRECT validity check: open may accept the flag where I/O
        // rejects it — one sector decides before the caller commits.
        internal bool Probe()
        {
            if (_length == 0)
                return true;
            return ReadAt(0, _buffer.Aligned, SectorBytes) >= 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _pos;
            set => _pos = value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            return _pos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
                throw new ArgumentException("offset/count exceed buffer", nameof(count));
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count == 0 || _pos >= _length)
                return 0;
            if (_pos < 0)
                throw new IOException("read before start of stream");
            count = (int)Math.Min((long)count, _length - _pos);
            var total = 0;
            while (total < count)
            {
                var rawOff = (_pos / SectorBytes) * SectorBytes;
                var skip = (int)(_pos - rawOff);
                var take = Math.Min(count - total, BufBytes - skip);
                var rawLen = ((skip + take + SectorBytes - 1) / SectorBytes) * SectorBytes;
                var got = ReadAt(rawOff, _buffer.Aligned, rawLen);
                if (got < 0)
                    throw new IOException($"unbuffered read failed at {rawOff} (code {_lastError})");
                var avail = Math.Min(take, got - skip);
                if (avail <= 0)
                    break;
                Marshal.Copy(_buffer.Aligned + skip, buffer, offset + total, avail);
                _pos += avail;
                total += avail;
                if (avail < take)
                    break; // short tail — next read is EOF
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _buffer.Dispose();
                if (disposing)
                    _handle.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
