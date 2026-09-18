using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TicTack
{
    // Mount-table snapshot cache: GetDrives()+IsReady costs ~218us/26KB per
    // call, and the initial sync probes once per file. Fail-closed TTL:
    // a vanished drive reports ready for at most 30s (copy then errors),
    // a fresh drive waits at most 30s (start-retry loop covers it).
    internal static class DriveGuard
    {
        private static readonly Func<DriveInfo[]> DefaultProbe = static () => DriveInfo.GetDrives();
        private static readonly object CacheLock = new();
        private static List<(string Root, bool Ready)>? _cache;
        private static DateTime _cacheAt;
        private static Func<DriveInfo[]>? _cacheProbe;

        private static List<(string Root, bool Ready)> GetCachedDrives(Func<DriveInfo[]>? getDrives)
        {
            getDrives ??= DefaultProbe;
            lock (CacheLock)
            {
                if (_cache != null && ReferenceEquals(_cacheProbe, getDrives)
                    && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(30))
                    return _cache;
                _cache = getDrives()
                    .Select(d =>
                    {
                        bool ready;
                        try { ready = d.IsReady; } catch { ready = false; }
                        return (d.RootDirectory.FullName, ready);
                    })
                    .ToList();
                _cacheAt = DateTime.UtcNow;
                _cacheProbe = getDrives;
                return _cache;
            }
        }

        internal static bool IsReady(string path, Func<DriveInfo[]>? getDrives = null)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var mounts = GetCachedDrives(getDrives)
                    .Where(d => d.Ready && fullPath.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Root.Length)
                    .ToList();
                if (mounts.Count == 0) return false;
                if (OperatingSystem.IsLinux() && mounts[0].Root == "/"
                    && (fullPath == "/srv" || fullPath.StartsWith("/srv/", StringComparison.Ordinal)
                        || fullPath == "/mnt" || fullPath.StartsWith("/mnt/", StringComparison.Ordinal)
                        || fullPath == "/media" || fullPath.StartsWith("/media/", StringComparison.Ordinal)
                        || fullPath.StartsWith("/run/media/", StringComparison.Ordinal)))
                    return false;
                return true;
            }
            catch { return false; }
        }
    }
}
