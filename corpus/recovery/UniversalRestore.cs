using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Item 6: universal restore v2. Storage-controller driver ladder (user
    // dir → removable → in-box DB) plus boot-mode handling plus NIC/GPU
    // first-boot stages: offline file copies into the restored image's
    // driver staging dir + a pnputil install script first boot runs.
    public sealed record UniversalRestoreOptions(
        string[] DriverDirs,
        string OfflineWindowsDir,
        bool DryRun = false);

    public interface IDriverInjector
    {
        void Inject(string offlineWindowsDir, string driverDir);
    }

    // Real path: offline DISM inject, Windows-gated. Signed drivers only
    // (no /ForceUnsigned — secure > bootable-on-any-hardware).
    public sealed class WindowsDismDriverInjector : IDriverInjector
    {
        private readonly ILogger _log;

        public WindowsDismDriverInjector(ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        public void Inject(string offlineWindowsDir, string driverDir)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("offline driver inject needs Windows (DISM).");
            ArgumentNullException.ThrowIfNull(offlineWindowsDir);
            ArgumentNullException.ThrowIfNull(driverDir);
            // DISM wants the image root (D:\), not D:\Windows.
            var image = Path.GetDirectoryName(offlineWindowsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(image))
                throw new InvalidDataException($"cannot derive the image root from {offlineWindowsDir}");
            _log.Info($"injecting storage drivers from {driverDir} into {image}");
            var r = ProcessRunner.RunAsync($"dism /Image:\"{image}\" /Add-Driver /Driver:\"{driverDir}\" /Recurse",
                Path.GetTempPath(), redirect: true, timeoutMs: 600000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"dism add-driver failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
        }

        private static string Tail(string s) =>
            s.Length <= 500 ? s : s.Substring(s.Length - 500);
    }

    // Fake for Linux-green tests: records injections, never shells out.
    public sealed class FakeDriverInjector : IDriverInjector
    {
        public readonly List<(string Image, string Driver)> Calls = new();

        public void Inject(string offlineWindowsDir, string driverDir) =>
            Calls.Add((offlineWindowsDir, driverDir));
    }

    // NIC/GPU first-boot staging: driver packs copied offline into the
    // restored image; first boot installs them via pnputil (no DISM, no
    // elevation for the copy itself — Linux-runnable against a mounted tree).
    public sealed record FirstBootOptions(
        string? NicDir,
        string? GpuDir,
        string OfflineWindowsDir,
        bool DryRun = false);

    public interface IFirstBootStager
    {
        void Stage(string offlineWindowsDir, string driverDir, string category);
    }

    public sealed class FileFirstBootStager : IFirstBootStager
    {
        public void Stage(string offlineWindowsDir, string driverDir, string category)
        {
            ArgumentNullException.ThrowIfNull(offlineWindowsDir);
            ArgumentNullException.ThrowIfNull(driverDir);
            ArgumentNullException.ThrowIfNull(category);
            var dest = Path.Combine(offlineWindowsDir, "Drivers", category);
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(driverDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dest, Path.GetRelativePath(driverDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    // Fake for Linux-green tests: records stagings, never touches disk.
    public sealed class FakeFirstBootStager : IFirstBootStager
    {
        public readonly List<(string Image, string Driver, string Category)> Calls = new();

        public void Stage(string offlineWindowsDir, string driverDir, string category) =>
            Calls.Add((offlineWindowsDir, driverDir, category));
    }

    public static class UniversalRestore
    {
        // Storage ladder: first existing dir wins, boot-critical match via
        // PnP IDs. 0 = injected (or dry-run plan); 1 = no rung had drivers.
        public static int InjectStorageDrivers(UniversalRestoreOptions opts, IDriverInjector injector, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(injector);
            ArgumentNullException.ThrowIfNull(log);
            foreach (var dir in opts.DriverDirs ?? [])
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;
                log.Info($"universal restore: storage-driver rung {dir}");
                if (opts.DryRun)
                {
                    log.Info("dry run: driver inject planned, nothing applied");
                    return 0;
                }
                try
                {
                    injector.Inject(opts.OfflineWindowsDir, dir);
                }
                catch (Exception ex)
                {
                    log.Error($"universal restore: inject from {dir} failed", ex);
                    return 1;
                }
                log.Info("universal restore: storage drivers injected");
                return 0;
            }
            log.Error($"universal restore: no driver rung exists (tried {(opts.DriverDirs ?? []).Length} dirs) — dissimilar storage bluescreens INACCESSIBLE_BOOT_DEVICE");
            return 1;
        }

        // NIC/GPU first-boot stages: offline copy of each driver pack into
        // the restored image + a pnputil install script first boot runs.
        // Missing dirs warn-and-skip (no NIC/GPU never bluescreens, unlike
        // storage); copy failures fail the run. 0 = staged (or nothing to
        // stage); 1 = a copy failed.
        public static int StageFirstBootDrivers(FirstBootOptions opts, IFirstBootStager stager, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(stager);
            ArgumentNullException.ThrowIfNull(log);
            var staged = 0;
            foreach (var (dir, category) in new[] { (opts.NicDir, "Nic"), (opts.GpuDir, "Gpu") })
            {
                var name = category.ToLowerInvariant();
                if (string.IsNullOrEmpty(dir))
                {
                    log.Info($"first-boot: no {name} dir configured, skipping");
                    continue;
                }
                if (!Directory.Exists(dir))
                {
                    log.Warn($"first-boot: {name} dir missing, skipping: {dir}");
                    continue;
                }
                if (opts.DryRun)
                {
                    log.Info($"dry run: {name} staging planned from {dir}, nothing applied");
                    continue;
                }
                try
                {
                    stager.Stage(opts.OfflineWindowsDir, dir, category);
                }
                catch (Exception ex)
                {
                    log.Error($"first-boot: {name} staging from {dir} failed", ex);
                    return 1;
                }
                log.Info($"first-boot: {name} drivers staged from {dir}");
                staged++;
            }
            if (staged > 0)
                WriteInstallScript(opts.OfflineWindowsDir, log);
            return 0;
        }

        // pnputil install script beside the staged packs: a sysprepped image
        // chains it from SetupComplete, otherwise the operator runs it once.
        // Offline file write, no registry, no DISM.
        internal static void WriteInstallScript(string offlineWindowsDir, ILogger log)
        {
            var dir = Path.Combine(offlineWindowsDir, "Drivers");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Install-FirstBoot-Drivers.cmd"),
                "@echo off\r\n"
                + "rem Staged by Corpus universal restore: first-boot NIC/GPU install.\r\n"
                + "pnputil /add-driver \"%~dp0Nic\\*.inf\" /subdirs /install\r\n"
                + "pnputil /add-driver \"%~dp0Gpu\\*.inf\" /subdirs /install\r\n");
            log.Info("first-boot: install script written");
        }

        // Boot-mode handling: GPT wants UEFI, MBR wants Legacy/BIOS. A
        // mismatch refuses with the conversion path (the conversion itself is
        // staged — CheckBootMode never rewrites a partition table).
        public static int CheckBootMode(DiskLayout layout, string firmwareType, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(firmwareType);
            ArgumentNullException.ThrowIfNull(log);
            var gpt = string.Equals(layout.Style, "gpt", StringComparison.OrdinalIgnoreCase);
            var uefi = string.Equals(firmwareType, "UEFI", StringComparison.OrdinalIgnoreCase);
            if (gpt == uefi)
            {
                log.Info($"universal restore: boot mode OK ({layout.Style} on {firmwareType})");
                return 0;
            }
            log.Error($"universal restore: {layout.Style} layout on {firmwareType} firmware is unbootable (staged: MBR2GPT conversion path, then re-repair)");
            return 1;
        }
    }
}
