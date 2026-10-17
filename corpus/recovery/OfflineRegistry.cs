using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Prepare-universal cluster item 8: one offline registry value write.
    // reg.exe load → add → unload against the target hive file; the mount
    // name is fixed (HKLM\corpus-tmp) so a second concurrent run refuses on
    // load failure — acceptable, no locking scheme (prepare-universal is a
    // single-operator flow). Unload runs in finally whenever the load
    // succeeded: a hive is never left mounted, even when the add fails.
    // No online path exists here by design (live-hive writes are the OS's
    // own regedit/Group Policy surface, not this op).
    // Trust boundary: valueName/data cross into cmd.exe unquoted (pinned
    // shape below); a double-quote in either refuses rather than escaping
    // the command. Values with spaces are on-box-owed (reg arg parsing of
    // quoted /d wants a VM check before trusting).
    public sealed record RegistryResult(string Command, int ExitCode, string Output);

    public interface IRegistryRunner
    {
        RegistryResult Run(string command);
    }

    // Real path: inbox reg.exe, Windows-gated.
    public sealed class WindowsRegistryRunner : IRegistryRunner
    {
        public RegistryResult Run(string command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("offline registry needs Windows (inbox reg.exe).");
            var (code, stdout, stderr, timedOut) = ProcessRunner.RunAsync(
                command, Path.GetTempPath(), redirect: true, timeoutMs: 600000).GetAwaiter().GetResult();
            if (timedOut)
                return new RegistryResult(command, -1, "timed out");
            return new RegistryResult(command, code, stdout + stderr);
        }
    }

    // Fake for Linux-green tests: records commands, replays queued results.
    public sealed class FakeRegistryRunner : IRegistryRunner
    {
        public readonly List<string> Calls = new();
        public Func<string, RegistryResult>? Handler;

        public RegistryResult Run(string command)
        {
            Calls.Add(command);
            return Handler != null ? Handler(command) : new RegistryResult(command, 0, string.Empty);
        }
    }

    public static class OfflineRegistry
    {
        private const string Mount = "HKLM\\corpus-tmp";

        // 0 = written (or dry-run plan); 1 = guard refused or reg failed.
        public static int SetValue(string hivePath, string keyPath, string valueName, string regType, string data, IRegistryRunner runner, ILogger log, bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(hivePath);
            ArgumentNullException.ThrowIfNull(keyPath);
            ArgumentNullException.ThrowIfNull(valueName);
            ArgumentNullException.ThrowIfNull(regType);
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(hivePath) || !File.Exists(hivePath))
            {
                log.Error($"offline registry: hive missing, refusing: {hivePath}");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(valueName) || string.IsNullOrWhiteSpace(regType))
            {
                log.Error("offline registry: value name and type are required, refusing");
                return 1;
            }
            if (valueName.Contains('"') || data.Contains('"'))
            {
                log.Error("offline registry: value and data must not contain double quotes, refusing");
                return 1;
            }
            // A trailing backslash would escape reg's closing quote (same
            // argv trap as DISM roots); a key never needs one — trim it.
            var key = $"{Mount}\\{keyPath}".TrimEnd('\\');
            var load = $"reg load {Mount} {ProcessRunner.QuoteArg(hivePath)}";
            var add = $"reg add \"{key}\" /v {valueName} /t {regType} /d {data} /f";
            var unload = $"reg unload {Mount}";
            if (dryRun)
            {
                log.Info($"dry run: registry write planned, nothing applied: {load} && {add} && {unload}");
                return 0;
            }
            if (Run(load, runner, log) != 0)
            {
                // Fixed mount name: a second concurrent run lands here (its
                // load refuses) — acceptable, said in the header.
                log.Error($"offline registry: load refused (in use by another run?): {load}");
                return 1;
            }
            try
            {
                if (Run(add, runner, log) != 0)
                    return 1;
                log.Info($"offline registry: set {key}\\{valueName} ({regType})");
                return 0;
            }
            finally
            {
                if (Run(unload, runner, log) != 0)
                    log.Error($"offline registry: UNLOAD FAILED — {Mount} may still be mounted, unload it by hand: {unload}");
            }
        }

        private static int Run(string command, IRegistryRunner runner, ILogger log)
        {
            RegistryResult r;
            try
            {
                r = runner.Run(command);
            }
            catch (Exception ex)
            {
                log.Error($"offline registry failed: {command}", ex);
                return 1;
            }
            if (r.ExitCode == 0)
            {
                log.Info($"offline registry applied (exit 0): {command}");
                return 0;
            }
            log.Error($"offline registry failed (exit {r.ExitCode}): {command}");
            return 1;
        }
    }
}
