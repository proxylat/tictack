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
            _filters = filters != null ? filters.ToArray() : Array.Empty<IFileFilter>();
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
        private readonly Pattern[] _patterns;
        private readonly bool _anyPathPattern;

        private readonly struct Pattern
        {
            public readonly string Value;
            public readonly string SlashSuffix;
            public readonly bool HasSlash;

            public Pattern(string value, bool hasSlash)
            {
                Value = value;
                HasSlash = hasSlash;
                SlashSuffix = hasSlash ? "*/" + value : value;
            }
        }

        public PatternFilter(string[] patterns)
        {
            if (patterns == null || patterns.Length == 0)
            {
                _patterns = Array.Empty<Pattern>();
                _anyPathPattern = false;
                return;
            }
            _patterns = new Pattern[patterns.Length];
            for (int i = 0; i < patterns.Length; i++)
            {
                var pat = patterns[i].Replace('\\', '/');
                var hasSlash = pat.IndexOf('/') >= 0;
                _patterns[i] = new Pattern(pat, hasSlash);
                if (hasSlash) _anyPathPattern = true;
            }
        }

        public bool ShouldProcess(string fullPath)
        {
            if (_patterns.Length == 0) return true;
            var fileName = Path.GetFileName(fullPath);
            var normPath = _anyPathPattern ? fullPath.Replace('\\', '/') : null;
            foreach (var p in _patterns)
            {
                if (MatchWildcard(p.Value, fileName)) return false;
                if (p.HasSlash &&
                    (MatchWildcard(p.Value, normPath!) || MatchWildcard(p.SlashSuffix, normPath!)))
                    return false;
            }
            return true;
        }

        // Windows filesystems are case-insensitive; Linux ones are not.
        private static readonly bool CaseInsensitive = OperatingSystem.IsWindows();

        private static bool CharsEqual(char a, char b) =>
            CaseInsensitive ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b) : a == b;

        private static bool MatchWildcard(string pattern, string text)
        {
            int pi = 0, ti = 0, starPos = -1, matchPos = 0;
            while (ti < text.Length)
            {
                if (pi < pattern.Length && (CharsEqual(pattern[pi], text[ti]) || pattern[pi] == '?'))
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
        // Windows paths are case-insensitive; Linux is not.
        private static readonly StringComparison Comparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public PathPrefixFilter(params string[] prefixes)
        {
            _prefixes = prefixes ?? Array.Empty<string>();
        }

        public bool ShouldProcess(string fullPath)
        {
            foreach (var p in _prefixes)
            {
                if (string.IsNullOrEmpty(p)) continue;
                // Boundary-aware: excluding /home/user must not exclude /home/user2.
                if (!fullPath.StartsWith(p, Comparison)) continue;
                if (fullPath.Length > p.Length && fullPath[p.Length] != '\\' && fullPath[p.Length] != '/') continue;
                return false;
            }
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
