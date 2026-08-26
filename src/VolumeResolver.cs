using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TicTack
{
    public static class VolumeResolver
    {
        private static readonly Regex VolumePattern = new Regex(@"\[([^\]]+)\]", RegexOptions.Compiled);

        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var map = BuildVolumeMap();
            if (map.Count == 0) return path;
            return VolumePattern.Replace(path, m =>
            {
                var label = m.Groups[1].Value;
                return map.TryGetValue(label, out var root) ? root : m.Value;
            });
        }

        public static void ResolveConfig(TicTackConfig cfg)
        {
            if (cfg == null || cfg.Sources == null) return;

            foreach (var src in cfg.Sources)
            {
                src.Path = Resolve(src.Path);
                src.Destination = Resolve(src.Destination);
                if (src.Sync != null && src.Sync.Versioning != null && src.Sync.Versioning.Path != null)
                    src.Sync.Versioning.Path = Resolve(src.Sync.Versioning.Path);
                if (src.Sync != null && src.Sync.Deletion != null && src.Sync.Deletion.Path != null)
                    src.Sync.Deletion.Path = Resolve(src.Sync.Deletion.Path);
            }

            if (cfg.Logging != null && cfg.Logging.Path != null)
                cfg.Logging.Path = Resolve(cfg.Logging.Path);

            foreach (var job in cfg.Jobs ?? Enumerable.Empty<JobConfig>())
            {
                if (job.WorkingDir != null)
                    job.WorkingDir = Resolve(job.WorkingDir);
            }

            if (cfg.ExternalDrives != null && cfg.ExternalDrives.WorkingDir != null)
                cfg.ExternalDrives.WorkingDir = Resolve(cfg.ExternalDrives.WorkingDir);
        }

        private static Dictionary<string, string> BuildVolumeMap()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel))
                    .GroupBy(d => d.VolumeLabel, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(d => d.VolumeLabel,
                        d => d.RootDirectory.FullName.TrimEnd('\\'),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, string>(); }
        }
    }
}
