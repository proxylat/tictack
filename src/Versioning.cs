using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public class NoVersioning : IVersioningStrategy
    {
        public Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            return Task.FromResult(ActionResult.Ok());
        }
    }

    // One version file on disk: its parsed timestamp (when the name matches
    // the a_{yyyyMMdd_HHmmss_fff}_{guid}.ext contract) and its pin state.
    public sealed class VersionFile
    {
        public string Path { get; }
        public DateTime Timestamp { get; }
        public bool HasTimestamp { get; }
        public bool Pinned { get; }

        public VersionFile(string path, DateTime timestamp, bool hasTimestamp, bool pinned)
        {
            Path = path;
            Timestamp = timestamp;
            HasTimestamp = hasTimestamp;
            Pinned = pinned;
        }
    }

    // Restic-style retention with OR-semantics per file: a version survives
    // when ANY policy keeps it. Unparseable names and pinned versions are
    // always kept — pruning must never delete what it cannot date, and a
    // pin is an explicit "hands off".
    public sealed class RetentionPolicy
    {
        public int? KeepLast { get; set; }
        public TimeSpan? KeepWithin { get; set; }
        public int? KeepDaily { get; set; }
        public int? KeepWeekly { get; set; }
        public int? KeepMonthly { get; set; }

        public readonly struct Decision
        {
            public string Path { get; }
            public bool Keep { get; }
            public string Reason { get; }

            public Decision(string path, bool keep, string reason)
            {
                Path = path;
                Keep = keep;
                Reason = reason;
            }
        }

        public static RetentionPolicy FromConfig(VersioningConfig? config, ILogger? log = null)
        {
            var policy = new RetentionPolicy();
            if (config == null) return policy;
            int? Clean(int? v, string key)
            {
                if (v == null) return null;
                if (v < 0)
                {
                    log?.Warn("Invalid versioning." + key + " '" + v + "' (use >= 0), ignoring");
                    return null;
                }
                return v;
            }
            // Legacy max_versions is a keep-last under the new spelling: with
            // no other policy set it behaves exactly like the old cap.
            var legacy = Clean(config.MaxVersions, "max_versions");
            var keepLast = Clean(config.KeepLast, "keep_last");
            policy.KeepLast = keepLast ?? legacy;
            if (!string.IsNullOrWhiteSpace(config.KeepWithin))
            {
                if (Config.TryParseDuration(config.KeepWithin, out var within))
                    policy.KeepWithin = within;
                else
                    log?.Warn("Invalid versioning.keep_within '" + config.KeepWithin + "' (use 30d/12h), ignoring");
            }
            policy.KeepDaily = Clean(config.KeepDaily, "keep_daily");
            policy.KeepWeekly = Clean(config.KeepWeekly, "keep_weekly");
            policy.KeepMonthly = Clean(config.KeepMonthly, "keep_monthly");
            return policy;
        }

        public bool IsEmpty =>
            KeepLast == null && KeepWithin == null && KeepDaily == null && KeepWeekly == null && KeepMonthly == null;

        public List<Decision> Evaluate(IEnumerable<VersionFile> versions, DateTime now)
        {
            var ordered = versions.OrderByDescending(v => v.HasTimestamp ? v.Timestamp : DateTime.MaxValue).ToList();
            var keep = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var v in ordered)
            {
                if (v.Pinned) keep[v.Path] = "pinned";
                else if (!v.HasTimestamp) keep[v.Path] = "unparseable-name";
            }

            if (KeepLast != null)
            {
                var n = 0;
                foreach (var v in ordered)
                {
                    if (n >= KeepLast.Value) break;
                    if (!keep.ContainsKey(v.Path)) keep[v.Path] = "keep-last";
                    n++;
                }
            }

            if (KeepWithin != null)
            {
                var cutoff = now - KeepWithin.Value;
                foreach (var v in ordered)
                    if (v.HasTimestamp && v.Timestamp >= cutoff && !keep.ContainsKey(v.Path))
                        keep[v.Path] = "keep-within";
            }

            // GFS ladder: within each calendar bucket the newest version
            // wins; the N newest buckets survive. Week buckets are ISO-8601
            // (restic parity); day/month are local calendar days.
            KeepBuckets(ordered, keep, KeepDaily, v => v.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "keep-daily");
            KeepBuckets(ordered, keep, KeepWeekly, v => IsoWeekKey(v.Timestamp), "keep-weekly");
            KeepBuckets(ordered, keep, KeepMonthly, v => v.Timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture), "keep-monthly");

            var result = new List<Decision>(ordered.Count);
            foreach (var v in ordered)
            {
                if (keep.TryGetValue(v.Path, out var reason))
                    result.Add(new Decision(v.Path, true, reason));
                else
                    result.Add(new Decision(v.Path, false, "beyond-retention"));
            }
            return result;
        }

        private static void KeepBuckets(List<VersionFile> newestFirst, Dictionary<string, string> keep, int? count, Func<VersionFile, string> key, string reason)
        {
            if (count == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = 0;
            // newestFirst order means the first file per bucket is its winner.
            foreach (var v in newestFirst)
            {
                if (!v.HasTimestamp || v.Pinned) continue;
                var bucket = key(v);
                if (!seen.Add(bucket)) continue;
                if (kept >= count.Value) break;
                kept++;
                if (!keep.ContainsKey(v.Path)) keep[v.Path] = reason;
            }
        }

        internal static string IsoWeekKey(DateTime stamp)
        {
            // ISOWeek is pure arithmetic (no culture data), so it works under
            // InvariantGlobalization. GetYear returns the week-numbering year.
            return ISOWeek.GetYear(stamp).ToString("D4", CultureInfo.InvariantCulture)
                + "-W" + ISOWeek.GetWeekOfYear(stamp).ToString("D2", CultureInfo.InvariantCulture);
        }
    }

    public class TimestampVersioning : IVersioningStrategy
    {
        private readonly string _versionBase;
        private readonly string _destBase;
        private readonly RetentionPolicy _policy;

        public TimestampVersioning(string versionBase, string destBase, int maxVersions = 10)
            : this(versionBase, destBase, new RetentionPolicy { KeepLast = Math.Max(0, maxVersions) })
        {
        }

        public TimestampVersioning(string versionBase, string destBase, RetentionPolicy policy)
        {
            _versionBase = versionBase;
            _destBase = destBase;
            _policy = policy ?? new RetentionPolicy();
        }

        // Name contract: {name}_{yyyyMMdd_HHmmss_fff}_{32-hex-guid}{ext}.
        // Anchored from the right so names containing '_' parse correctly.
        internal static readonly Regex StampPattern = new Regex(
            "_(?<date>[0-9]{8})_(?<time>[0-9]{6})_(?<ms>[0-9]{3})_[0-9a-fA-F]{32}(?<ext>\\.[^.]*)?$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

        internal static bool TryParseStamp(string fileName, out DateTime stamp, out string originalName)
        {
            stamp = DateTime.MinValue;
            originalName = fileName;
            var m = StampPattern.Match(fileName);
            if (!m.Success) return false;
            if (!DateTime.TryParseExact(m.Groups["date"].Value + "_" + m.Groups["time"].Value + "_" + m.Groups["ms"].Value,
                "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                return false;
            originalName = fileName.Substring(0, m.Index) + m.Groups["ext"].Value;
            return true;
        }

        internal static string PinPath(string versionPath) => versionPath + ".pinned";

        internal static bool IsPinned(string versionPath)
        {
            try { return File.Exists(PinPath(versionPath)); } catch { return false; }
        }

        public Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct)
        {
            if (!File.Exists(PathUtil.EnsureExtended(destPath))) return Task.FromResult(ActionResult.Ok());

            try
            {
                ct.ThrowIfCancellationRequested();
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var name = Path.GetFileNameWithoutExtension(destPath);
                var ext = Path.GetExtension(destPath);
                var dir = Path.GetDirectoryName(destPath);
                var syncRoot = Path.GetDirectoryName(_versionBase.TrimEnd('\\', '/'));
                string relDir;
                if (dir != null && syncRoot != null && dir.StartsWith(syncRoot, StringComparison.OrdinalIgnoreCase))
                    relDir = PathUtil.Relative(dir, syncRoot);
                else if (dir != null && dir.StartsWith(_destBase, StringComparison.OrdinalIgnoreCase))
                    relDir = PathUtil.Relative(dir, _destBase);
                else
                    relDir = "";
                var verDir = string.IsNullOrEmpty(relDir) ? _versionBase : Path.Combine(_versionBase, relDir);
                var verFile = Path.Combine(verDir, name + "_" + ts + "_" + Guid.NewGuid().ToString("N") + ext);
                var temp = verFile + ".tictack.tmp";

                Directory.CreateDirectory(verDir);
                try
                {
                    using (var input = File.OpenRead(PathUtil.EnsureExtended(destPath)))
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    // Same post-write validation as the copy and archive paths:
                    // never trust a version file that was not verified.
                    if (!FileHasher.SameContent(destPath, temp))
                        throw new IOException("Version validation failed: " + destPath);
                    File.Move(temp, verFile);
                    // Barrier for the version commit (same Pillai surface as
                    // the copy path). Silent best-effort: a lost version
                    // entry just re-archives next run; no logger is wired
                    // through this strategy, and the data itself is flushed.
                    CopyAction.FlushDirectory(verDir);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }

                PruneGroup(verDir, name + ext);
                return Task.FromResult(ActionResult.Ok());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Task.FromResult(ActionResult.Fail(ex.Message)); }
        }

        // Prune one file's group: same-directory versions whose parsed
        // original name matches. Parsing (not prefix matching) also fixes the
        // old a.txt/a_b.txt prefix collision, where one file's prune could
        // eat another's versions.
        private void PruneGroup(string verDir, string originalName)
        {
            string[] files;
            try { files = Directory.GetFiles(verDir); }
            catch { return; }
            var group = new List<VersionFile>();
            foreach (var f in files)
            {
                if (f.EndsWith(".pinned", StringComparison.Ordinal)
                    || f.EndsWith(".tictack.tmp", StringComparison.Ordinal))
                    continue;
                if (!TryParseStamp(Path.GetFileName(f), out var stamp, out var orig))
                    continue; // someone else's group or foreign file: not ours
                if (!orig.Equals(originalName, StringComparison.Ordinal))
                    continue;
                group.Add(new VersionFile(f, stamp, true, IsPinned(f)));
            }
            var policy = _policy.IsEmpty ? new RetentionPolicy { KeepLast = 10 } : _policy;
            var deleted = 0;
            foreach (var d in policy.Evaluate(group, DateTime.Now))
            {
                if (d.Keep) continue;
                try { File.Delete(d.Path); deleted++; } catch { }
            }
            if (deleted > 0) CopyAction.FlushDirectory(verDir);
        }
    }

    public static class VersioningFactory
    {
        public static IVersioningStrategy Create(VersioningConfig? config, string destBase) =>
            Create(config, destBase, null);

        public static IVersioningStrategy Create(VersioningConfig? config, string destBase, ILogger? log)
        {
            if (config == null || string.IsNullOrEmpty(config.Path))
                return new NoVersioning();
            return new TimestampVersioning(config.Path, destBase, RetentionPolicy.FromConfig(config, log));
        }
    }
}
