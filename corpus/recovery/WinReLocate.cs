using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Item 15a: winre.wim LOCATE logic (VM proof later). Three sources in
    // order: reagentc /info output → inbox known path → per-drive recovery
    // partition scan. NEVER assumes C:\Recovery or that the file exists:
    // every candidate is probed, and a miss returns null so the caller falls
    // back to the ADK path or refuses honestly. Windows syscalls sit behind
    // IWinReProbe; Linux tests run the fake.
    public sealed record WinReLocation(string Path, string Source);

    public interface IWinReProbe
    {
        // reagentc /info stdout, or null when unavailable (non-Windows,
        // unelevated, command failed). Never throws for a failed command.
        string? ReadReagentcOutput();
        string SystemRoot { get; }
        string[] DriveRoots { get; }
        bool FileExists(string path);
    }

    // Real path: reagentc + live filesystem, Windows-gated.
    public sealed class WindowsWinReProbe : IWinReProbe
    {
        public string? ReadReagentcOutput()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("winre locate needs Windows (reagentc).");
            var r = ProcessRunner.RunAsync("reagentc /info", Path.GetTempPath(), redirect: true, timeoutMs: 60000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout))
                return null;
            return r.Stdout;
        }

        public string SystemRoot =>
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        public string[] DriveRoots
        {
            get
            {
                var drives = DriveInfo.GetDrives();
                var roots = new string[drives.Length];
                for (var i = 0; i < drives.Length; i++)
                    roots[i] = drives[i].Name;
                return roots;
            }
        }

        public bool FileExists(string path) => File.Exists(path);
    }

    // Fake for Linux-green tests: canned reagentc output + file set.
    public sealed class FakeWinReProbe : IWinReProbe
    {
        public string? ReagentcOutput;
        public string SystemRootValue = "";
        public string[] DriveRootsValue = [];
        public readonly HashSet<string> ExistingFiles = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadReagentcOutput() => ReagentcOutput;
        public string SystemRoot => SystemRootValue;
        public string[] DriveRoots => DriveRootsValue;
        public bool FileExists(string path) => ExistingFiles.Contains(path);
    }

    public static class WinReLocate
    {
        // reagentc /info shape (English): "Windows RE location:
        // \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE" when
        // enabled, empty after the colon when disabled. Localized labels miss
        // the match and fall through to the known-path + partition scan.
        public static string? ParseReagentcLocation(string? output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            foreach (var line in output.Split('\n'))
            {
                var idx = line.IndexOf("Windows RE location:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var value = line.Substring(idx + "Windows RE location:".Length).Trim().TrimEnd('\r');
                return string.IsNullOrEmpty(value) ? null : value;
            }
            return null;
        }

        // Ordered, de-duplicated candidates: reagentc-derived first, inbox
        // known path second, per-drive recovery scan last. No single path is
        // ever assumed — Locate probes each via IWinReProbe.FileExists.
        public static WinReLocation[] CandidatePaths(string? reagentcLocation, string systemRoot, string[] driveRoots)
        {
            ArgumentNullException.ThrowIfNull(systemRoot);
            ArgumentNullException.ThrowIfNull(driveRoots);
            var out_ = new List<WinReLocation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string source)
            {
                if (seen.Add(path)) out_.Add(new WinReLocation(path, source));
            }
            if (!string.IsNullOrEmpty(reagentcLocation))
            {
                var loc = reagentcLocation.Trim().TrimEnd('\\', '/');
                if (loc.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                {
                    // VM-proven (Win11 26200, elevated): GLOBALROOT recovery
                    // paths open fine from user mode (File.Exists true,
                    // OpenRead yields the WIM bytes). Skipping them strands
                    // stock boxes whose recovery partition has no drive
                    // letter — probe, never skip. A miss falls through to
                    // known-path + scan + the ADK/refusal log.
                    Add(Path.Combine(loc, "winre.wim"), "reagentc");
                }
                else if (loc.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    Add(Path.Combine(loc.Substring(4), "winre.wim"), "reagentc");
                }
                else if ((loc.Length >= 3 && loc[1] == ':' && char.IsAsciiLetter(loc[0])) || Path.IsPathRooted(loc))
                {
                    // Drive-letter (real reagentc) or a rooted test path.
                    Add(Path.Combine(loc, "winre.wim"), "reagentc");
                }
            }
            Add(Path.Combine(systemRoot, "System32", "Recovery", "winre.wim"), "known-path");
            foreach (var root in driveRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                Add(Path.Combine(root, "Recovery", "WindowsRE", "winre.wim"), "partition-scan");
            }
            return out_.ToArray();
        }

        // First existing candidate wins. Null = not found: the caller builds
        // with the ADK path or refuses honestly (never a guessed path).
        public static WinReLocation? Locate(IWinReProbe probe, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(log);
            var output = probe.ReadReagentcOutput();
            if (output == null)
                log.Info("winre: reagentc unavailable (needs Windows + elevation?), probing known paths + recovery partitions");
            var cands = CandidatePaths(ParseReagentcLocation(output), probe.SystemRoot, probe.DriveRoots);
            foreach (var c in cands)
            {
                if (probe.FileExists(c.Path))
                {
                    log.Info($"winre.wim found via {c.Source}: {c.Path}");
                    return c;
                }
            }
            log.Error($"winre.wim not found (probed {cands.Length} candidates: reagentc + known-path + partition-scan) — build with the ADK path (ADK + WinPE add-on: https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install) or enable Windows RE (reagentc /enable)");
            return null;
        }
    }
}
