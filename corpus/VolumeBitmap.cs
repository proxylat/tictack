using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Corpus
{
    // Used-cluster map (§5 rung 3): FSCTL_GET_VOLUME_BITMAP over a volume or
    // snapshot device, in bounded 1M-cluster windows (128 KiB bitmap each —
    // never a whole-volume alloc). Parse/append are pure for Linux tests.
    public static class VolumeBitmap
    {
        private const uint FsctlGetVolumeBitmap = 0x9006F;
        private const uint IoctlDiskGetLengthInfo = 0x7405C;
        private const int WindowClusters = 1024 * 1024;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        // Cluster size from the live root (identical on its snapshots).
        public static long BytesPerCluster(string root)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("volume bitmap needs Windows");
            if (!GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _))
                throw new IOException("GetDiskFreeSpaceW(" + root + ") failed: " + Marshal.GetLastWin32Error());
            return (long)spc * bps;
        }

        // Total volume bytes (64-bit: the W variant's cluster count overflows
        // past 16 TiB at 4 KiB clusters).
        public static long TotalBytes(string root)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("volume bitmap needs Windows");
            if (!GetDiskFreeSpaceExW(root, out _, out var total, out _))
                throw new IOException("GetDiskFreeSpaceExW(" + root + ") failed: " + Marshal.GetLastWin32Error());
            return (long)total;
        }

        // Exact byte length of a volume/snapshot device (partition extent).
        public static long GetLengthBytes(string devicePath)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("volume length needs Windows");
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var outBuf = new byte[8];
            if (!DeviceIoControl(fs.SafeFileHandle, IoctlDiskGetLengthInfo, null, 0, outBuf, 8, out var ret, IntPtr.Zero) || ret != 8)
                throw new IOException("IOCTL_DISK_GET_LENGTH_INFO(" + devicePath + ") failed: " + Marshal.GetLastWin32Error());
            return BitConverter.ToInt64(outBuf, 0);
        }

        // Merged allocated runs as (startLcn, clusterCount), ascending.
        public static List<(long Start, long Count)> ReadRuns(string devicePath, long totalClusters)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("volume bitmap needs Windows");
            var runs = new List<(long, long)>();
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var outBuf = new byte[16 + WindowClusters / 8];
            var lcn = 0L;
            while (lcn < totalClusters)
            {
                var inBuf = BitConverter.GetBytes(lcn);
                if (!DeviceIoControl(fs.SafeFileHandle, FsctlGetVolumeBitmap, inBuf, 8, outBuf, outBuf.Length, out var ret, IntPtr.Zero))
                    throw new IOException($"FSCTL_GET_VOLUME_BITMAP @LCN {lcn} failed: {Marshal.GetLastWin32Error()}");
                var start = BitConverter.ToInt64(outBuf, 0);
                var bits = BitConverter.ToInt64(outBuf, 8);
                if (bits == 0) break;
                AppendRuns(runs, start, outBuf, 16, ret - 16, bits);
                lcn = start + bits;
            }
            return runs;
        }

        // Pure bit→run parser: buf holds bitmap bytes for validBits clusters
        // starting at startLcn, LSB-first per NTFS.
        public static List<(long Start, long Count)> ParseRuns(long startLcn, byte[] buf, int validBits)
        {
            var runs = new List<(long, long)>();
            AppendRuns(runs, startLcn, buf, 0, buf.Length, validBits);
            return runs;
        }

        // Windowed append: reopens the trailing run when the new window's
        // first bit continues it, so multi-window reads merge seamlessly.
        internal static void AppendRuns(List<(long Start, long Count)> runs, long startLcn, byte[] buf, int off, int len, long validBits)
        {
            var bits = (int)Math.Min(validBits, (long)len * 8);
            long curStart = -1, curLen = 0;
            if (runs.Count > 0)
            {
                var (ps, pc) = runs[runs.Count - 1];
                if (ps + pc == startLcn && bits > 0 && (buf[off] & 1) != 0)
                {
                    runs.RemoveAt(runs.Count - 1);
                    curStart = ps;
                    curLen = pc;
                }
            }
            for (var i = 0; i < bits; i++)
            {
                var set = (buf[off + i / 8] & (1 << (i % 8))) != 0;
                if (set)
                {
                    if (curStart < 0) { curStart = startLcn + i; curLen = 0; }
                    curLen++;
                }
                else if (curStart >= 0)
                {
                    runs.Add((curStart, curLen));
                    curStart = -1;
                    curLen = 0;
                }
            }
            if (curStart >= 0) runs.Add((curStart, curLen));
        }
    }
}
