using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TicTack;

namespace Corpus
{
    // Restic-style live progress. opts.Progress off (or redirected stdout
    // with auto) keeps today's classic 2 Hz % log lines; otherwise the
    // console gets one \r-rewritten status line plus a closing summary.
    // All failure paths are best-effort: diagnostics never break a capture.
    internal sealed record LiveProgressOptions(string Mode, bool ShowName, bool ShowPath)
    {
        internal static LiveProgressOptions FromConfig(CorpusLoggingConfig? logging)
        {
            var mode = (logging?.Progress ?? "auto").Trim().ToLowerInvariant();
            if (mode != "on" && mode != "off")
                mode = "auto";
            return new LiveProgressOptions(mode, logging?.ProgressName ?? true, logging?.ProgressPath ?? false);
        }

        internal bool Enabled => Mode != "off";
        internal bool Live => Mode == "on" || (Mode == "auto" && LiveProgress.ConsoleLive());
    }

    // Byte-range -> path map over one volume's data runs. Pure Build/Lookup
    // (unit-tested); FromVolume is the thin MFT adapter. First path wins on
    // overlap; unmapped offsets return null (caller shows @offset).
    internal sealed class ExtentNameIndex
    {
        private readonly List<(long Start, long End, string Path)> _ranges = new();

        internal static ExtentNameIndex Build(long bytesPerCluster, IEnumerable<(string Path, IReadOnlyList<(long StartLcn, long ClusterCount)> Runs)> files)
        {
            var index = new ExtentNameIndex();
            var seen = new HashSet<long>();
            foreach (var (path, runs) in files)
            {
                if (string.IsNullOrEmpty(path) || runs == null)
                    continue;
                foreach (var (lcn, count) in runs)
                {
                    if (count <= 0 || seen.Contains(lcn))
                        continue;
                    seen.Add(lcn);
                    index._ranges.Add((lcn * bytesPerCluster, (lcn + count) * bytesPerCluster, path));
                }
            }
            index._ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            return index;
        }

        internal string? Lookup(long offset)
        {
            int lo = 0, hi = _ranges.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                var (start, end, path) = _ranges[mid];
                if (offset < start)
                    hi = mid - 1;
                else if (offset >= end)
                    lo = mid + 1;
                else
                    return path;
            }
            return null;
        }

        // Borrowed volume, never disposed here (the source owns it).
        // Null = unusable (non-NTFS, IO fault) -> caller shows @offset.
        internal static ExtentNameIndex? FromVolume(IMftVolume vol)
        {
            try
            {
                var entries = vol.Enumerate();
                if (entries == null || entries.Count == 0)
                    return null;
                var byRef = new Dictionary<ulong, MftEnumEntry>(entries.Count);
                foreach (var e in entries)
                    byRef.TryAdd(e.FileRef, e);
                var files = new List<(string, IReadOnlyList<(long, long)>)>();
                foreach (var e in entries)
                {
                    if ((e.Attributes & 0x10) != 0) // directory, same bit ChangeSource uses
                        continue;
                    string path;
                    byte[] record;
                    try
                    {
                        path = FullPath(byRef, e);
                        record = vol.GetRecordBytes(e.FileRef);
                    }
                    catch { continue; }
                    // Data runs for display: skip the MFT record's own cluster
                    // (it would misattribute the MFT zone to random files).
                    var runs = MftExtentReader.ParseRecordExtents(record, e.FileRef, vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns, includeRecordCluster: false);
                    if (runs != null && runs.Count > 0)
                        files.Add((path, runs));
                }
                return files.Count == 0 ? null : Build(vol.BytesPerCluster, files);
            }
            catch { return null; }
        }

