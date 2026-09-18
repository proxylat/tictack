using System;
using System.IO;

namespace TicTack
{
    public static class PathUtil
    {
        public static string EnsureExtended(string path) =>
            EnsureExtended(path, OperatingSystem.IsWindows());

        internal static string EnsureExtended(string path, bool isWindows)
        {
            if (!isWindows || path.Length <= 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + path.Substring(2);
            // Manual check so the Windows branch stays unit-testable on Linux:
            // drive-letter root (C:...) gets the plain prefix; a relative path
            // must not be prefixed into an invalid extended path.
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
                return @"\\?\" + path;
            return path;
        }

        // Root-relative path used across the pipeline, deletion, and
        // versioning. On a prefix mismatch the path is returned unchanged.
        internal static string Relative(string path, string root)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root, comparison)) return path;
            // Boundary guard (only when the root has no trailing separator):
            // /a/src must not swallow /a/src2/f.
            var rootEndsWithSeparator = root.Length > 0 && (root[root.Length - 1] == '\\' || root[root.Length - 1] == '/');
            if (!rootEndsWithSeparator && path.Length > root.Length && path[root.Length] != '\\' && path[root.Length] != '/')
                return path;
            return path.Substring(root.Length).TrimStart('\\', '/');
        }
    }
}
