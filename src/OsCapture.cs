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

    // Item 11 wiring: tree-level capture/restore over the single-file
    // mechanism above + the CLI/scheduling surface (TicTack --os-capture /
    // --os-restore, invocable from jobs:). Boundary with corpus item 5:
    // TicTack restores FILES and prints the bootloader line; orchestrated
    // layout recreate + bootloader reinstall (diskpart/mountvol/firmware
    // checks) lives in corpus BootRepairOrchestrator and is never
    // reimplemented here.
    public static class OsCaptureRunner
    {
        // Whole-tree capture through the caller's accessor (direct or VSS).
        // Per-file failures warn-and-continue (one locked OS file must not
        // kill the run); the failed count fails the CLI exit code instead.
        public static async Task<(int Captured, int Failed)> CaptureTreeAsync(
            IFileAccessor accessor, string sourceDir, string captureDir, ILogger log, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(accessor);
            ArgumentNullException.ThrowIfNull(sourceDir);
            ArgumentNullException.ThrowIfNull(captureDir);
            ArgumentNullException.ThrowIfNull(log);
            var captured = 0;
            var failed = 0;
            var boot = 0;
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(sourceDir, file);
                if (OsCapture.IsBootFile(rel)) boot++;
                try
                {
                    await OsCapture.CaptureAsync(accessor, file, Path.Combine(captureDir, rel), ct);
                    captured++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.Warn("os-capture: skipping " + rel + ": " + ex.Message);
                }
            }
            log.Info($"os-capture: {captured} files captured ({boot} boot-critical), {failed} failed");
            return (captured, failed);
        }

        // Whole-tree restore from a capture. Ends with the bootloader line
        // for the operator (see the item-5 boundary above).
        public static async Task<(int Restored, int Failed)> RestoreTreeAsync(
            string captureDir, string destDir, ILogger log, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(captureDir);
            ArgumentNullException.ThrowIfNull(destDir);
            ArgumentNullException.ThrowIfNull(log);
            var restored = 0;
            var failed = 0;
            foreach (var file in Directory.EnumerateFiles(captureDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(captureDir, file);
                try
                {
                    await OsCapture.RestoreAsync(file, Path.Combine(destDir, rel), ct);
                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.Warn("os-restore: skipping " + rel + ": " + ex.Message);
                }
            }
            log.Info($"os-restore: {restored} files restored, {failed} failed");
            LogBootloaderHint(destDir, log);
            return (restored, failed);
        }

        internal static void LogBootloaderHint(string destDir, ILogger log)
        {
            if (OperatingSystem.IsWindows())
                log.Info("bootloader: run bcdboot \"" + Path.Combine(destDir, "Windows") + "\" /s <ESP> /f UEFI (or corpus --restore-disk for orchestrated repair)");
            else
                log.Info("bootloader: reinstall GRUB to the ESP targeting " + destDir + " (file restore only — layout repair is corpus item 5)");
        }
    }
}
