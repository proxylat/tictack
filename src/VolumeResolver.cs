using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace TicTack
{
    public static partial class VolumeResolver
    {
        // Linear-time pattern, no ReDoS shape; 100ms timeout satisfies MA0009 without a pragma.
        [GeneratedRegex(@"\[(?<label>[^\]]+)\]", RegexOptions.None, matchTimeoutMilliseconds: 100)]
        private static partial Regex VolumePattern();

        public static string Resolve(string path)
        {
            return Resolve(path, BuildVolumeMap(GetVolumes(null)));
        }

        internal static string Resolve(string path, IEnumerable<(string label, string root)> volumes)
        {
            return Resolve(path, BuildVolumeMap(volumes));
        }

        private static string Resolve(string path, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (map.Count == 0) return path;
            return VolumePattern().Replace(path, m =>
            {
                var label = m.Groups[1].Value;
                return map.TryGetValue(label, out var root) ? root : m.Value;
            });
        }

        internal static string Resolve(string path, Dictionary<string, string> map, bool _) => Resolve(path, map);

        internal static IEnumerable<string> MatchLabels(string value)
        {
            foreach (Match m in VolumePattern().Matches(value))
                yield return m.Groups[1].Value;
        }

        private static IEnumerable<(string label, string root)> GetVolumes(ILogger? log)
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel))
                    .Select(d => (d.VolumeLabel, d.RootDirectory.FullName.TrimEnd('\\')))
                    .ToList();
            }
            catch (Exception ex)
            {
                log?.Warn("Volume table unavailable, [Label] paths left unresolved: " + ex.Message);
                return Array.Empty<(string label, string root)>();
            }
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
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }
    }
}
