using System;
using System.Collections.Generic;
using TicTack;

namespace Corpus
{
    // Ransomware hardening (fixes gap 4): WORM immutability gate + change
    // alarm, mirroring kopia's object-lock story at local-tool scale. Both
    // knobs (WormMode, ChangeAlarmPercent) default off: with defaults every
    // method below is a no-op and behavior is unchanged.
    public sealed class WormBlockedException : InvalidOperationException
    {
        public WormBlockedException(string message)
            : base(message)
        {
        }
    }

    public sealed class ChangeAlarmException : InvalidOperationException
    {
        public ChangeAlarmException(string message)
            : base(message)
        {
        }
    }

    public static class Hardening
    {
        // WORM gate for prune/GC/delete paths: any plan that would destroy
        // snapshot data (manifests, layouts, packs, indexed chunks) refuses
        // LOUD, naming what was blocked. A plan that destroys nothing passes:
        // index remarking and no-op sweeps are metadata bookkeeping.
        public static void ThrowIfWormBlocked(bool wormMode, GcPlan plan, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(log);
            if (!wormMode)
                return;
            if (plan.DropManifests.Count == 0 && plan.DropPacks.Count == 0
                && plan.DropLayouts.Count == 0 && plan.DeadChunks == 0)
                return;
            var named = new List<string>();
            named.AddRange(plan.DropManifests);
            named.AddRange(plan.DropLayouts);
            named.AddRange(plan.DropPacks);
            named.Sort(StringComparer.Ordinal);
            var shown = string.Join(", ", named.GetRange(0, Math.Min(5, named.Count)));
            var msg = "worm mode: refusing to destroy snapshot data " +
                $"({plan.DropManifests.Count} manifests, {plan.DropLayouts.Count} layouts, " +
                $"{plan.DropPacks.Count} packs, {plan.DeadChunks} chunks / {plan.DeadBytes} bytes" +
                (shown.Length > 0 ? "; e.g. " + shown : "") + ")";
            log.Error(msg);
            throw new WormBlockedException(msg);
        }

        // Pure churn math: per-index digest/hole comparison, grid growth or
        // shrinkage counts as changed. 0 when both are empty.
        public static double ChurnFraction(IReadOnlyList<ManifestEntry> parent, IReadOnlyList<ManifestEntry> child)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(child);
            var total = Math.Max(parent.Count, child.Count);
            if (total == 0)
                return 0;
            var changed = Math.Abs(parent.Count - child.Count);
            var shared = Math.Min(parent.Count, child.Count);
            for (var i = 0; i < shared; i++)
            {
                if (parent[i].IsHole != child[i].IsHole
                    || !string.Equals(parent[i].DigestHex, child[i].DigestHex, StringComparison.Ordinal))
                    changed++;
            }
            return (double)changed / total;
        }

        // Change alarm: with thresholdPercent > 0 and a parent to compare,
        // churn above the threshold logs ERROR and refuses the commit
        // (fail-closed: wholesale encryption reads as ~100% churn). force is
        // the explicit operator bypass. Threshold 0 disables; a null parent
        // (first gen) has nothing to compare against.
        public static void CheckChangeAlarm(SnapshotManifest? parent, IReadOnlyList<ManifestEntry> entries, double thresholdPercent, bool force, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(log);
            if (thresholdPercent <= 0)
                return;
            if (parent == null)
                return;
            var percent = ChurnFraction(parent.Chunks, entries) * 100;
            if (percent <= thresholdPercent)
                return;
            var msg = $"change alarm: churn {percent:F1}% exceeds {thresholdPercent:F1}% " +
                $"(parent gen {parent.Generation}, {entries.Count} chunks)";
            if (force)
            {
                log.Warn(msg + ", forced: committing anyway");
                return;
            }
            log.Error(msg);
            throw new ChangeAlarmException(msg + " (re-run with --force-full to override)");
        }
    }
}
