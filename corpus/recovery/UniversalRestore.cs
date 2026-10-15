using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Item 6: universal restore v2, STORAGE-FIRST. The implemented step is the
    // storage-controller driver ladder (user dir → removable → in-box DB) plus
    // boot-mode handling; NIC/GPU are staged follow-ups (first-boot PnP),
    // pinned in StagedFollowUps so the deferral is explicit, not silent.
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

    public static class UniversalRestore
    {
        // Explicitly deferred: named here and logged on every run.
        public static readonly string[] StagedFollowUps =
        [
            "NIC drivers (first-boot PnP)",
            "GPU drivers (first-boot PnP)",
        ];

        // Storage ladder: first existing dir wins, boot-critical match via
        // PnP IDs. 0 = injected (or dry-run plan); 1 = no rung had drivers.
        public static int InjectStorageDrivers(UniversalRestoreOptions opts, IDriverInjector injector, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(injector);
            ArgumentNullException.ThrowIfNull(log);
            log.Info("universal restore: staged follow-ups (not this run): " + string.Join("; ", StagedFollowUps));
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
