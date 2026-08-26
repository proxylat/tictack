using System;
using System.IO;

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
                    CleanDir(src.Destination, log);
                if (src.Sync != null)
                {
                    if (src.Sync.Versioning != null && !string.IsNullOrEmpty(src.Sync.Versioning.Path)
                        && Directory.Exists(src.Sync.Versioning.Path))
                        CleanDir(src.Sync.Versioning.Path, log);
                    if (src.Sync.Deletion != null && !string.IsNullOrEmpty(src.Sync.Deletion.Path)
                        && Directory.Exists(src.Sync.Deletion.Path))
                        CleanDir(src.Sync.Deletion.Path, log);
                }
            }
        }

        private static void CleanDir(string dir, ILogger log)
        {
            try
            {
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tictack.tmp",
                    SearchOption.AllDirectories))
                {
                    var dest = tmp.Substring(0, tmp.Length - ".tictack.tmp".Length);
                    if (!File.Exists(dest))
                    {
                        try { File.Move(tmp, dest); log.Warn(string.Format("Recovered {0} from temp", Path.GetFileName(dest))); continue; }
                        catch { }
                    }
                    try { File.Delete(tmp); log.Warn(string.Format("Cleaned stale temp {0}", Path.GetFileName(tmp))); }
                    catch (Exception ex) { log.Warn(string.Format("Could not clean {0}: {1}", Path.GetFileName(tmp), ex.Message)); }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { log.Warn(string.Format("PowerGuard: {0}: {1}", dir, ex.Message)); }
        }
    }
}