        // Same parent-walk as the exclusion resolver, display-only (no normalization).
        private static string FullPath(Dictionary<ulong, MftEnumEntry> byRef, MftEnumEntry entry)
        {
            var parts = new Stack<string>();
            var cur = entry;
            for (int depth = 0; depth < 64; depth++)
            {
                parts.Push(cur.Name);
                if (cur.ParentRef == cur.FileRef || !byRef.TryGetValue(cur.ParentRef, out var parent))
                    break;
                cur = parent;
            }
            return string.Join("\\", parts);
        }
    }

    // IProgress<long> with the classic 2 Hz log lines replaced on a live
    // console by one rewritten status line. Null-returning Create keeps the
    // caller's classic reporter untouched.
    internal sealed class LiveProgress : IProgress<long>
    {
        private const int BarWidth = 20;
        private const int PathCap = 60;

        private readonly ILogger _log;
        private readonly string _label;
        private readonly long _total;
        private readonly LiveProgressOptions _opts;
        private readonly Func<IMftVolume?>? _volume;
        private readonly IProgress<long>? _fwd;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly object _gate = new();
        private ExtentNameIndex? _index;
        private Task<ExtentNameIndex?>? _indexTask;
        private int _lastLen;
        private bool _finished;

        private LiveProgress(ILogger log, LiveProgressOptions opts, string label, long total, Func<IMftVolume?>? volume, IProgress<long>? fwd)
        {
            _log = log;
            _opts = opts;
            _label = label;
            _total = total;
            _volume = volume;
            _fwd = fwd;
        }

        internal static IProgress<long>? Create(ILogger log, LiveProgressOptions? opts, string label, long total, Func<IMftVolume?>? volume, IProgress<long>? fwd = null)
        {
            if (opts == null || !opts.Enabled || !opts.Live)
                return null;
            var live = new LiveProgress(log, opts, label, total, volume, fwd);
            // The name index walks the whole MFT (minutes on a real C:).
            // Build it off the copy path so the bar renders immediately;
            // Report shows @offset until it lands. Skipped entirely when
            // names/paths are off: no scan, no cost.
            if ((opts.ShowName || opts.ShowPath) && volume != null)
            {
                try
                {
                    live._indexTask = Task.Run(() =>
                    {
                        try
                        {
                            var v = live._volume?.Invoke();
                            return v == null ? null : ExtentNameIndex.FromVolume(v);
                        }
                        catch { return null; }
                    });
                }
                catch { }
            }
            return live;
        }

        internal static bool ConsoleLive()
        {
            try { return !Console.IsOutputRedirected; }
            catch { return false; }
        }

        public void Report(long bytes)
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                try { _fwd?.Report(bytes); }
                catch { }
                // Name index builds in the background (see Create); pick it
                // up the moment it lands, @offset until then.
                var pending = _indexTask;
                if (pending != null && pending.IsCompleted)
                {
                    _indexTask = null;
                    try { _index = pending.Result; }
                    catch { _index = null; } // Fall back to extent-offset display.
                }
                if (bytes < _total)
                {
                    WriteLine(Render(_label, bytes, _total, _sw.Elapsed.TotalSeconds, Display(bytes)));
                    return;
                }
                Finish(bytes);
            }
        }

        private string? Display(long offset)
        {
            if (!_opts.ShowPath && !_opts.ShowName)
                return null;
            string? path = null;
            try { path = _index?.Lookup(offset); }
            catch { path = null; }
            if (string.IsNullOrEmpty(path))
                return "@offset " + Human(offset);
            if (_opts.ShowPath)
                return TruncateLeft(path, PathCap);
            return Path.GetFileName(path);
        }

        internal static string TruncateLeft(string path, int cap)
            => path.Length > cap ? "..." + path.Substring(path.Length - (cap - 3)) : path;

        private void WriteLine(string line)
        {
            try
            {
                Console.Write("\r" + line.PadRight(_lastLen));
                _lastLen = Math.Max(_lastLen, line.Length);
            }
            catch { }
        }

        private void Finish(long bytes)
        {
            _finished = true;
            try { Console.WriteLine(); }
            catch { }
            var s = _sw.Elapsed.TotalSeconds;
            _log.Info($"{_label} done: {Human(bytes)} in {FmtTime(s)}{(s > 0 ? $" ({Human((long)(bytes / s))}/s avg)" : "")}");
        }

        internal static string Render(string label, long done, long total, double elapsedSec, string? name)
        {
            double pct = total > 0 ? Math.Min(100.0, done * 100.0 / total) : 0;
            int filled = (int)(pct / 100 * BarWidth);
            double rate = elapsedSec > 0 ? done / elapsedSec : 0;
            string eta = rate > 0 && done < total ? " ETA " + FmtTime((total - done) / rate) : "";
            var line = $"{label} [{new string('#', filled)}{new string('-', BarWidth - filled)}] {pct:F0}% {Human(done)}/{Human(total)} {Human((long)rate)}/s{eta}";
            return string.IsNullOrEmpty(name) ? line : line + " " + name;
        }

        internal static string Human(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
            if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
            return bytes + " B";
        }

        private static string FmtTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
        }
    }
}
