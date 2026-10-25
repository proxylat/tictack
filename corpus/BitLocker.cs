using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TicTack;

namespace Corpus
{
    // Item 12b: BitLocker orchestration around VSS capture. The 12a verdict
    // (proven on-box, re-verified 2026-10-25 probe-1231c on a scratch
    // encrypted VHD: suspended→VSS 0, unlocked+protected→VSS 0,
    // locked→VSS ReturnValue 4; our exact -protectors -disable/-enable
    // commands exit 0) is load-bearing: VSS snapshot creation succeeds on
    // unlocked-protected AND protectors-suspended volumes, and fails loud on
    // locked ones — so suspend suffices, decrypt is unnecessary, and locked
    // must refuse loud (never a silent unlock attempt).
    public enum BitLockerState
    {
        Unknown,
        Unprotected,
        ProtectedUnlocked,
        Suspended,
        Locked
    }

    // KeyIds are protector ID strings ({GUID}) ONLY — key material is never
    // queried, parsed, logged, or stored anywhere on this path.
    public sealed record BitLockerInfo(BitLockerState State, IReadOnlyList<string> KeyIds);

    public interface IBitLockerController
    {
        BitLockerInfo Query(string volume);
        void Suspend(string volume);
        void Resume(string volume);
    }

    // What the snapshot manifest records: the protection state DURING
    // capture (post-suspend — the on-disk state the image carries) plus the
    // protector key IDs. Implemented by VssBlockSource; file sources omit.
    public interface IBitLockerVolume
    {
        string? BitLockerState { get; }
        List<string>? BitLockerKeyIds { get; }
    }

    // manage-bde, not CIM Win32_EncryptableVolume: a plain promptless exe already driven via ProcessRunner by both existing probes (DiskImager, RestoreOrchestrator), with zero COM/WMI surface.
    public sealed class ManageBdeBitLocker : IBitLockerController
    {
        public static ManageBdeBitLocker Instance { get; } = new();

        private ManageBdeBitLocker()
        {
        }

