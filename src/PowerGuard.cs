using System.Globalization;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TicTack
{
    public static class PowerGuard
    {
        public static void Cleanup(TicTackConfig cfg, ILogger log)
        {
            var allSources = new System.Collections.Generic.List<SourceConfig>(cfg.Sources ?? new System.Collections.Generic.List<SourceConfig>());
            allSources.AddRange(Config.ResolveRemoteSources(cfg));
            foreach (var src in allSources)
            {
                if (!string.IsNullOrEmpty(src.Destination) && Directory.Exists(src.Destination))
                    CleanDir(src.Destination, src.RemoteEndpoint == null ? src.Path : null, log);
                if (src.Sync != null)
                {
                    if (src.Sync.Versioning != null && !string.IsNullOrEmpty(src.Sync.Versioning.Path)
                        && Directory.Exists(src.Sync.Versioning.Path))
                        CleanDir(src.Sync.Versioning.Path, null, log);
                    if (src.Sync.Deletion != null && !string.IsNullOrEmpty(src.Sync.Deletion.Path)
                        && Directory.Exists(src.Sync.Deletion.Path))
                        CleanDir(src.Sync.Deletion.Path, null, log);
                }
            }
        }

        private static void CleanDir(string dir, string? sourceBase, ILogger log)
        {
            try
            {
                foreach (var res in Directory.EnumerateFiles(dir, "*.tictack.tmp.resume", SearchOption.AllDirectories))
                {
                    var tmpPair = res.Substring(0, res.Length - ".resume".Length);
                    if (!File.Exists(tmpPair))
                    {
                        try { File.Delete(res); } catch { }
                    }
                }
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tictack.tmp",
                    SearchOption.AllDirectories))
                {
                    if (File.Exists(tmp + ".resume"))
                    {
                        log.Debug("Preserving resumable temp " + Path.GetFileName(tmp));
                        continue;
                    }
                    var dest = tmp.Substring(0, tmp.Length - ".tictack.tmp".Length);
                    if (!File.Exists(dest) && sourceBase != null)
                    {
                        var source = Path.Combine(sourceBase, Path.GetRelativePath(dir, dest));
                        // Only a proven mismatch may be deleted. A temp that
                        // matches is recoverable; one that cannot be read right
                        // now (killed writer's handle or a scanner) is unknown,
                        // and unknown must never mean "stale".
                        if (CompareToSource(source, tmp) != Match.No)
                        {
                            TryRecover(tmp, dest, log);
                            continue;
                        }
                    }
                    try
                    {
                        File.Delete(tmp);
                        // Barrier for the unlink above: without it the stale
                        // tmp resurrects on crash. Benign (next startup
                        // re-cleans), so a failed sync never fails the clean.
                        if (!CopyAction.FlushDirectory(Path.GetDirectoryName(tmp)))
                            log.Debug("PowerGuard: directory sync failed after cleaning " + Path.GetFileName(tmp));
                        log.Info(string.Format(CultureInfo.InvariantCulture, "Cleaned stale temp {0}", Path.GetFileName(tmp)));
                    }
                    catch (Exception ex) { log.Warn(string.Format(CultureInfo.InvariantCulture, "Could not clean {0}: {1}", Path.GetFileName(tmp), ex.Message)); }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn(string.Format("PowerGuard: {0}: {1}", dir, ex.Message));
            }
            catch (Exception ex) { log.Warn(string.Format("PowerGuard: {0}: {1}", dir, ex.Message)); }
        }

        // A temp that matches its source is recoverable data. Windows can
        // transiently refuse the move while a killed writer's handle or a
        // scanner (Defender) is still releasing it, so retry for ~3s; if it
        // still fails, keep the temp for the next startup instead of deleting
        // the only copy.
        private static void TryRecover(string tmp, string dest, ILogger log)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, dest);
                    // Same barrier for the crash-recovery commit itself.
                    CopyAction.FlushDirectory(Path.GetDirectoryName(dest));
                    log.Info(string.Format(CultureInfo.InvariantCulture, "Recovered {0} from validated temp", Path.GetFileName(dest)));
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= 15)
                    {
                        log.Warn(string.Format(CultureInfo.InvariantCulture, "PowerGuard: could not recover {0}: {1} (temp kept for next startup)", Path.GetFileName(dest), ex.Message));
                        return;
                    }
                    Thread.Sleep(200);
                }
            }
        }

        private enum Match { Yes, No, Unknown }

        private static Match CompareToSource(string source, string temp)
        {
            if (!File.Exists(source)) return Match.No;
            try
            {
                var sourceInfo = new FileInfo(source);
                var tempInfo = new FileInfo(temp);
                if (!tempInfo.Exists || sourceInfo.Length != tempInfo.Length) return Match.No;
                return FileHasher.ComputeHex(source, null) == FileHasher.ComputeHex(temp, null)
                    ? Match.Yes
                    : Match.No;
            }
            catch
            {
                // Unreadable right now: do not destroy the only copy.
                return Match.Unknown;
            }
        }
    }

    // Sleep inhibitor for prevent_sleep, held for one run or sync pass.
    // Windows: thread execution state. Linux: systemd-inhibit companion.
    internal sealed class SleepInhibit : IDisposable
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        private Process? _companion;
        private bool _windowsHeld;
        private bool _disposed;

        public static SleepInhibit Hold(bool enabled, string? label, int timeoutMinutes, ILogger log)
        {
            var h = new SleepInhibit();
            if (!enabled) return h;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SetThreadExecutionState(EsContinuous | EsSystemRequired);
                    h._windowsHeld = true;
                }
                else
                {
                    var exe = FindOnPath("systemd-inhibit");
                    if (exe == null)
                    {
                        log.Debug("Sleep inhibit unavailable (no systemd-inhibit), running uninhibited");
                        return h;
                    }
                    var secs = Math.Min(Math.Max(timeoutMinutes, 1), 1440) * 60 + 300;
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("--what=sleep:idle");
                    psi.ArgumentList.Add("--who=TicTackSv");
                    psi.ArgumentList.Add("--why=" + (label ?? "tictack"));
                    psi.ArgumentList.Add("--mode=block");
                    psi.ArgumentList.Add("sleep");
                    psi.ArgumentList.Add(secs.ToString(CultureInfo.InvariantCulture));
                    h._companion = Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                log.Debug("Sleep inhibit failed, running uninhibited: " + ex.Message);
            }
            return h;
        }

        private static string? FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_windowsHeld)
            {
                try { SetThreadExecutionState(EsContinuous); } catch { }
                _windowsHeld = false;
            }
            if (_companion != null)
            {
                try { if (!_companion.HasExited) _companion.Kill(entireProcessTree: true); } catch { }
                try { _companion.Dispose(); } catch { }
                _companion = null;
            }
        }
    }
}
