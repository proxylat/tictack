using System;

namespace TicTack
{
    public static class PathUtil
    {
        public static string EnsureExtended(string path) =>
            EnsureExtended(path, OperatingSystem.IsWindows());

        internal static string EnsureExtended(string path, bool isWindows)
        {
            if (!isWindows)
                return path;
            if (path.Length > 240 && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return @"\\?\" + path;
            return path;
        }
    }
}
