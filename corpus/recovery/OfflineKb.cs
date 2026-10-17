using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Prepare-universal cluster item 9: remove Windows packages by KB
    // number, OFFLINE ONLY. Each caller-named KB resolves via /Get-Packages
    // to a servicing identity containing the KB string, then
    // /Remove-Package removes that identity. No match is a WRN + continue
    // (KB not installed isn't failure); query/remove failures fail loud per
    // KB but continue with the next (ops-continue, like OfflineTrim).
    // OFFLINE-ONLY: the target must never be the live OS. The guard refuses
    // any dir resolving to the live Windows dir (System32-parent and
    // %SystemRoot% candidates); there is deliberately no online path —
    // wusa/DISM against the running OS has no method here (that surface is
    // live servicing, not prepare-universal).
    // DISM shapes (/Get-Packages listing, identity-contains-KB match) are
    // training-knowledge, on-box-unprobed: whether real identities embed KB
    // numbers at all is on-box-owed (VM diff; may need a CBS/WMI rework).
    public static class OfflineKb
    {
        // 0 = all KBs removed/absent (or plan / nothing to do);
        // 1 = guard refused or a query/removal failed.
        public static int RemoveKb(string offlineWindowsDir, IReadOnlyList<string> kbNumbers, ITrimRunner runner, ILogger log, bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(offlineWindowsDir);
            ArgumentNullException.ThrowIfNull(kbNumbers);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(offlineWindowsDir))
            {
                log.Error("offline kb: no --windows-dir given, refusing");
                return 1;
            }
            if (IsLiveWindowsDir(offlineWindowsDir, LiveWindowsCandidates()))
            {
                log.Error($"offline kb: {offlineWindowsDir} is the LIVE Windows dir — offline-only op, refusing (never service the running OS from prepare-universal)");
                return 1;
            }
            var hive = Path.Combine(offlineWindowsDir, "System32", "config", "SYSTEM");
            if (!File.Exists(hive))
            {
                log.Error($"offline kb: not a Windows tree (no {hive}), refusing");
                return 1;
            }
            var image = Path.GetDirectoryName(offlineWindowsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(image))
            {
                log.Error($"offline kb: cannot derive the image root from {offlineWindowsDir}");
                return 1;
            }
            if (kbNumbers.Count == 0)
            {
                log.Info("offline kb: no KBs given, nothing to do");
                return 0;
            }
            var failures = 0;
            foreach (var kb in kbNumbers)
            {
                if (string.IsNullOrWhiteSpace(kb))
                    continue;
                if (dryRun)
                {
                    log.Info($"dry run: KB removal planned for {kb} (identity resolved on-box), nothing applied");
                    continue;
                }
                if (!TryRemoveOne(image, kb, runner, log))
                    failures++;
            }
            return failures == 0 ? 0 : 1;
        }

        private static bool TryRemoveOne(string image, string kb, ITrimRunner runner, ILogger log)
        {
            var list = $"dism /Image:{ProcessRunner.QuoteArg(image)} /Get-Packages";
            TrimResult r;
            try
            {
                r = runner.Run(list);
            }
            catch (Exception ex)
            {
                log.Error($"offline kb: list failed for {kb}: {list}", ex);
                return false;
            }
            if (r.ExitCode != 0)
            {
                log.Error($"offline kb: list failed for {kb} (exit {r.ExitCode}): {list}");
                return false;
            }
            var matches = ParseIdentities(r.Output, kb);
            if (matches.Count == 0)
            {
                log.Warn($"offline kb: no installed package contains {kb}, skipping (not installed isn't failure)");
                return true;
            }
            var ok = true;
            foreach (var id in matches)
            {
                var remove = $"dism /Image:{ProcessRunner.QuoteArg(image)} /Remove-Package /PackageName:{id}";
                TrimResult rr;
                try
                {
                    rr = runner.Run(remove);
                }
                catch (Exception ex)
                {
                    log.Error($"offline kb: remove failed for {id}: {remove}", ex);
                    ok = false;
                    continue;
                }
                if (rr.ExitCode == 0)
                    log.Info($"offline kb removed ({kb}, exit 0): {remove}");
                else
                {
                    log.Error($"offline kb: remove failed for {id} (exit {rr.ExitCode}): {remove}");
                    ok = false;
                }
            }
            return ok;
        }

        // /Get-Packages lists "Package Identity : <id>" lines; keep the ids
        // containing the caller-named KB string (case-insensitive).
        internal static List<string> ParseIdentities(string output, string kb)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(output))
                return ids;
            const string prefix = "Package Identity :";
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = line.Substring(prefix.Length).Trim();
                if (id.Length > 0 && id.Contains(kb, StringComparison.OrdinalIgnoreCase))
                    ids.Add(id);
            }
            return ids;
        }

        // Live-OS candidates: the System32 folder's parent (C:\Windows),
        // plus %SystemRoot%. Empty off-Windows (no live dir there): the
        // guard then cannot match and passes through — correct, there is no
        // live Windows dir on Linux.
        internal static IEnumerable<string> LiveWindowsCandidates()
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrWhiteSpace(system))
            {
                var parent = Path.GetDirectoryName(system.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent))
                    yield return parent;
            }
            var root = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(root))
                yield return root;
        }

        internal static bool IsLiveWindowsDir(string dir, IEnumerable<string> liveCandidates)
        {
            ArgumentNullException.ThrowIfNull(dir);
            ArgumentNullException.ThrowIfNull(liveCandidates);
            if (string.IsNullOrWhiteSpace(dir))
                return false;
            // Trim both separators explicitly (not just the platform pair):
            // live candidates are Windows-shaped (%SystemRoot%) even when
            // this comparison runs on Linux.
            string Norm(string p) =>
                Path.GetFullPath(p.TrimEnd('\\', '/'));
            var want = Norm(dir);
            foreach (var live in liveCandidates)
            {
                if (string.IsNullOrWhiteSpace(live))
                    continue;
                if (string.Equals(want, Norm(live), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