        public BitLockerInfo Query(string volume)
        {
            if (!OperatingSystem.IsWindows())
                return new BitLockerInfo(BitLockerState.Unknown, Array.Empty<string>());
            string status;
            try
            {
                var r = ProcessRunner.RunAsync($"manage-bde -status {volume}", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    return new BitLockerInfo(BitLockerState.Unknown, Array.Empty<string>());
                status = r.Stdout;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return new BitLockerInfo(BitLockerState.Unknown, Array.Empty<string>());
            }
            var ids = QueryKeyIds(volume);
            return new BitLockerInfo(BitLockerParser.ParseState(status, ids), ids);
        }

        public void Suspend(string volume)
        {
            // Plain -disable (single reboot): protection auto-resumes on the
            // next boot even if we crash before Resume — no indefinite hole.
            RunOrThrow($"manage-bde -protectors -disable {volume}", volume, "suspend");
        }

        public void Resume(string volume)
        {
            RunOrThrow($"manage-bde -protectors -enable {volume}", volume, "re-enable");
        }

        private static void RunOrThrow(string command, string volume, string verb)
        {
            try
            {
                var r = ProcessRunner.RunAsync(command, AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                if (!r.TimedOut && r.ExitCode == 0)
                    return;
                throw new InvalidOperationException($"BitLocker {verb} failed for volume {volume} ({command} exited {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                if (ex is InvalidOperationException && ex.Message.StartsWith("BitLocker ", StringComparison.Ordinal))
                    throw;
                throw new InvalidOperationException($"BitLocker {verb} failed for volume {volume} ({ex.Message})", ex);
            }
        }

        // Best-effort: IDs enrich the manifest, their absence never blocks.
        private static IReadOnlyList<string> QueryKeyIds(string volume)
        {
            try
            {
                var r = ProcessRunner.RunAsync($"manage-bde -protectors -get {volume}", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                if (r.TimedOut || r.ExitCode != 0)
                    return Array.Empty<string>();
                return BitLockerParser.ParseKeyIds(r.Stdout);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return Array.Empty<string>();
            }
        }
    }

    // Pure parsing (Linux-testable): -status carries lock/protection state,
    // -protectors -get carries the IDs. Only "ID:" lines are ever retained
    // from the protectors output — the rest is discarded, never logged.
    internal static class BitLockerParser
    {
        internal static BitLockerState ParseState(string statusStdout, IReadOnlyList<string> keyIds)
        {
            // Locked check mirrors both existing probes (DiskImager,
            // RestoreOrchestrator): "Locked" without "Unlocked" (the latter
            // is a substring trap — "Unlocked" contains "locked").
            if (statusStdout.Contains("Locked", StringComparison.OrdinalIgnoreCase)
                && !statusStdout.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                return BitLockerState.Locked;
            if (statusStdout.Contains("Protection On", StringComparison.OrdinalIgnoreCase))
                return BitLockerState.ProtectedUnlocked;
            // Protection Off is ambiguous (suspended vs never-encrypted):
            // surviving key protectors mean suspended, none mean unprotected.
            return keyIds.Count > 0 ? BitLockerState.Suspended : BitLockerState.Unprotected;
        }

        internal static List<string> ParseKeyIds(string protectorsStdout)
        {
            var ids = new List<string>();
            foreach (var line in protectorsStdout.Split('\n'))
            {
                var idx = line.IndexOf("ID:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;
                var id = line.Substring(idx + 3).Trim().TrimEnd('\r');
                if (id.StartsWith("{", StringComparison.Ordinal) && id.EndsWith("}", StringComparison.Ordinal)
                    && !ids.Contains(id, StringComparer.Ordinal))
                    ids.Add(id);
            }
            return ids;
        }
    }

    // Arm before VssSnapshot.Create, Dispose after the shadow is torn down:
    // suspend covers the whole capture, resume runs in Dispose so it fires
    // even when capture throws (VssBlockSource disposes the scope on both
    // the catch path and the normal path). Every transition logs at Info.
    public sealed class BitLockerCaptureScope : IDisposable, IBitLockerVolume
    {
        private readonly IBitLockerController _controller;
        private readonly string _volume;
        private readonly ILogger _log;
        private readonly bool _suspendedByUs;
        private bool _disposed;

        // State DURING capture (post-suspend), for the manifest record.
        public string CaptureState { get; }
        public List<string>? CaptureKeyIds { get; }

        string? IBitLockerVolume.BitLockerState => CaptureState;
        List<string>? IBitLockerVolume.BitLockerKeyIds => CaptureKeyIds;

        private BitLockerCaptureScope(IBitLockerController controller, string volume, ILogger log, bool suspendedByUs, string captureState, List<string>? captureKeyIds)
        {
            _controller = controller;
            _volume = volume;
            _log = log;
            _suspendedByUs = suspendedByUs;
            CaptureState = captureState;
            CaptureKeyIds = captureKeyIds;
        }

        public static BitLockerCaptureScope Arm(string volume, IBitLockerController controller, ILogger log)
        {
            ArgumentException.ThrowIfNullOrEmpty(volume);
            ArgumentNullException.ThrowIfNull(controller);
            ArgumentNullException.ThrowIfNull(log);
            var info = controller.Query(volume);
            var keyIds = info.KeyIds.Count > 0 ? new List<string>(info.KeyIds) : null;
            switch (info.State)
            {
                case BitLockerState.Locked:
                    // Refusal style reuses the restore preflight (and the
                    // disk-capture probe): name the volume + the unlock
                    // command, throw before a single byte is imaged.
                    throw new InvalidDataException($"refusing VSS snapshot: volume {volume} is BitLocker-locked (unlock with manage-bde -unlock {volume} first)");
                case BitLockerState.ProtectedUnlocked:
                    log.Info($"BitLocker: volume {volume} is protected-unlocked, suspending protectors for capture");
                    try
                    {
                        controller.Suspend(volume);
                    }
                    catch (Exception ex)
                    {
                        // 12a: VSS captures protected-unlocked volumes fine, so
                        // a suspend failure degrades to Warn+proceed — refusing
                        // would lose the backup for nothing. Re-query first: a
                        // half-applied suspend must be resumed immediately and
                        // the manifest must name the honest end state.
                        log.Warn($"BitLocker: suspend failed on {volume}, capturing protected-unlocked ({ex.Message})");
                        var captureState = info.State;
                        if (controller.Query(volume).State == BitLockerState.Suspended)
                        {
                            log.Warn($"BitLocker: suspend half-applied on {volume}, re-enabling immediately");
                            try
                            {
                                controller.Resume(volume);
                            }
                            catch (Exception rex)
                            {
                                log.Error($"BitLocker: FAILED to re-enable protectors on {volume} — run 'manage-bde -protectors -enable {volume}' manually", rex);
                                captureState = BitLockerState.Suspended;
                            }
                        }
                        return new BitLockerCaptureScope(controller, volume, log, false, captureState.ToString(), keyIds);
                    }
                    log.Info($"BitLocker: protectors suspended on {volume}, capturing while suspended");
                    return new BitLockerCaptureScope(controller, volume, log, true, BitLockerState.Suspended.ToString(), keyIds);
                case BitLockerState.Suspended:
                    log.Info($"BitLocker: volume {volume} is already suspended, leaving as-is (no re-enable on teardown)");
                    return new BitLockerCaptureScope(controller, volume, log, false, info.State.ToString(), keyIds);
                case BitLockerState.Unprotected:
                    log.Info($"BitLocker: volume {volume} is unprotected, no suspension needed");
                    return new BitLockerCaptureScope(controller, volume, log, false, info.State.ToString(), keyIds);
                default:
                {
                    // Unknown is not Silent: BitLocker absence/disability is
                    // provable (no fvevol = no encryption possible), so only
                    // warn when the state is genuinely indeterminate.
                    var env = VssHealth.QueryBitLockerEnv();
                    if (VssHealth.IsAbsentOrDisabled(env))
                    {
                        log.Info($"BitLocker not present or disabled by policy ({VssHealth.Summarize(env)}); capturing {volume} without suspension");
                        return new BitLockerCaptureScope(controller, volume, log, false, BitLockerState.Unprotected.ToString(), keyIds);
                    }
                    log.Warn($"BitLocker: could not determine protection state of {volume} ({VssHealth.Summarize(env)}), proceeding without suspension");
                    return new BitLockerCaptureScope(controller, volume, log, false, info.State.ToString(), keyIds);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_suspendedByUs) return;
            try
            {
                _controller.Resume(_volume);
                _log.Info($"BitLocker: protectors re-enabled on {_volume}");
            }
            catch (Exception)
            {
                // One immediate retry before the loud manual line.
                try
                {
                    _controller.Resume(_volume);
                    _log.Info($"BitLocker: protectors re-enabled on {_volume} (second attempt)");
                }
                catch (Exception ex)
                {
                    // Dispose must not throw (it would mask a capture failure):
                    // Error is the loud channel — file log + alert path.
                    _log.Error($"BitLocker: FAILED to re-enable protectors on {_volume} — run 'manage-bde -protectors -enable {_volume}' manually", ex);
                }
            }
        }
    }
}
