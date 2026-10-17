using System;
using System.Globalization;
using System.IO;
using System.Threading;
using TicTack;

namespace Corpus
{
    // WIM backend seam (§12.14 third round): DISM on Windows, wimlib-imagex
    // on Linux. Deliberate reuse, not a new wire format: IWimRunner extends
    // the existing IDismRunner string protocol ("one line in, DismResult
    // out"), so DismRunner plugs in behind a zero-logic adapter (never
    // forked) and WimlibRunner shells wimlib-imagex through the same shape.
    // Only the arg SHAPES differ per backend, formed in WimBackend below.
    public enum WimBackendKind
    {
        Dism,
        Wimlib,
    }

    public interface IWimRunner : IDismRunner
    {
        WimBackendKind Backend { get; }
    }

    // Zero-logic adapter: reuses the existing DismRunner as-is.
    public sealed class DismWimRunner : IWimRunner
    {
        private readonly IDismRunner _inner;

        public DismWimRunner(IDismRunner inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public WimBackendKind Backend => WimBackendKind.Dism;

        public DismResult Run(string arguments) => _inner.Run(arguments);
    }

    // wimlib-imagex shell-out (zero deps). Missing binary is a loud refusal
    // naming the path — never a silent fallback to nothing. No OS gate: it
    // runs wherever the binary exists (Linux here, also built for Windows).
    public sealed class WimlibRunner : IWimRunner
    {
        public const string DefaultBinaryPath = "/usr/bin/wimlib-imagex";

        private readonly string _binaryPath;

        public WimlibRunner(string? binaryPath = null)
        {
            _binaryPath = string.IsNullOrWhiteSpace(binaryPath) ? DefaultBinaryPath : binaryPath;
        }

        public WimBackendKind Backend => WimBackendKind.Wimlib;

        public string BinaryPath => _binaryPath;

        public DismResult Run(string arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (!File.Exists(_binaryPath))
                throw new WimSplitRefusedException(_binaryPath, $"wimlib-imagex not found at {_binaryPath} — install wimlib; refusing rather than running nothing");
            // Unbounded wait: splits run for minutes on multi-GB WIMs.
            var (code, stdout, stderr, _) = ProcessRunner.RunAsync(
                _binaryPath + " " + arguments, Path.GetTempPath(), true, CancellationToken.None, Timeout.Infinite)
                .GetAwaiter().GetResult();
            return new DismResult(code, stdout, stderr);
        }
    }

    // Split/solid refusals: every refusal names the path + reason.
    public sealed class WimSplitRefusedException : SingleFileBackupException
    {
        public string ImagePath { get; }

        public WimSplitRefusedException(string imagePath, string reason)
            : base($"refusing --split-swm of {imagePath}: {reason}")
        {
            ImagePath = imagePath;
        }
    }

    // wimlib post-run failure (DISM side reuses SingleFileDismException so
    // the CLI's exit-code mapping stays intact).
    public sealed class WimBackendException : SingleFileBackupException
    {
        public string ImagePath { get; }
        public int ExitCode { get; }

        public WimBackendException(string imagePath, string arguments, DismResult result)
            : base($"wimlib-imagex failed for {imagePath} (exit {result.ExitCode}): wimlib-imagex {arguments}\n{result.Stderr}".TrimEnd())
        {
            ImagePath = imagePath;
            ExitCode = result.ExitCode;
        }
    }

