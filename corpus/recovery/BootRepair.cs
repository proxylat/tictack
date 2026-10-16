using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TicTack;

namespace Corpus
{
    // Item 5: bare-metal tail — orchestrated layout recreate + bootloader
    // reinstall with checks (the manual bcdboot printout stays only as the
    // default dry-run/review path). Windows-only syscalls (diskpart, mountvol,
    // bcdboot, firmware query) sit behind IBootRepairRunner; Linux tests run
    // the fake, the real runner fails loud off Windows.
    public interface IBootRepairRunner
    {
        void ApplyLayout(string diskpartScript);
        (string WindowsDir, string EspDir) LocateVolumes(DiskLayout layout, int targetDisk);
        void InstallBootloader(string windowsDir, string espDir);
        string FirmwareType();
        void CheckBootFiles(string espDir);
    }

    // Real path: Windows-gated, every method throws off Windows.
    public sealed class WindowsBootRepairRunner : IBootRepairRunner
    {
        private readonly ILogger _log;

        public WindowsBootRepairRunner(ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        private static void RequireWindows()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("boot repair needs Windows (diskpart/bcdboot).");
        }

        public void ApplyLayout(string diskpartScript)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(diskpartScript);
            _log.Info("applying partition layout via diskpart");
            RunDiskpartScript(diskpartScript);
        }

