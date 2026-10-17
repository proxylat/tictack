using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Prepare-universal cluster items 7+10: unattend apply + language-pack
    // add, both offline DISM against a restored/serviced tree. Same
    // executor seam as OfflineTrim (ITrimRunner) so tests stay Linux-green.
    // DISM arg shapes are training-knowledge, on-box-unprobed: the exact
    // /Apply-Unattend and /Add-Package /PackagePath quoting against an
    // /Image root is on-box-owed (VM diff before trusting).
    public static class OfflineServicing
    {
        // 0 = applied (or dry-run plan); 1 = guard refused or DISM failed.
        public static int ApplyUnattend(string offlineWindowsDir, string unattendFile, ITrimRunner runner, ILogger log, bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(offlineWindowsDir);
            ArgumentNullException.ThrowIfNull(unattendFile);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            var image = GuardTree(offlineWindowsDir, log, "unattend");
            if (image == null)
                return 1;
            // Fail fast: a plan naming a nonexistent file is a broken plan,
            // so the probe runs before the dry-run branch too.
            if (!File.Exists(unattendFile))
            {
                log.Error($"offline servicing: unattend file missing, refusing: {unattendFile}");
                return 1;
            }
            var command = $"dism /Image:{ProcessRunner.QuoteArg(image)} /Apply-Unattend:{ProcessRunner.QuoteArg(unattendFile)}";
            if (dryRun)
            {
                log.Info($"dry run: unattend planned, nothing applied: {command}");
                return 0;
            }
            return RunOne(command, runner, log, "unattend");
        }

        // 0 = all added (or plan / nothing to do); 1 = a cab was missing or
        // a DISM add failed. Missing cabs refuse BEFORE any mutation: the
        // whole list is probed first so a typo never half-installs a pack.
        public static int AddLangPack(string offlineWindowsDir, IReadOnlyList<string> cabPaths, ITrimRunner runner, ILogger log, bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(offlineWindowsDir);
            ArgumentNullException.ThrowIfNull(cabPaths);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            var image = GuardTree(offlineWindowsDir, log, "lang-pack");
            if (image == null)
                return 1;
            if (cabPaths.Count == 0)
            {
                log.Info("offline servicing: no lang-pack cabs given, nothing to do");
                return 0;
            }
            foreach (var cab in cabPaths)
            {
                if (!File.Exists(cab))
                {
                    log.Error($"offline servicing: lang-pack cab missing, refusing (nothing applied): {cab}");
                    return 1;
                }
            }
            var failures = 0;
            foreach (var cab in cabPaths)
            {
                var command = $"dism /Image:{ProcessRunner.QuoteArg(image)} /Add-Package /PackagePath:{ProcessRunner.QuoteArg(cab)}";
                if (dryRun)
                {
                    log.Info($"dry run: lang-pack planned, nothing applied: {command}");
                    continue;
                }
                if (RunOne(command, runner, log, "lang-pack") != 0)
                    failures++;
            }
            return failures == 0 ? 0 : 1;
        }

        // Not-a-Windows-tree guard shared by both ops: the SYSTEM hive must
        // exist (same probe as OfflineTrim). Returns the DISM image root
        // (D:\, not D:\Windows — trailing-backslash argv trap handled by
        // QuoteArg per ProcessRunner), or null after logging the refusal.
        internal static string? GuardTree(string offlineWindowsDir, ILogger log, string op)
        {
            if (string.IsNullOrWhiteSpace(offlineWindowsDir))
            {
                log.Error($"offline servicing: no --windows-dir given, refusing ({op})");
                return null;
            }
            var hive = Path.Combine(offlineWindowsDir, "System32", "config", "SYSTEM");
            if (!File.Exists(hive))
            {
                log.Error($"offline servicing: not a Windows tree (no {hive}), refusing ({op})");
                return null;
            }
            var image = Path.GetDirectoryName(offlineWindowsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(image))
            {
                log.Error($"offline servicing: cannot derive the image root from {offlineWindowsDir}");
                return null;
            }
            return image;
        }

        private static int RunOne(string command, ITrimRunner runner, ILogger log, string op)
        {
            TrimResult r;
            try
            {
                r = runner.Run(command);
            }
            catch (Exception ex)
            {
                log.Error($"offline servicing failed ({op}): {command}", ex);
                return 1;
            }
            var detail = string.IsNullOrWhiteSpace(r.Output) ? string.Empty : $" ({Tail(r.Output)})";
            if (r.ExitCode == 0)
            {
                log.Info($"offline servicing applied ({op}, exit 0): {command}{detail}");
                return 0;
            }
            log.Error($"offline servicing failed ({op}, exit {r.ExitCode}): {command}{detail}");
            return 1;
        }

        private static string Tail(string s) =>
            s.Length <= 300 ? s : s.Substring(s.Length - 300);
    }
}
