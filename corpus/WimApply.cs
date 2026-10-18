using System;
using System.Globalization;
using System.IO;
using TicTack;

namespace Corpus
{
    // Install-without-USB: apply a WIM/ESD image to a partition, then
    // bcdboot the applied tree. DISM /Apply-Image from disk — no WinPE
    // boot needed.
    //
    // HONEST CAVEAT: DISM /CheckIntegrity checksums the WIM being read,
    // not the bytes landed on disk — there is no chunk-level verify on
    // this path (unlike the corpus chunk pipeline). A bad disk write
    // after a green apply is not caught here; BootVerify after reboot
    // is the backstop.
    //
    // bcdboot seam note: the existing IBootRepairRunner.InstallBootloader
    // needs (windowsDir, espDir) resolved from a DiskLayout extent match
    // — it does not fit a plain "<target>\Windows" call, which is all an
    // apply flow owns. So bcdboot goes through the minimal
    // IWimApplyBootRunner below (single windowsDir; the real impl shells
    // the stock `bcdboot "<win>"` letting Windows pick the system
    // partition). BootRepair.cs untouched.
    //
    // Off-Windows note: the wimlib path applies to plain directories
    // (that is the Linux E2E). The live-OS refusal only applies where the
    // booted OS volume is determinable (Windows via SystemDirectory);
    // off Windows there is no live Windows volume to protect, bcdboot is
    // skipped with an INF line, and pointing wimlib at a mounted Windows
    // partition remains the operator's responsibility.
    public sealed class WimApplyRefusedException : SingleFileBackupException
    {
        public string Path { get; }

        public WimApplyRefusedException(string op, string path, string reason)
            : base($"refusing {op} of {path}: {reason}")
        {
            Path = path;
        }
    }

    public sealed class WimApplyBootException : SingleFileBackupException
    {
        public string WindowsDir { get; }
        public int ExitCode { get; }

        public WimApplyBootException(string windowsDir, int exitCode, string detail)
            : base($"bcdboot failed for {windowsDir} (exit {exitCode}): {detail}".TrimEnd())
        {
            WindowsDir = windowsDir;
            ExitCode = exitCode;
        }
    }

    // Minimal bcdboot seam: one windowsDir in, bootloader installed.
    public interface IWimApplyBootRunner
    {
        void Install(string windowsDir);
    }

    // Real path: Windows-gated, stock bcdboot with no /s (the system
    // partition owns the BCD on an install-without-USB target).
    // Shape is knowledge-sourced — on-box Windows bcdboot probe OWED.
    public sealed class BcdbootRunner : IWimApplyBootRunner
    {
        private readonly ILogger _log;

        public BcdbootRunner(ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        public void Install(string windowsDir)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(windowsDir);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("bcdboot needs Windows.");
            _log.Info($"installing bootloader: bcdboot {windowsDir}");
            var r = ProcessRunner.RunAsync($"bcdboot \"{windowsDir}\"", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new WimApplyBootException(windowsDir, r.ExitCode, $"bcdboot failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {(r.Stderr + r.Stdout).TrimEnd()}");
        }
    }

    public static class WimApply
    {
        // CLI shape: --apply --wim X [--index N] --target D.
        // On Windows this bcdboots for real; off Windows (wimlib
        // dir-apply) bcdboot is skipped — there is no Windows tree.
        public static void Apply(string wimPath, int? index, string targetDir, IWimRunner runner, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            IWimApplyBootRunner? boot = OperatingSystem.IsWindows() ? new BcdbootRunner(log) : null;
            Apply(wimPath, index, targetDir, runner, log, boot);
        }

        // Injectable-boot overload (tests + any caller that resolves the
        // ESP itself). Null boot = skip bcdboot with an INF line.
        public static void Apply(string wimPath, int? index, string targetDir, IWimRunner runner, ILogger log, IWimApplyBootRunner? boot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wimPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            ApplyCore(wimPath, index, targetDir, runner, log, boot, Environment.SystemDirectory, OperatingSystem.IsWindows());
        }

