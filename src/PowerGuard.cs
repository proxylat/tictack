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
                        if (FilesMatch(source, tmp))
                        {
                            try { File.Move(tmp, dest); log.Warn(string.Format("Recovered {0} from validated temp", Path.GetFileName(dest))); continue; }
                            catch { }
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

        private static bool FilesMatch(string source, string temp)
        {
            if (!File.Exists(source)) return false;
            return FileHasher.SameContent(source, temp);
        }
    }
}
