using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    // File-level OS capture + bootloader restore primitives.
    // Boundary with corpus item 5: corpus owns capture ORCHESTRATION (what/when,
    // scheduling, chunk index, dedup); TicTack owns only the file-level MECHANISM
    // here (locked-tolerant read via the caller's IFileAccessor, atomic
    // temp+rename write). No scheduling, no chunking, no CLI in this file.
    public static class OsCapture
    {
        // Bootloader-critical paths, volume-relative, '/'-separated,
        // case-insensitive. Windows: BCD store + bootmgr + winload/winresume;
        // Linux: kernel/initrd/grub under /boot plus the EFI system partition.
        public static bool IsBootFile(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            var rel = relativePath.Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith("efi/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("boot/", StringComparison.OrdinalIgnoreCase)
                || rel.Equals("bootmgr", StringComparison.OrdinalIgnoreCase))
                return true;
            if (rel.StartsWith("windows/system32/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(rel);
                if (name.StartsWith("winload", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("winresume", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Capture one live OS file through the caller's accessor: FileAccessor
        // for direct locked reads, VssFileAccessor for snapshot fallback on
        // Windows (same pattern as CopyAction). Atomic temp+rename with the
        // shared .tictack.tmp suffix so PowerGuard recovers crash orphans.
        public static async Task CaptureAsync(IFileAccessor accessor, string sourceFile, string captureFile, CancellationToken ct)
        {
            using var src = accessor.OpenRead(sourceFile);
            await StreamToAtomicAsync(src, captureFile, ct);
        }

        // Restore one file from capture to its live path. Capture files are
        // plain (never locked), so no accessor/VSS is needed on this leg.
        public static async Task RestoreAsync(string captureFile, string destFile, CancellationToken ct)
        {
            using var src = File.OpenRead(captureFile);
            await StreamToAtomicAsync(src, destFile, ct);
        }

        private static async Task StreamToAtomicAsync(Stream src, string destFile, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = destFile + ".tictack.tmp";
            using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, 81920, ct);
                dst.Flush(true);
            }
            if (File.Exists(destFile))
            {
                File.SetAttributes(destFile, FileAttributes.Normal);
                File.Replace(tmp, destFile, null);
            }
            else
            {
                File.Move(tmp, destFile);
            }
        }
    }
}