        // Test seam: the live-OS guard needs the real machine paths in
        // product, but tests must run on any drive (CI TEMP sits on the
        // OS volume, which the guard rightly refuses).
        internal static void ApplyCore(string wimPath, int? index, string targetDir, IWimRunner runner, ILogger log, IWimApplyBootRunner? boot, string systemDirectory, bool isWindows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wimPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(wimPath))
                throw new SingleFileSourceNotFoundException(wimPath);
            GuardLiveOs(targetDir, systemDirectory, isWindows);

            // Index rule reused verbatim from WimImageOps.Convert: probe
            // first, single-index defaults 1 (+INF), multi-index requires
            // explicit — never silently drop images.
            int count = WimBackend.WimInfo(wimPath, runner);
            if (count <= 0)
                throw new SingleFileVerifyException(wimPath, "lists 0 images — nothing to apply");
            int effective = index ?? (count == 1
                ? 1
                : throw new WimApplyRefusedException("--apply", wimPath, $"source lists {count} images — pass an explicit index; refusing rather than silently dropping images"));
            if (index is < 1 || index > count)
                throw new WimApplyRefusedException("--apply", wimPath, $"index {index} is out of range — source lists {count} image(s)");
            if (index is null)
                log.Info($"apply {wimPath} defaults to index 1 (single-index source)");

            Directory.CreateDirectory(targetDir);
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            // wimlib shape probed on-box (`wimlib-imagex apply --help`,
            // 1.14.5): apply WIMFILE [IMAGE] TARGET — index always passed
            // (the rule above always resolves one). DISM shape is
            // knowledge-sourced — on-box Windows probe OWED.
            var idx = effective.ToString(CultureInfo.InvariantCulture);
            var args = wimlib
                ? $"apply {ProcessRunner.QuoteArg(wimPath)} {idx} {ProcessRunner.QuoteArg(targetDir)}"
                : $"/Apply-Image /ImageFile:{ProcessRunner.QuoteArg(wimPath)} /Index:{idx} /ApplyDir:{ProcessRunner.QuoteArg(targetDir)} /CheckIntegrity";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(wimPath, args, r)
                    : new SingleFileDismException(wimPath, args, r);
            log.Info($"applied {wimPath} index {idx} to {targetDir}");

            if (boot is null)
            {
                log.Info($"skipping bcdboot for {targetDir} (no boot runner — off-Windows wimlib apply)");
                return;
            }
            var windowsDir = Path.Combine(targetDir, "Windows");
            boot.Install(windowsDir);
            log.Info($"bootloader installed for {windowsDir}");
        }

        // Dual-boot safety: never overwrite the booted OS. Pure string
        // logic (roots compared) so tests pin it without Windows.
        // Drive-letter fast path first: Path.GetPathRoot does not parse
        // "C:\" on non-Windows runtimes, and the guard's string logic is
        // pinned by tests on Linux.
        internal static void GuardLiveOs(string targetDir, string systemDirectory, bool isWindows)
        {
            if (!isWindows)
                return;
            var targetFull = Path.GetFullPath(targetDir);
            if (IsSameDrive(targetDir, systemDirectory) || IsSameDrive(targetFull, systemDirectory))
                throw new WimApplyRefusedException("--apply", targetDir, $"target is on the running OS volume ({systemDirectory}) — refusing to overwrite the booted OS");
            var targetRoot = Path.GetPathRoot(targetFull);
            var systemRoot = Path.GetPathRoot(systemDirectory);
            if (!string.IsNullOrEmpty(targetRoot) && string.Equals(targetRoot, systemRoot, StringComparison.OrdinalIgnoreCase))
                throw new WimApplyRefusedException("--apply", targetDir, $"target is on the running OS volume {systemRoot} — refusing to overwrite the booted OS");
        }

        private static bool IsSameDrive(string targetFull, string systemDirectory) =>
            targetFull.Length >= 2 && systemDirectory.Length >= 2
            && targetFull[1] == ':' && systemDirectory[1] == ':'
            && char.IsAsciiLetter(targetFull[0]) && char.IsAsciiLetter(systemDirectory[0])
            && char.ToUpperInvariant(targetFull[0]) == char.ToUpperInvariant(systemDirectory[0]);
    }
}