        private static void RunDiskpartScript(string diskpartScript)
        {
            var script = Path.GetTempFileName();
            try
            {
                File.WriteAllText(script, diskpartScript);
                var r = ProcessRunner.RunAsync($"diskpart /s \"{script}\"", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    throw new InvalidDataException($"diskpart failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            }
            finally
            {
                try { File.Delete(script); } catch (IOException) { }
            }
        }

        // Target-scoped volume location: the ESP and Windows come from the
        // TARGET disk (extent match), never the running system. mountvol /S
        // and the C..Z scan would find the live OS — wrong whenever target
        // != boot disk (VHDX restores, PE with the OS disk present).
        public (string WindowsDir, string EspDir) LocateVolumes(DiskLayout layout, int targetDisk)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(layout);
            if (targetDisk < 0)
                throw new ArgumentOutOfRangeException(nameof(targetDisk), "target disk must be non-negative (got: " + targetDisk + ")");
            PartitionRecord? esp = null;
            var osCandidates = new List<PartitionRecord>();
            foreach (var p in layout.Partitions)
            {
                if (p.Skipped)
                    continue;
                if (esp == null && string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase))
                    esp = p;
                if (string.Equals(p.Filesystem, "ntfs", StringComparison.OrdinalIgnoreCase))
                    osCandidates.Add(p);
            }
            if (esp == null)
                throw new InvalidDataException($"no FAT32 ESP in the layout for target disk {targetDisk}");
            var espLetter = ResolveLetter(targetDisk, esp, assign: true);
            if (espLetter == null)
                throw new InvalidDataException($"target disk {targetDisk} partition {esp.PartitionNumber} (ESP) has no drive letter and auto-assign failed");
            if (!Directory.Exists(espLetter + "\\EFI"))
                throw new InvalidDataException($"target ESP at {espLetter} has no \\EFI (restore did not stream it?)");
            // Two passes: resolve-only first (no side effects when the OS
            // already has a letter), assign-and-retry only for the still
            // letterless. Windows dir verified per candidate — the first
            // NTFS partition is the OS on Windows layouts, Recovery follows.
            foreach (var os in osCandidates)
            {
                var letter = ResolveLetter(targetDisk, os, assign: false);
                if (letter != null && Directory.Exists(letter + "\\Windows\\System32"))
                    return (letter + "\\Windows", espLetter);
            }
            foreach (var os in osCandidates)
            {
                var letter = ResolveLetter(targetDisk, os, assign: true);
                if (letter != null && Directory.Exists(letter + "\\Windows\\System32"))
                    return (letter + "\\Windows", espLetter);
            }
            throw new InvalidDataException($"no Windows install on target disk {targetDisk} (checked {osCandidates.Count} NTFS partition(s))");
        }

        private string? ResolveLetter(int targetDisk, PartitionRecord rec, bool assign)
        {
            var found = DiskCapture.TryFindVolume(targetDisk, rec.OffsetBytes, rec.LengthBytes);
            // bcdboot wants letters: GUID paths and mount points don't do.
            if (found != null && found.Length == 2 && found[1] == ':' && char.IsAsciiLetter(found[0]))
                return found;
            if (!assign)
                return null;
            // diskpart `assign` lies to ESPs: it reports success but no
            // letter appears (proven live 2026-10-15 — the no-default-letter
            // attribute eats it). mountvol on the volume GUID sticks for
            // every type, so all letters go through this one path.
            if (found == null)
                throw new InvalidDataException($"target disk {targetDisk} partition {rec.PartitionNumber} has no volume to mount (extent {rec.OffsetBytes}+{rec.LengthBytes} unresolved)");
            if (!found.StartsWith(@"\\?\", StringComparison.Ordinal))
                throw new InvalidDataException($"target disk {targetDisk} partition {rec.PartitionNumber} is mounted at {found} with no GUID path (give the volume a letter first)");
            var taken = new HashSet<char>();
            foreach (var d in DriveInfo.GetDrives())
                if (d.Name.Length > 0)
                    taken.Add(char.ToUpperInvariant(d.Name[0]));
            var letter = FindFreeLetter(taken);
            if (letter == null)
                throw new InvalidDataException($"no free drive letter for target disk {targetDisk} partition {rec.PartitionNumber}");
            _log.Info($"assigning {letter}: to target disk {targetDisk} partition {rec.PartitionNumber} via mountvol");
            // mountvol wants the trailing backslash TryFindVolume trims.
            var r = ProcessRunner.RunAsync($"mountvol {letter}: {found}\\", Path.GetTempPath(), redirect: true, timeoutMs: 60000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"mountvol {letter}: failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            found = DiskCapture.TryFindVolume(targetDisk, rec.OffsetBytes, rec.LengthBytes);
            if (found != null && found.Length == 2 && found[1] == ':' && char.IsAsciiLetter(found[0]))
                return found;
            throw new InvalidDataException($"mountvol {letter}: reported success but target disk {targetDisk} partition {rec.PartitionNumber} still has no letter");
        }

        // Free-letter scan, Z down to C (A/B are floppy legacy): descending
        // so operator-attached sticks (lowest-free-first) rarely collide.
        // Pure for tests; the caller feeds uppercase GetDrives initials.
        internal static char? FindFreeLetter(ISet<char> taken)
        {
            ArgumentNullException.ThrowIfNull(taken);
            for (var c = 'Z'; c >= 'C'; c--)
                if (!taken.Contains(c))
                    return c;
            return null;
        }

        public void InstallBootloader(string windowsDir, string espDir)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(windowsDir);
            ArgumentNullException.ThrowIfNull(espDir);
            _log.Info($"installing bootloader: bcdboot {windowsDir} /s {espDir} /f UEFI");
            var r = ProcessRunner.RunAsync($"bcdboot \"{windowsDir}\" /s {espDir} /f UEFI", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"bcdboot failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
        }

        public string FirmwareType()
        {
            RequireWindows();
            // No PowerShell/WMI (both absent in installer-PE): probe a bogus
            // UEFI variable. UEFI answers "no such variable" (203); Legacy
            // BIOS answers "invalid function" (1) — the API doesn't exist
            // there. Any other failure (access denied without the privilege,
            // real variable collision) still lands on the UEFI branch, which
            // is the only side that can reach this call at all.
            _ = GetFirmwareEnvironmentVariable("", "{00000000-0000-0000-0000-000000000000}", new byte[1], 1);
            return Marshal.GetLastWin32Error() == 1 ? "Legacy" : "UEFI";
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFirmwareEnvironmentVariable(string lpName, string lpGuid, byte[] pBuffer, uint nSize);

        public void CheckBootFiles(string espDir)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(espDir);
            var bcd = Path.Combine(espDir, "EFI", "Microsoft", "Boot", "BCD");
            if (!File.Exists(bcd))
                throw new InvalidDataException($"bcdboot reported success but {bcd} is missing (do not boot this target)");
        }

        private static string Tail(string s) =>
            s.Length <= 500 ? s : s.Substring(s.Length - 500);
    }

    // Fake for Linux-green tests: records calls, never shells out.
    public sealed class FakeBootRepairRunner : IBootRepairRunner
    {
        public readonly List<string> Calls = new();
        public string Firmware = "UEFI";
        public string WindowsDir = "D:\\Windows";
        public string EspDir = "S:";
        public bool ThrowOnApply;

        public void ApplyLayout(string diskpartScript)
        {
            Calls.Add("ApplyLayout:" + diskpartScript.Length);
            LastScript = diskpartScript;
            if (ThrowOnApply)
                throw new InvalidDataException("fake diskpart failure");
        }

        public string LastScript { get; private set; } = "";
        public int LastLocateDisk { get; private set; } = -1;

        public (string WindowsDir, string EspDir) LocateVolumes(DiskLayout layout, int targetDisk)
        {
            Calls.Add("LocateVolumes");
            LastLocateDisk = targetDisk;
            return (WindowsDir, EspDir);
        }

        public void InstallBootloader(string windowsDir, string espDir) =>
            Calls.Add($"InstallBootloader:{windowsDir}>{espDir}");

        public string FirmwareType()
        {
            Calls.Add("FirmwareType");
            return Firmware;
        }

        public void CheckBootFiles(string espDir) =>
            Calls.Add("CheckBootFiles:" + espDir);
    }

    // Checks-then-runs: GPT-only, ESP FAT32 with the §9 size floor (200MB
    // 512e / 300MB 4Kn), MSR present, ≥1 NTFS, UEFI firmware. Returns 0 on a
    // verified boot chain, 1 on any refuse-or-fail (PE sees a clean line, no
    // stack trace).
    public static class BootRepairOrchestrator
    {
        public static int Repair(DiskLayout layout, IBootRepairRunner runner, ILogger log)
        {
            // One-shot composition (PE callers): apply first, bootloader
            // after. RestoreDisk calls the phases separately so bytes
            // stream between them — the table MUST be recreated before
            // streaming (diskpart clean wipes the old table; streaming
            // first would be destroyed). Same-disk contract: Repair
            // targets the disk the layout came from (bare-metal
            // same-slot replacement); cross-disk restores go through
            // RestoreDisk, which passes the real target.
            var rc = ApplyPhase(layout, runner, log, layout.DiskNumber);
            return rc != 0 ? rc : BootloaderPhase(layout, runner, log, layout.DiskNumber);
        }

        // Pre-stream: checks + firmware gate + partition-table recreate.
        // The script is the no-format variant (payload ranges stream next;
        // formatting here would wipe what restore is about to write).
        // targetDisk is explicit: the script's `select disk` MUST name the
        // restore target, never the layout's source disk.
        public static int ApplyPhase(DiskLayout layout, IBootRepairRunner runner, ILogger log, int targetDisk)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (targetDisk < 0)
                throw new ArgumentOutOfRangeException(nameof(targetDisk), "target disk must be non-negative (got: " + targetDisk + ")");
            var blocked = CheckLayout(layout);
            if (blocked != null)
            {
                log.Error(blocked);
                return 1;
            }
            try
            {
                var firmware = runner.FirmwareType();
                if (!string.Equals(firmware, "UEFI", StringComparison.OrdinalIgnoreCase))
                {
                    log.Error($"refusing boot repair: firmware is {firmware}, layout is GPT (Legacy+GPT is unbootable; the MBR2GPT path is a staged follow-up)");
                    return 1;
                }
                runner.ApplyLayout(DiskpartScript.Generate(layout, targetDisk, formatPartitions: false));
                log.Info($"boot repair: partition layout recreated on disk {targetDisk}");
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("boot repair failed", ex);
                return 1;
            }
        }

        // Post-verify: bootloader reinstall + boot-file check. Runs only
        // after the restored bytes verified (never boot a corrupt target).
        // Volumes resolve from the TARGET disk (extent match), never the
        // running system.
        public static int BootloaderPhase(DiskLayout layout, IBootRepairRunner runner, ILogger log, int targetDisk)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (targetDisk < 0)
                throw new ArgumentOutOfRangeException(nameof(targetDisk), "target disk must be non-negative (got: " + targetDisk + ")");
            try
            {
                var (win, esp) = runner.LocateVolumes(layout, targetDisk);
                log.Info($"boot repair: OS at {win}, ESP at {esp} (target disk {targetDisk})");
                runner.InstallBootloader(win, esp);
                log.Info("boot repair: bootloader reinstalled");
                runner.CheckBootFiles(esp);
                log.Info("boot repair: boot files verified");
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("boot repair failed", ex);
                return 1;
            }
        }

        // Dry-run plan: the same script restore-disk writes, plus the bcdboot
        // line with letters resolved at repair time (never by hand).
        public static string Plan(DiskLayout layout, int targetDisk)
        {
            ArgumentNullException.ThrowIfNull(layout);
            return DiskpartScript.Generate(layout, targetDisk)
                + "rem boot repair plan: resolve the target ESP + OS letters by extent match, then bcdboot <OS>\\Windows /s <ESP> /f UEFI (letters auto-resolved)\n";
        }

        // Pure layout gate, shared by Repair and the dry-run path.
        internal static string? CheckLayout(DiskLayout layout)
        {
            if (!string.Equals(layout.Style, "gpt", StringComparison.OrdinalIgnoreCase))
                return $"refusing boot repair: layout style is {layout.Style} (GPT only; MBR repair is a staged follow-up)";
            var floor = layout.SectorSizeBytes == 4096 ? 300L * 1024 * 1024 : 200L * 1024 * 1024;
            var esp = null as PartitionRecord;
            var os = null as PartitionRecord;
            var msr = false;
            foreach (var p in layout.Partitions)
            {
                if (!p.Skipped && string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase))
                    esp ??= p;
                if (!p.Skipped && string.Equals(p.Filesystem, "ntfs", StringComparison.OrdinalIgnoreCase))
                    os ??= p;
                if (p.Skipped)
                    msr = true;
            }
            if (esp == null)
                return "refusing boot repair: no FAT32 ESP in the layout";
            if (esp.LengthBytes < floor)
                return $"refusing boot repair: ESP is {esp.LengthBytes} bytes, floor is {floor} (future WinRE updates break below it)";
            if (!msr)
                return "refusing boot repair: no MSR partition in the layout";
            if (os == null)
                return "refusing boot repair: no NTFS OS partition in the layout";
            return null;
        }
    }
}
