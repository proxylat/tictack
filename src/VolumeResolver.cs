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
            return Resolve(path, GetVolumes());
        }

        internal static string Resolve(string path, IEnumerable<(string label, string root)> volumes)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var map = BuildVolumeMap(volumes);
            if (map.Count == 0) return path;
            return VolumePattern.Replace(path, m =>
            {
                var label = m.Groups[1].Value;
                return map.TryGetValue(label, out var root) ? root : m.Value;
            });
        }

        public static void ResolveConfig(TicTackConfig cfg)
        {
            ResolveConfig(cfg, GetVolumes());
        }

        internal static void ResolveConfig(TicTackConfig cfg, IEnumerable<(string label, string root)> volumes)
        {
            if (cfg == null || cfg.Sources == null) return;

            foreach (var src in cfg.Sources)
            {
                src.Path = Resolve(src.Path, volumes);
                for (var i = 0; i < src.Paths.Count; i++)
                    src.Paths[i] = Resolve(src.Paths[i], volumes);
                src.Destination = Resolve(src.Destination, volumes);
                src.StateDbPath = Resolve(src.StateDbPath, volumes);
                if (src.Sync != null && src.Sync.Versioning != null && src.Sync.Versioning.Path != null)
                    src.Sync.Versioning.Path = Resolve(src.Sync.Versioning.Path, volumes);
                if (src.Sync != null && src.Sync.Deletion != null && src.Sync.Deletion.Path != null)
                    src.Sync.Deletion.Path = Resolve(src.Sync.Deletion.Path, volumes);
            }

            if (cfg.Logging != null && cfg.Logging.Path != null)
                cfg.Logging.Path = Resolve(cfg.Logging.Path, volumes);
            if (cfg.Logging != null && cfg.Logging.AlertPath != null)
                cfg.Logging.AlertPath = Resolve(cfg.Logging.AlertPath, volumes);

            foreach (var job in cfg.Jobs ?? Enumerable.Empty<JobConfig>())
            {
                if (job.WorkingDir != null)
                    job.WorkingDir = Resolve(job.WorkingDir, volumes);
            }

            if (cfg.ExternalDrives != null && cfg.ExternalDrives.WorkingDir != null)
                cfg.ExternalDrives.WorkingDir = Resolve(cfg.ExternalDrives.WorkingDir, volumes);
        }

        private static IEnumerable<(string label, string root)> GetVolumes()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel))
                    .Select(d => (d.VolumeLabel, d.RootDirectory.FullName.TrimEnd('\\')))
                    .ToList();
            }
            catch { return Array.Empty<(string label, string root)>(); }
        }

        private static Dictionary<string, string> BuildVolumeMap(IEnumerable<(string label, string root)> volumes)
        {
            try
            {
                return volumes
                    .Where(v => !string.IsNullOrEmpty(v.label) && !string.IsNullOrEmpty(v.root))
                    .GroupBy(v => v.label, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(v => v.label,
                        v => v.root,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, string>(); }
        }
    }
}
