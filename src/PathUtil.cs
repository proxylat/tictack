using System;

namespace TicTack
{
    public static class PathUtil
    {
        public static string EnsureExtended(string path)
        {
            if (path != null && path.Length > 240 && !path.StartsWith(@"\\?\"))
                return @"\\?\" + path;
            return path;
        }
    }
}
