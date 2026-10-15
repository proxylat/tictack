using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Item 5: bare-metal tail — orchestrated layout recreate + bootloader
    // reinstall with checks (the manual bcdboot printout stays only as the
    // default dry-run/review path). Windows-only syscalls (diskpart, bcdboot,
    // mountvol, firmware query) sit behind IBootRepairRunner; Linux tests run
    // the fake, the real runner fails loud off Windows.
    public interface IBootRepairRunner
    {
        void ApplyLayout(string diskpartScript);
        (string WindowsDir, string EspDir) LocateVolumes();
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
            var script = Path.GetTempFileName();
            try
            {
                File.WriteAllText(script, diskpartScript);
                _log.Info("applying partition layout via diskpart");
                var r = ProcessRunner.RunAsync($"diskpart /s \"{script}\"", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    throw new InvalidDataException($"diskpart failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            }
            finally
            {
                try { File.Delete(script); } catch (IOException) { }
            }
        }

        public (string WindowsDir, string EspDir) LocateVolumes()
        {
            RequireWindows();
            // The supported ESP mount (no manual letter assignment): mountvol
            // /S mounts the system partition; the OS volume is found by scan.
            var m = ProcessRunner.RunAsync("mountvol S: /S", Path.GetTempPath(), redirect: true, timeoutMs: 60000).GetAwaiter().GetResult();
            if (m.TimedOut || m.ExitCode != 0)
                throw new InvalidDataException($"mountvol S: /S failed (exit {m.ExitCode}{(m.TimedOut ? ", timed out" : "")}): {Tail(m.Stderr + m.Stdout)}");
            string? win = null;
            for (var d = 'C'; d <= 'Z'; d++)
            {
                if (Directory.Exists(d + ":\\Windows\\System32"))
                {
                    win = d + ":\\Windows";
                    break;
                }
            }
            if (win == null)
                throw new InvalidDataException("no Windows install found (scanned C..Z for \\Windows\\System32)");
            if (!Directory.Exists("S:\\EFI"))
                throw new InvalidDataException("ESP mounted at S: but has no \\EFI (wrong partition or empty ESP)");
            return (win, "S:");
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
            var r = ProcessRunner.RunAsync("powershell -NoProfile -Command \"(Get-ComputerInfo -Property BiosFirmwareType).BiosFirmwareType\"",
                Path.GetTempPath(), redirect: true, timeoutMs: 60000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout))
                throw new InvalidDataException($"firmware-type query failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            return r.Stdout.Trim();
        }

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
            if (ThrowOnApply)
                throw new InvalidDataException("fake diskpart failure");
        }

        public (string WindowsDir, string EspDir) LocateVolumes()
        {
            Calls.Add("LocateVolumes");
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
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
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
                runner.ApplyLayout(DiskpartScript.Generate(layout));
                log.Info("boot repair: partition layout recreated");
                var (win, esp) = runner.LocateVolumes();
                log.Info($"boot repair: OS at {win}, ESP at {esp}");
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
        public static string Plan(DiskLayout layout)
        {
            ArgumentNullException.ThrowIfNull(layout);
            return DiskpartScript.Generate(layout)
                + "rem boot repair plan: mountvol S: /S, then bcdboot <OS>\\Windows /s S: /f UEFI (letters auto-resolved)\n";
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