    public static class WimBackend
    {
        // SWM split for FAT32 USB targets: any WIM >4GB fails on FAT32, so
        // --to-wim results get --split-swm <sizeMb>. Returns the first part
        // path (= swmTarget); parts 2..n follow the DISM/wimlib stem
        // convention (X.swm, X2.swm, ...). Part enumeration is the caller's
        // glob — this method proves the SET is readable by info-querying the
        // first part (index count >= 1) instead.
        public static string SplitSwm(string wimPath, string swmTarget, int fileSizeMb, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wimPath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(swmTarget))
                throw new SingleFileTargetMissingException("--split-swm");
            if (fileSizeMb <= 0) throw new ArgumentOutOfRangeException(nameof(fileSizeMb));
            if (!File.Exists(wimPath))
                throw new SingleFileSourceNotFoundException(wimPath);
            if (wimPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
                throw new WimSplitRefusedException(wimPath, "ESD is solid-compressed and cannot be split — same rule as --append (plain-WIM only)");
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            if (!wimlib)
                RefuseIfSolid(wimPath, runner);
            var size = fileSizeMb.ToString(CultureInfo.InvariantCulture);
            var splitArgs = wimlib
                ? $"split {ProcessRunner.QuoteArg(wimPath)} {ProcessRunner.QuoteArg(swmTarget)} {size}"
                : $"/Split-Image /ImageFile:{ProcessRunner.QuoteArg(wimPath)} /SWMFile:{ProcessRunner.QuoteArg(swmTarget)} /FileSize:{size}";
            var r = runner.Run(splitArgs);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(wimPath, splitArgs, r)
                    : new SingleFileDismException(wimPath, splitArgs, r);
            var indexes = WimInfo(swmTarget, runner);
            if (indexes < 1)
                throw new SingleFileVerifyException(swmTarget, "split produced no readable images — the SWM set is not usable");
            log.Info($"split {wimPath} into {swmTarget} (+parts) at {fileSizeMb} MB: first part lists {indexes} image(s)");
            return swmTarget;
        }

        // Index count of an image (WIM, SWM part, or ESD): DISM via
        // /Get-WimInfo + the existing CountImageIndexes heuristic; wimlib
        // via `info` + CountWimlibIndexes. Used by SplitSwm's first-part
        // proof and by a future capture-from-dir verify.
        public static int WimInfo(string imagePath, IWimRunner runner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentNullException.ThrowIfNull(runner);
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            var args = wimlib
                ? $"info {ProcessRunner.QuoteArg(imagePath)}"
                : $"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(imagePath)}";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(imagePath, args, r)
                    : new SingleFileDismException(imagePath, args, r);
            return wimlib ? CountWimlibIndexes(r.Stdout) : SingleFileBackup.CountImageIndexes(r.Stdout);
        }

        // Integrity leg: DISM has no standalone verify, so readability via
        // /Get-WimInfo is the passthrough (the same leg VerifyWim starts
        // with); wimlib gets its real `verify`. Throws naming the path.
        public static void WimVerify(string imagePath, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (runner.Backend == WimBackendKind.Wimlib)
            {
                var args = $"verify {ProcessRunner.QuoteArg(imagePath)}";
                var r = runner.Run(args);
                if (r.ExitCode != 0)
                    throw new WimBackendException(imagePath, args, r);
            }
            else
            {
                WimInfo(imagePath, runner);
            }
            log.Info($"verified {imagePath}: integrity check OK");
        }

        // wimlib `info` prints per-image "Index:                  1"
        // (colon-tight); DISM prints "Index : 1" (colon-spaced), which is
        // why the existing CountImageIndexes heuristic does NOT match wimlib
        // output — probed 2026-10-17 against wimlib 1.14.5 (see E2E). This
        // accepts both shapes so DISM-shaped fixtures still parse.
        public static int CountWimlibIndexes(string infoStdout)
        {
            ArgumentNullException.ThrowIfNull(infoStdout);
            var count = 0;
            foreach (var line in infoStdout.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.StartsWith("Index", StringComparison.OrdinalIgnoreCase)
                    && t.Length > 5 && t[5..].TrimStart().StartsWith(":", StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        // Pre-split solid gate (DISM only): same locked default as --append —
        // the on-box DISM split-of-solid probe is OWED (needs Windows + real
        // DISM; on-box-unprobed, fake-covered only).
        private static void RefuseIfSolid(string wimPath, IWimRunner runner)
        {
            var args = $"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(wimPath)}";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw new SingleFileDismException(wimPath, args, r);
            if (SingleFileBackup.IsSolidImageInfo(r.Stdout))
                throw new WimSplitRefusedException(wimPath, "solid-compressed image; --split-swm is plain-WIM only (on-box DISM split-of-solid probe still owed — refuse stays until it lands)");
        }
    }
}
