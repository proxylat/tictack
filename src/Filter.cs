using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TicTack
{
    public class CompositeFilter : IFileFilter
    {
        private readonly IFileFilter[] _filters;
        public CompositeFilter(IEnumerable<IFileFilter> filters)
        {
            _filters = filters != null ? filters.ToArray() : new IFileFilter[0];
        }
        public bool ShouldProcess(string fullPath)
        {
            foreach (var f in _filters)
                if (!f.ShouldProcess(fullPath)) return false;
            return true;
        }
    }

    public class PatternFilter : IFileFilter
    {
        private readonly string[] _patterns;

        public PatternFilter(string[] patterns)
        {
            _patterns = patterns ?? Array.Empty<string>();
        }

        public bool ShouldProcess(string fullPath)
        {
            if (_patterns.Length == 0) return true;
            var fileName = Path.GetFileName(fullPath);
            var normPath = fullPath.Replace('\\', '/');
            foreach (var p in _patterns)
            {
                var pat = p.Replace('\\', '/');
                if (MatchWildcard(pat, fileName)) return false;
                if (pat.IndexOf('/') >= 0 &&
                    (MatchWildcard(pat, normPath) || MatchWildcard("*/" + pat, normPath)))
                    return false;
            }
            return true;
        }

        private static bool MatchWildcard(string pattern, string text)
        {
            int pi = 0, ti = 0, starPos = -1, matchPos = 0;
            while (ti < text.Length)
            {
                if (pi < pattern.Length && (char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(text[ti]) || pattern[pi] == '?'))
                { pi++; ti++; }
                else if (pi < pattern.Length && pattern[pi] == '*')
                { starPos = pi; matchPos = ti; pi++; }
                else if (starPos >= 0)
                { pi = starPos + 1; matchPos++; ti = matchPos; }
                else return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }
    }

    public class PathPrefixFilter : IFileFilter
    {
        private readonly string[] _prefixes;

        public PathPrefixFilter(params string[] prefixes)
        {
            _prefixes = prefixes;
        }

        public bool ShouldProcess(string fullPath)
        {
            foreach (var p in _prefixes)
                if (fullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
    }

    public class SizeFilter : IFileFilter
    {
        private readonly long _maxBytes;

        public SizeFilter(long maxSizeMb)
        {
            _maxBytes = maxSizeMb * 1024 * 1024;
        }

        public bool ShouldProcess(string fullPath)
        {
            if (_maxBytes <= 0) return true;
            try { return new FileInfo(fullPath).Length <= _maxBytes; }
            catch { return true; }
        }
    }
}
