using System;
using System.IO;

namespace TicTack
{
    // Destination free-space pre-check + non-retryable disk-full classifier
    // shared by TicTack and Corpus.
    internal static class DiskSpaceGuard
    {
        private const int Win32ErrorDiskFull = 112;       // 0x70
        private const int Win32ErrorHandleDiskFull = 39;  // 0x27
        private const int LinuxEnospc = 28;

        public static bool IsDiskFull(Exception? ex)
        {
            while (ex != null)
            {
                if (ex is IOException io)
                {
                    int hr = io.HResult;
                    int low = hr & 0xFFFF;
                    if (OperatingSystem.IsWindows() && (low == Win32ErrorDiskFull || low == Win32ErrorHandleDiskFull))
                        return true;
                    if (!OperatingSystem.IsWindows() && (hr == LinuxEnospc || low == LinuxEnospc))
                        return true;
                }
                ex = ex.InnerException;
            }
            return false;
        }

        public static bool TryGetAvailableBytes(string path, out long availableBytes)
        {
            availableBytes = 0;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                availableBytes = new DriveInfo(root).AvailableFreeSpace;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static long GbToBytes(double? gb) =>
            gb.HasValue && gb.Value > 0 && !double.IsNaN(gb.Value)
                ? (long)(gb.Value * 1024.0 * 1024.0 * 1024.0)
                : 0L;

        public static bool HasHeadroom(
            string destPath,
            long requiredBytes,
            long minFreeBytes,
            out long availableBytes,
            Func<string, long>? freeSpaceOverride = null)
        {
            availableBytes = 0;
            if (minFreeBytes <= 0)
                return true;

            if (freeSpaceOverride != null)
            {
                availableBytes = freeSpaceOverride(destPath);
            }
            else if (!TryGetAvailableBytes(destPath, out availableBytes))
            {
                // Unknown drive (e.g. UNC without DriveInfo) — don't falsely block.
                return true;
            }

            long needed = Math.Max(0L, requiredBytes) + minFreeBytes;
            return availableBytes >= needed;
        }
    }
}
