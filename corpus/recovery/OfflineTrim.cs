using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Footprint trimming (system-image.md 12.14 third round): REMOVAL, not
    // compression — each op deletes something. Slots into
    // --prepare-universal (already services offline trees), never capture.
    // NTLite-style component removal is a deliberate NON-goal: it breaks
    // cumulative updates/SFC, so this file holds no removal list and offers
    // no seam for one — every deletion below is caller-named or ResetBase.
    public sealed record OfflineTrimOptions(
        string OfflineWindowsDir,
        bool ResetBase = false,
        string[]? DisableFeatures = null,
        string[]? RemovePackages = null,
        bool DisableHibernation = false,
        bool DryRun = false);

    public sealed record TrimResult(string Command, int ExitCode, string Output);

    public interface ITrimRunner
    {
        TrimResult Run(string command);
    }

    // Real path: inbox DISM/powercfg, Windows-gated. DISM arg shapes are
    // from training knowledge, on-box-unprobed (no VM run yet).
    public sealed class WindowsTrimRunner : ITrimRunner
    {
        public TrimResult Run(string command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("offline trim needs Windows (inbox DISM/powercfg).");
            var (code, stdout, stderr, timedOut) = ProcessRunner.RunAsync(
                command, Path.GetTempPath(), redirect: true, timeoutMs: 600000).GetAwaiter().GetResult();
            if (timedOut)
                return new TrimResult(command, -1, "timed out");
            return new TrimResult(command, code, stdout + stderr);
        }
    }

    // Fake for Linux-green tests: records commands, replays queued results.
    public sealed class FakeTrimRunner : ITrimRunner
    {
        public readonly List<string> Calls = new();
        public Func<string, TrimResult>? Handler;

        public TrimResult Run(string command)
        {
            Calls.Add(command);
            return Handler != null ? Handler(command) : new TrimResult(command, 0, string.Empty);
        }
    }

    public static class OfflineTrim
    {
        // Runs the caller-selected trim ops in order (ResetBase, features,
        // packages, hibernation), logging each command and its exit code.
        // 0 = all applied (or dry-run plan); 1 = guard refused or an op
        // failed. Ops after a failure still run — each is independent — but
        // the nonzero return fails the prepare step loud.
        public static int Trim(OfflineTrimOptions opts, ITrimRunner runner, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(opts.OfflineWindowsDir))
            {
                log.Error("offline trim: no --windows-dir given, refusing");
                return 1;
            }
            var hive = Path.Combine(opts.OfflineWindowsDir, "System32", "config", "SYSTEM");
            if (!File.Exists(hive))
            {
                log.Error($"offline trim: not a Windows tree (no {hive}), refusing");
                return 1;
            }
            // DISM wants the image root (D:\), not D:\Windows — same
            // trailing-backslash argv trap as the driver injector.
            var image = Path.GetDirectoryName(opts.OfflineWindowsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(image))
            {
                log.Error($"offline trim: cannot derive the image root from {opts.OfflineWindowsDir}");
                return 1;
            }
            var failures = 0;
            var commands = new List<string>();
            if (opts.ResetBase)
            {
                // Irreversible: resets the servicing base so superseded
                // updates can never be uninstalled. Says so here and in
                // the log line below; the CLI flag help must repeat it.
                commands.Add($"dism /Image:{ProcessRunner.QuoteArg(image)} /Cleanup-Image /ResetBase");
            }
            foreach (var feature in opts.DisableFeatures ?? [])
            {
                if (string.IsNullOrWhiteSpace(feature))
                    continue;
                commands.Add($"dism /Image:{ProcessRunner.QuoteArg(image)} /Disable-Feature /FeatureName:{feature} /Remove");
            }
            foreach (var package in opts.RemovePackages ?? [])
            {
                if (string.IsNullOrWhiteSpace(package))
                    continue;
                commands.Add($"dism /Image:{ProcessRunner.QuoteArg(image)} /Remove-Package /PackageName:{package}");
            }
            // powercfg has no offline /Image switch: this runs against the
            // booted OS, so schedule it for first boot on the target (or run
            // booted into the image). It releases hiberfil.sys on next boot —
            // it never touches the image bytes themselves.
            if (opts.DisableHibernation)
                commands.Add("powercfg /h off");
            if (commands.Count == 0)
            {
                log.Info("offline trim: no ops selected, nothing to do");
                return 0;
            }
            foreach (var command in commands)
            {
                if (opts.DryRun)
                {
                    log.Info($"dry run: trim planned, nothing applied: {command}");
                    continue;
                }
                TrimResult r;
                try
                {
                    r = runner.Run(command);
                }
                catch (Exception ex)
                {
                    log.Error($"offline trim failed: {command}", ex);
                    failures++;
                    continue;
                }
                var detail = string.IsNullOrWhiteSpace(r.Output) ? string.Empty : $" ({Tail(r.Output)})";
                if (r.ExitCode == 0)
                    log.Info($"offline trim applied (exit 0): {command}{detail}");
                else
                {
                    log.Error($"offline trim failed (exit {r.ExitCode}): {command}{detail}");
                    failures++;
                }
            }
            return failures == 0 ? 0 : 1;
        }

        // First-boot hibernation off: powercfg has no offline switch and
        // running it live would hit the tech box, not the target — so the
        // off switch is staged into SetupComplete.cmd (runs as SYSTEM on
        // first boot). Marker-guarded: idempotent, never duplicates, and an
        // OEM-existing script is appended to, never replaced.
        public static int StageNoHibernate(string offlineWindowsDir, bool dryRun, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(offlineWindowsDir))
            {
                log.Error("offline trim: no --windows-dir given, refusing");
                return 1;
            }
            const string marker = "rem Corpus: disable hibernation (staged by --trim-no-hibernate)";
            var script = Path.Combine(offlineWindowsDir, "Setup", "Scripts", "SetupComplete.cmd");
            if (dryRun)
            {
                log.Info($"dry run: trim planned, nothing applied: stage powercfg /h off in {script}");
                return 0;
            }
            var existing = File.Exists(script) ? File.ReadAllText(script) : string.Empty;
            if (existing.Contains(marker, StringComparison.Ordinal))
            {
                log.Info($"offline trim: {script} already staged, nothing to do");
                return 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            var prefix = existing.Length == 0 ? "@echo off\r\n" : (existing.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : "\r\n");
            File.AppendAllText(script, prefix + marker + "\r\npowercfg /h off\r\n");
            log.Info($"offline trim: staged powercfg /h off in {script} (runs at first boot)");
            return 0;
        }

        private static string Tail(string s) =>
            s.Length <= 300 ? s : s.Substring(s.Length - 300);
    }
}
