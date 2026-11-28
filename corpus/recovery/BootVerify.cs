using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
        int TimeoutSeconds = 300,
        int StartupMemoryMb = 2048,
        int Generation = 0,
        bool CheckFs = true);

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
        private readonly int _startupMemoryMb;

        public WindowsHyperVRunner(ILogger log, int timeoutSeconds = 300, int startupMemoryMb = 2048)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            _timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
            _startupMemoryMb = startupMemoryMb > 0 ? startupMemoryMb : 2048;
        }

        private static void RequireWindows()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("boot-verify needs Windows (Hyper-V).");
        }

        public string Boot(string vhdxPath, string vmName) => Boot(vhdxPath, vmName, generation: 0);

        public string Boot(string vhdxPath, string vmName, int generation)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(vhdxPath);
            ArgumentNullException.ThrowIfNull(vmName);
            var gen = generation is 1 or 2 ? generation : DetectVhdxGeneration(vhdxPath, _log);
            _log.Info($"boot-verify: starting {vmName} (Gen {gen}) from {vhdxPath}");
            var r = ProcessRunner.RunAsync(
                BuildBootCommand(vmName, vhdxPath, _startupMemoryMb, gen),
                Path.GetTempPath(), redirect: true, timeoutMs: _timeoutMs).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
            {
                try { Cleanup(vmName); } catch { }
                throw new InvalidDataException($"VM start failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            }
            return vmName;
        }

        internal static int DetectVhdxGeneration(string vhdxPath, ILogger log)
        {
            try
            {
                using var disk = new DiscUtils.Vhdx.Disk(vhdxPath, FileAccess.Read);
                var layout = PartitionTable.ReadFromStream(disk.Content, 0, disk.Capacity, log);
                return string.Equals(layout.Style, "mbr", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            }
            catch
            {
                return 2;
            }
        }

        public static void VerifyFilesystem(string vhdxPath, ILogger log)
        {
            RequireWindows();
            var ps = $"$ErrorActionPreference='Stop'; $m=Mount-VHD -Path '{vhdxPath}' -ReadOnly -Passthru; try {{ $d=$m|Get-Disk; $vols=Get-Partition -DiskNumber $d.Number | Get-Volume | Where-Object {{ $_.FileSystem -eq 'NTFS' -and $_.DriveLetter }}; foreach($v in $vols) {{ $dl=$v.DriveLetter+':'; $out=& chkdsk $dl /scan 2>&1; if ($LASTEXITCODE -ne 0) {{ throw ('chkdsk /scan failed on '+$dl+': '+($out -join ' ')) }}; $sys=Join-Path $dl 'Windows\\System32\\config\\SYSTEM'; $krn=Join-Path $dl 'Windows\\System32\\ntoskrnl.exe'; if (Test-Path (Join-Path $dl 'Windows')) {{ if (-not (Test-Path $sys) -or -not (Test-Path $krn)) {{ throw ('critical Windows boot files missing on '+$dl) }} }} }} }} finally {{ Dismount-VHD -Path '{vhdxPath}' }}";
            log.Info($"boot-verify: pre-boot chkdsk /scan on {vhdxPath}");
            var r = ProcessRunner.RunAsync($"powershell -NoProfile -Command \"{ps}\"", Path.GetTempPath(), redirect: true, timeoutMs: 300000).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"pre-boot filesystem check failed (exit {r.ExitCode}): {Tail(r.Stderr + r.Stdout)}");
        }

        // Single-quoted into PowerShell (spaces safe); a raw ' would break
        // out, so the verb rejects quotes in both values before this runs.
        internal static string BuildBootCommand(string vmName, string vhdxPath, long startupMemoryMb, int generation = 2) =>
            $"powershell -NoProfile -Command \"New-VM -Name '{vmName}' -VHDPath '{vhdxPath}' -Generation {generation} -MemoryStartupBytes {startupMemoryMb * 1024L * 1024L} | Out-Null; Start-VM -Name '{vmName}'\"";

        public string HealthCheck(string vmId)
        {
            RequireWindows();
            ArgumentNullException.ThrowIfNull(vmId);
            return PollHealth(() => QueryHeartbeatOnce(vmId), _timeoutMs, ms => Thread.Sleep(ms), _log);
        }

        // Poll to verdict: a booting VM reports NoContact for minutes before
        // the heartbeat IC answers. Any "Ok*" wins immediately; anything
        // else (NoContact, query errors, timeouts) retries to the deadline.
        internal static string PollHealth(Func<string> queryBeat, int timeoutMs, Action<int> sleepMs, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(queryBeat);
            ArgumentNullException.ThrowIfNull(sleepMs);
            ArgumentNullException.ThrowIfNull(log);
            var sw = Stopwatch.StartNew();
            var last = "";
            while (true)
            {
                try
                {
                    last = queryBeat();
                }
                catch (Exception ex)
                {
                    last = "query failed: " + ex.Message;
                }
                if (last.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
                    return "heartbeat " + last;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                    throw new InvalidDataException($"VM heartbeat is '{last}' — not healthy after {timeoutMs / 1000}s");
                log.Info($"boot-verify: heartbeat '{last}', waiting for healthy...");
                sleepMs(15000);
            }
        }

        private string QueryHeartbeatOnce(string vmId)
        {
            var r = ProcessRunner.RunAsync(
                $"powershell -NoProfile -Command \"(Get-VM -Name '{vmId}').Heartbeat\"",
                Path.GetTempPath(), redirect: true, timeoutMs: Math.Min(60000, _timeoutMs)).GetAwaiter().GetResult();
            if (r.TimedOut || r.ExitCode != 0)
                throw new InvalidDataException($"heartbeat query failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Tail(r.Stderr + r.Stdout)}");
            return FirstBeatLine(r.Stdout);
        }

        // First non-empty line: the query intermittently repeats the value
        // ("NoContact\nNoContact" seen on-box) and trails blank lines.
        internal static string FirstBeatLine(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return string.Empty;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length > 0) return line;
            }
            return string.Empty;
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
        public readonly IList<string> Calls = new List<string>();
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
            if (opts.CheckFs && OperatingSystem.IsWindows() && runner is WindowsHyperVRunner)
            {
                try
                {
                    WindowsHyperVRunner.VerifyFilesystem(opts.VhdxPath, log);
                }
                catch (Exception ex)
                {
                    log.Error("boot-verify: pre-boot filesystem check failed", ex);
                    return 1;
                }
            }
            string vm;
            try
            {
                vm = runner is WindowsHyperVRunner hv
                    ? hv.Boot(opts.VhdxPath, opts.VmName, opts.Generation)
                    : runner.Boot(opts.VhdxPath, opts.VmName);
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
