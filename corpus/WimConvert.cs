using System;
using System.IO;
using System.Linq;
using TicTack;

namespace Corpus
{
    public enum WimCompression
    {
        Max,
        Recovery,
    }

    // Convert/merge refusals: every refusal names the path + reason.
    public sealed class WimConvertRefusedException : SingleFileBackupException
    {
        public string ImagePath { get; }

        public WimConvertRefusedException(string op, string imagePath, string reason)
            : base($"refusing {op} of {imagePath}: {reason}")
        {
            ImagePath = imagePath;
        }
    }

    // WIM<->ESD convert + SWM merge (research items 3+4). Source-agnostic:
    // WIM->ESD, ESD->WIM and single-index WIM->WIM are the same op, only
    // the extensions and /Compress differ. Index rule is safety-critical:
    // the source index count is probed first; a single-index source makes
    // index optional (defaults 1, INF line), a multi-index source makes it
    // REQUIRED — never silently drop images.
    public static class WimImageOps
    {
        public static string Convert(string sourcePath, string destPath, int? index, WimCompression compress, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(sourcePath))
                throw new SingleFileSourceNotFoundException(sourcePath);
            if (SamePath(sourcePath, destPath))
                throw new WimConvertRefusedException("--convert", sourcePath, "source and destination are the same file — refusing to export onto itself");
            if (destPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) && compress == WimCompression.Max)
                throw new WimConvertRefusedException("--convert", destPath, "ESD is always solid/recovery-compressed — use Recovery, not Max");

            int count = WimBackend.WimInfo(sourcePath, runner);
            if (count <= 0)
                throw new SingleFileVerifyException(sourcePath, "lists 0 images — nothing to export");
            int effective = index ?? (count == 1
                ? 1
                : throw new WimConvertRefusedException("--convert", sourcePath, $"source lists {count} images — pass an explicit index; refusing rather than silently dropping images"));
            if (index is < 1 || index > count)
                throw new WimConvertRefusedException("--convert", sourcePath, $"index {index} is out of range — source lists {count} image(s)");
            if (index is null)
                log.Info($"convert {sourcePath} defaults to index 1 (single-index source)");

            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            var args = wimlib
                ? $"export {ProcessRunner.QuoteArg(sourcePath)} {effective} {ProcessRunner.QuoteArg(destPath)} --compress={(compress == WimCompression.Max ? "maximum" : "recovery")}"
                : $"/Export-Image /SourceImageFile:{ProcessRunner.QuoteArg(sourcePath)} /SourceIndex:{effective} /DestinationImageFile:{ProcessRunner.QuoteArg(destPath)} /Compress:{(compress == WimCompression.Max ? "max" : "recovery")}";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(sourcePath, args, r)
                    : new SingleFileDismException(sourcePath, args, r);
            log.Info($"converted {sourcePath} index {effective} to {destPath} ({compress})");
            return destPath;
        }

        // SWM merge: reunite a split set into one WIM. wimlib `join` needs
        // ALL parts, enumerated via the X.swm/X2.swm/... stem convention.
        // DISM shape (/SWMFile stem glob + /SourceIndex:1) is knowledge-
        // sourced, on-box probe OWED — the fake pins the shape only.
        public static string MergeSwm(string firstPartPath, string destPath, IWimRunner runner, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(firstPartPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            var parts = WimBackend.EnumerateSwmParts(firstPartPath).ToList();
            if (parts.Count == 0)
                throw new SingleFileSourceNotFoundException(firstPartPath);
            if (parts.Count < 2)
                throw new WimConvertRefusedException("--merge-swm", firstPartPath, "no siblings found (want X.swm, X2.swm, ...) — refusing to merge a lone part");
            if (SamePath(firstPartPath, destPath))
                throw new WimConvertRefusedException("--merge-swm", firstPartPath, "source and destination are the same file — refusing to merge onto itself");

            string args;
            if (runner.Backend == WimBackendKind.Wimlib)
            {
                args = $"join {ProcessRunner.QuoteArg(destPath)} {string.Join(" ", parts.Select(ProcessRunner.QuoteArg))}";
            }
            else
            {
                var dir = Path.GetDirectoryName(firstPartPath);
                var pattern = (string.IsNullOrEmpty(dir) ? "" : dir + Path.DirectorySeparatorChar)
                    + Path.GetFileNameWithoutExtension(firstPartPath) + "*" + Path.GetExtension(firstPartPath);
                args = $"/Export-Image /SourceImageFile:{ProcessRunner.QuoteArg(firstPartPath)} /SWMFile:{ProcessRunner.QuoteArg(pattern)} /SourceIndex:1 /DestinationImageFile:{ProcessRunner.QuoteArg(destPath)}";
            }
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw runner.Backend == WimBackendKind.Wimlib
                    ? new WimBackendException(firstPartPath, args, r)
                    : new SingleFileDismException(firstPartPath, args, r);
            log.Info($"merged {parts.Count} part(s) from {firstPartPath} into {destPath}");
            return destPath;
        }

        private static bool SamePath(string a, string b)
        {
            var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp);
        }
    }
}
