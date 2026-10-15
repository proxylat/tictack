using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Item 7: SureBackup-lite — scheduled VHDX export + Hyper-V boot + health
    // check, the "images are not write-only" proof gate. The Hyper-V runner is
    // Windows-gated with a fake on Linux; RunScheduled is the minimal
    // scheduler hook (one call; src/Scheduler.cs wiring is a follow-up).
    public sealed record BootVerifyOptions(
        string VhdxPath,
        string VmName = "corpus-bootverify",
        int TimeoutSeconds = 300);

    public interface IHyperVRunner
    {
        // Attach VhdxPath as VmName and start it. Returns the VM id.
        string Boot(string vhdxPath, string vmName);
        // Heartbeat/integration-services health. Returns a detail line.
        string HealthCheck(string vmId);
        void Cleanup(string vmId);
    }

    // Real path: Hyper-V PowerShell, offline (no vSwitch — the no-network
    // principle holds for verify VMs too). Windows-gated, fails loud.
    public sealed class WindowsHyperVRunner : IHyperVRunner
    {
        private readonly ILogger _log;
        private readonly int _timeoutMs;

        public WindowsHyperVRunner(ILogger log, int timeoutSeconds = 300)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            _timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
        }

        private static void RequireWindows()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("boot-verify needs Windows (Hyper-V).");
        }

        public string Boot(string vhdxPath, string vmName)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(vhdxPath);
            ArgumentNullException.ThrowIfNull(vmName);
            _log.Info($"boot-verify: starting {vmName} from {vhdxPath}");
            var r = ProcessRunner.RunAsync(
                $"powershell -NoProfile -Command \"New-VM -Name '{vmName}' -VHDPath '{vhdxPath}' -Generation 2 -MemoryStartupBytes 2GB | Out-Null; Start-VM -Name '{vmName}'\"",
                Path.GetTempPath(), redirect: true, timeoutMs: _timeoutMs).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"VM start failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            return vmName;
        }

        public string HealthCheck(string vmId)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(vmId);
            var r = ProcessRunner.RunAsync(
                $"powershell -NoProfile -Command \"(Get-VM -Name '{vmId}').Heartbeat\"",
                Path.GetTempPath(), redirect: true, timeoutMs: _timeoutMs).GetAwaiter().GetResult();
            var beat = r.Stdout.Trim();
            if (r.TimedOut || r.ExitCode != 0 || !beat.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"VM heartbeat is '{beat}' — not healthy (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            return "heartbeat " + beat;
        }

        // Best-effort by design: a stuck VM must not fail the verdict or
        // throw out of a finally. Warns, never throws.
        public void Cleanup(string vmId)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(vmId);
            var r = ProcessRunner.RunAsync(
                $"powershell -NoProfile -Command \"Stop-VM -Name '{vmId}' -TurnOff -ErrorAction SilentlyContinue; Remove-VM -Name '{vmId}' -Force -ErrorAction SilentlyContinue\"",
                Path.GetTempPath(), redirect: true, timeoutMs: _timeoutMs).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                _log.Warn($"boot-verify: cleanup of {vmId} failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
        }

        private static string Tail(string s) =>
            s.Length <= 500 ? s : s.Substring(s.Length - 500);
    }

    // Fake for Linux-green tests: records calls, canned health.
    public sealed class FakeHyperVRunner : IHyperVRunner
    {
        public readonly List<string> Calls = new();
        public string Health = "heartbeat OkApplicationsHealthy";
        public bool FailBoot;
        public bool FailHealth;

        public string Boot(string vhdxPath, string vmName)
        {
            Calls.Add($"Boot:{vmName}");
            if (FailBoot)
                throw new InvalidDataException("fake VM start failure");
            return "fake-" + vmName;
        }

        public string HealthCheck(string vmId)
        {
            Calls.Add("HealthCheck:" + vmId);
            if (FailHealth)
                throw new InvalidDataException("fake heartbeat NoContact");
            return Health;
        }

        public void Cleanup(string vmId) =>
            Calls.Add("Cleanup:" + vmId);
    }

    public static class BootVerifyOrchestrator
    {
        // Full gate: VHDX exists → boot → health → cleanup (always). 0 = the
        // image booted and answered healthy; 1 = anything else.
        public static int Verify(BootVerifyOptions opts, IHyperVRunner runner, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(opts);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrEmpty(opts.VhdxPath) || !File.Exists(opts.VhdxPath))
            {
                log.Error($"boot-verify: no VHDX at {opts.VhdxPath} (export one first)");
                return 1;
            }
            string vm;
            try
            {
                vm = runner.Boot(opts.VhdxPath, opts.VmName);
            }
            catch (Exception ex)
            {
                log.Error("boot-verify: VM failed to start", ex);
                return 1;
            }
            try
            {
                var health = runner.HealthCheck(vm);
                log.Info("boot-verify: image booted and healthy (" + health + ")");
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("boot-verify: health check failed", ex);
                return 1;
            }
            finally
            {
                try
                {
                    runner.Cleanup(vm);
                }
                catch (Exception ex)
                {
                    log.Warn("boot-verify: cleanup failed: " + ex.Message);
                }
            }
        }

        // Minimal scheduler hook: export first (caller-supplied, store-owned),
        // then the gate. A TimerScheduler job (or any timer) calls this one
        // line — no other wiring lives in this item.
        public static int RunScheduled(Func<string> exportVhdx, string vmName, IHyperVRunner runner, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(exportVhdx);
            ArgumentNullException.ThrowIfNull(vmName);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            string vhdx;
            try
            {
                vhdx = exportVhdx();
            }
            catch (Exception ex)
            {
                log.Error("boot-verify: scheduled export failed", ex);
                return 1;
            }
            return Verify(new BootVerifyOptions(vhdx, vmName), runner, log);
        }
    }
}
