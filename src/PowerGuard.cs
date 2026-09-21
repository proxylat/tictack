using System;
using System.IO;
using System.Threading;

namespace TicTack
{
    public static class PowerGuard
    {
        public static void Cleanup(TicTackConfig? cfg, ILogger log)
        {
            if (cfg == null || cfg.Sources == null) return;
            foreach (var src in cfg.Sources)
            {
                if (!string.IsNullOrEmpty(src.Destination) && Directory.Exists(src.Destination))
                    CleanDir(src.Destination, src.Path, log);
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
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tictack.tmp",
                    SearchOption.AllDirectories))
                {
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
                    try { File.Delete(tmp); log.Warn(string.Format("Cleaned stale temp {0}", Path.GetFileName(tmp))); }
                    catch (Exception ex) { log.Warn(string.Format("Could not clean {0}: {1}", Path.GetFileName(tmp), ex.Message)); }
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
                    log.Warn(string.Format("Recovered {0} from validated temp", Path.GetFileName(dest)));
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= 15)
                    {
                        log.Warn(string.Format("PowerGuard: could not recover {0}: {1} (temp kept for next startup)", Path.GetFileName(dest), ex.Message));
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
}
