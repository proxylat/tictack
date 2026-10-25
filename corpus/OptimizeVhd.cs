using System;
using System.IO;
using System.Threading;
using TicTack;

namespace Corpus
{
    // --optimize-vhd post-step for --to-vhdx: compact the VHDX with Hyper-V
    // Optimize-VHD after verify greens.
    //
    // WIRING (main agent, Program.cs RunSingleFile --to-vhdx path — this file
    // wires nothing itself): order is verify -> compact -> zstd, i.e. call
    // Compact AFTER BackupVhdx returns (verify already greened inside it) and
    // BEFORE CompressSidecar. Compact-then-compress: the sidecar hashes and
    // deletes the original, so compacting after it would compact nothing and
    // ship the archive uncompacted.
    //
    // No --mode knob on the CLI by design: one less spelling, Full is the
    // reclaim-everything choice. The mode param stays internal for tests.
    public sealed class OptimizeVhdException : SingleFileBackupException
    {
        public string ImagePath { get; }
        public int ExitCode { get; }

        public OptimizeVhdException(string imagePath, int exitCode, string arguments, string stderr)
            : base($"Optimize-VHD failed for {imagePath} (exit {exitCode}): powershell.exe {arguments}\n{stderr}".TrimEnd())
        {
            ImagePath = imagePath;
            ExitCode = exitCode;
        }
    }

    public sealed class OptimizeVhdModuleException : SingleFileBackupException
    {
        public string ImagePath { get; }

        public OptimizeVhdModuleException(string imagePath, string output)
            : base($"Optimize-VHD failed for {imagePath}: Hyper-V PowerShell module required (the cmdlet is unknown on this box — install the Hyper-V role/tools):\n{output}".TrimEnd())
        {
            ImagePath = imagePath;
        }
    }

    public interface IOptimizeRunner
    {
        DismResult Run(string arguments);
    }

    // Production seam: powershell.exe via the house ProcessRunner, mirroring
    // DismRunner's shape (unbounded wait: compacts run long — the 10-minute
    // house default would kill them).
    public sealed class PowershellOptimizeRunner : IOptimizeRunner
    {
        public DismResult Run(string arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("--optimize-vhd needs Windows (Hyper-V powershell.exe)");
            var (code, stdout, stderr, _) = ProcessRunner.RunAsync(
                "powershell.exe " + arguments, Path.GetTempPath(), true, CancellationToken.None, Timeout.Infinite)
                .GetAwaiter().GetResult();
            return new DismResult(code, stdout, stderr);
        }
    }

    public static class OptimizeVhd
    {
        public static (long Before, long After) Compact(string vhdxPath, IOptimizeRunner runner, ILogger log, string mode = "Full")
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException($"--optimize-vhd needs Windows (Hyper-V Optimize-VHD); {vhdxPath} was not compacted");
            ArgumentException.ThrowIfNullOrWhiteSpace(vhdxPath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(vhdxPath))
                throw new SingleFileSourceNotFoundException(vhdxPath);
            var arguments = BuildArguments(vhdxPath, mode);
            var before = new FileInfo(vhdxPath).Length;
            var result = runner.Run(arguments);
            ThrowIfFailed(vhdxPath, arguments, result);
            var after = new FileInfo(vhdxPath).Length;
            log.Info($"optimized {vhdxPath}: {before} -> {after} bytes ({before - after} reclaimed, Mode {mode})");
            return (before, after);
        }

        public static string BuildArguments(string vhdxPath, string mode = "Full")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(vhdxPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(mode);
            return "-NoProfile -NonInteractive -Command \"Optimize-VHD -Path '"
                + vhdxPath.Replace("'", "''", StringComparison.Ordinal)
                + "' -Mode " + mode + "\"";
        }

        // Best-effort match on the stock PowerShell "term is not recognized"
        // text — on-box template captured 2026-10-25 ("<name> : The term
        // '<name>' is not recognized as the name of a cmdlet, function,
        // script file, or operable program.", exit 1, stderr). Unelevated
        // capture was attempted but the temp-user child cannot start over
        // SSH (0xC0000142); any elevation failure still surfaces via
        // OptimizeVhdException.
        internal static bool IsModuleMissing(string output)
        {
            ArgumentNullException.ThrowIfNull(output);
            return output.Contains("Optimize-VHD", StringComparison.OrdinalIgnoreCase)
                && output.Contains("not recognized", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ThrowIfFailed(string vhdxPath, string arguments, DismResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.ExitCode == 0)
                return;
            if (IsModuleMissing(result.Stdout + "\n" + result.Stderr))
                throw new OptimizeVhdModuleException(vhdxPath, result.Stdout + result.Stderr);
            throw new OptimizeVhdException(vhdxPath, result.ExitCode, arguments, result.Stderr);
        }
    }
}
