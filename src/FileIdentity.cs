using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TicTack
{
    // Stable file identity for move pairing (#2) and the hardened skip key
    // (#6): the filesystem's own file number plus its metadata-change time.
    // Both come from one handle open, so capture costs a single P/Invoke
    // pair per COPY (the rare path) — never on the warm-scan skip path.
    // Returns false when identity is unavailable (FAT32, access denied,
    // unknown libc layout): callers fall back to the old behavior.
    internal static class FileIdentity
    {
        public static bool TryGet(string path, out string? fileId, out long ctimeTicks)
        {
            fileId = null;
            ctimeTicks = 0;
            try
            {
                if (OperatingSystem.IsWindows())
                    return TryGetWindows(path, out fileId, out ctimeTicks);
                return TryGetLinux(path, out fileId, out ctimeTicks);
            }
            catch { return false; }
        }

        // Windows: MFT file index (survives renames) + ChangeTime (moves
        // with any metadata edit; writers cannot preserve it like mtime).
        private static bool TryGetWindows(string path, out string? fileId, out long ctimeTicks)
        {
            fileId = null;
            ctimeTicks = 0;
            var handle = CreateFile(PathUtil.EnsureExtended(path), FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,
                OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle == InvalidHandle) return false;
            try
            {
                if (!GetFileInformationByHandle(handle, out var info)) return false;
                var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                if (index == 0) return false;
                if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var basic, FileBasicInfoSize))
                    return false;
                fileId = "W" + index.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
                ctimeTicks = basic.ChangeTime;
                return true;
            }
            finally { CloseHandle(handle); }
        }

        // Linux: (device, inode) + ctime. glibc stat layout; anything else
        // (musl, macOS) is caught by the size cross-check below, which
        // rejects the result instead of trusting shifted fields.
        private static bool TryGetLinux(string path, out string? fileId, out long ctimeTicks)
        {
            fileId = null;
            ctimeTicks = 0;
            if (stat(path, out var buf) != 0 || buf.Ino == 0) return false;
            long length;
            try { length = new FileInfo(path).Length; }
            catch { return false; }
            if (buf.Size != length) return false;
            fileId = "L" + buf.Dev.ToString("x", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + buf.Ino.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            ctimeTicks = DateTime.UnixEpoch.Ticks + buf.CtimeSec * 10_000_000L + buf.CtimeNsec / 100L;
            return true;
        }

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;
        private const uint FileShareDelete = 4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const int FileBasicInfoClass = 0;
        private const int FileBasicInfoSize = 40;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleInfo
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileBasicInfo
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
            private uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long TvSec;
            public long TvNsec;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StatBuffer
        {
            public ulong Dev;
            public ulong Ino;
            public ulong Nlink;
            public uint Mode;
            public uint Uid;
            public uint Gid;
            private int Pad0;
            public ulong Rdev;
            public long Size;
            public long Blksize;
            public long Blocks;
            public Timespec Atime;
            public Timespec Mtime;
            public Timespec Ctime;
            // __glibc_reserved[3]: native stat writes 144 bytes. A 120-byte
            // buffer lets the last 24 smash the stack — observed as a
            // NullReferenceException on the next innocent line.
            private long Reserved0;
            private long Reserved1;
            private long Reserved2;

            public long CtimeSec => Ctime.TvSec;
            public long CtimeNsec => Ctime.TvNsec;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleInfo lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int infoClass,
            out FileBasicInfo lpFileInformation, int dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        // glibc only: the struct layout below is glibc's. musl/macOS have
        // no libc.so.6, so the load fails and callers take the fallback.
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int stat(string pathname, out StatBuffer buf);
    }
}
