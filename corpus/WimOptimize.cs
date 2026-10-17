using System;
using System.IO;
using TicTack;

namespace Corpus
{
    // --optimize-wim: dedupe + optionally recompress a WIM via wimlib
    // `optimize` (probed 2026-10-17 against wimlib-imagex 1.14.5 on this
    // box: `optimize WIMFILE [--recompress] [--compress=TYPE]
    // [--threads=N] [--check] [--nocheck] [--solid]`, plus man-only
    // extras like --include-integrity/--chunk-size; only --recompress is
    // surfaced here).
    //
    // Windows path: DISM has NO optimize verb, so DismWimRunner is refused
    // loud (option (a)) naming wimlib as the path — no export-to-temp
    // emulation (option (b)): a full export rewrite is not the same op
    // (fresh compression decisions, new file identity) and pretending
    // otherwise would lie about what ran.
    //
    // Atomicity: wimlib writes the rebuilt WIM to a temp file and renames
    // it over the original when ready (per `man wimoptimize`), so no
    // caller-side temp+rename is needed — the backend owns the atomicity.
    public static class WimOptimize
    {
        public static (long Before, long After) Optimize(string wimPath, bool recompress, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wimPath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(wimPath))
                throw new SingleFileSourceNotFoundException(wimPath);
            if (runner.Backend == WimBackendKind.Dism)
                throw new WimConvertRefusedException("--optimize-wim", wimPath, "DISM has no optimize verb — run this on a box with wimlib-imagex");

            var before = new FileInfo(wimPath).Length;
            var args = $"optimize {ProcessRunner.QuoteArg(wimPath)}" + (recompress ? " --recompress" : "");
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw new WimBackendException(wimPath, args, r);
            if (!File.Exists(wimPath))
                throw new SingleFileVerifyException(wimPath, "optimize reported success but the WIM is gone afterwards — refusing to report sizes for a missing file");
            var after = new FileInfo(wimPath).Length;
            log.Info($"optimized {wimPath}: {before} -> {after} bytes (reclaimed {before - after} bytes{(recompress ? ", recompressed" : "")})");
            return (before, after);
        }
    }
}
