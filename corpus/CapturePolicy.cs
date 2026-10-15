using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Corpus
{
    // Items 1-minimal/2/3/4: capture-policy core. Pure helpers + seams, so every
    // rule is Linux-provable with fakes. Windows-only extent resolution (MFT
    // path→LCN, retrieval pointers) plugs into the seams: MftExclusionResolver
    // (MftReader.cs) over the shared MFT reader. Until the CLI passes it, the
    // CLI images unexcluded and incrementals carry no fingerprint (guards
    // abstain on null, never false-positive).
    public interface ILayoutFingerprintProvider
    {
        string? LayoutFingerprint();
    }

    public interface IExclusionResolver
    {
        IReadOnlyList<string> ExclusionNames { get; }
        long BytesPerCluster { get; }
        IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns();
    }

    public enum CaptureDecision
    {
        Capture,
        SkipLoud,
        FullOnlyLoud,
    }

    public static class CapturePolicy
    {
        // Item 4 defaults: regenerable / churn-heavy, safe to restore as zeros.
        // $LogFile is DELIBERATELY absent — crash-consistent restore needs it.
        public static readonly IReadOnlyList<string> DefaultExclusions = new[]
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys",
            "$Recycle.Bin", "System Volume Information", "$UsnJrnl:$J",
            "Windows\\Temp", "Temp",
        };

        // Null configured = defaults. $LogFile is stripped even when explicitly
        // listed: it is never excludable.
        public static IReadOnlyList<string> EffectiveSkip(IReadOnlyList<string>? configured)
        {
            var want = configured ?? DefaultExclusions;
            var result = new List<string>(want.Count);
            foreach (var e in want)
            {
                if (string.Equals(e, "$LogFile", StringComparison.OrdinalIgnoreCase)
                    || e.EndsWith("\\$LogFile", StringComparison.OrdinalIgnoreCase)
                    || e.EndsWith("/$LogFile", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(e);
            }
            return result;
        }

        // Order-insensitive set compare; null == empty (old manifests, no resolver).
        public static bool ExclusionSetChanged(IReadOnlyList<string>? parent, IReadOnlyList<string>? current)
        {
            var p = parent ?? (IReadOnlyList<string>)Array.Empty<string>();
            var c = current ?? (IReadOnlyList<string>)Array.Empty<string>();
            if (p.Count != c.Count) return true;
            var set = new HashSet<string>(p, StringComparer.Ordinal);
            foreach (var e in c)
            {
                if (!set.Contains(e)) return true;
            }
            return false;
        }

        // Item 1-minimal: layout fingerprint = SHA-256 over the $MFT's own runs.
        // Defrag/optimize relocates extents without USN entries; $MFT runs
        // shifting proves relocation happened. Hex lowercase like ChunkId.
        public static string ComputeLayoutFingerprint(IReadOnlyList<(long StartLcn, long ClusterCount)> mftRuns)
        {
            ArgumentNullException.ThrowIfNull(mftRuns);
            var buf = new byte[mftRuns.Count * 16];
            for (var i = 0; i < mftRuns.Count; i++)
            {
                BitConverter.GetBytes(mftRuns[i].StartLcn).CopyTo(buf, i * 16);
                BitConverter.GetBytes(mftRuns[i].ClusterCount).CopyTo(buf, (i * 16) + 8);
            }
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(buf)).ToLowerInvariant();
        }

        // Null-tolerant: a side without a fingerprint (file sources, old
        // manifests) cannot prove relocation → no verdict → proceed.
        public static bool FingerprintMismatch(string? parent, string? current) =>
            !string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(current)
            && !string.Equals(parent, current, StringComparison.Ordinal);

        // LCN runs → dirty chunk indexes via byte offsets (chunk i covers bytes
        // [i*chunkBytes,...), not raw LCNs). StartLcn < 0 is the
        // retrieval-pointers sparse marker (item 2): no clusters, nothing to
        // dirty — skip, never compute a negative chunk index.
        public static HashSet<long> DirtyChunksForRuns(IReadOnlyList<(long StartLcn, long ClusterCount)> runs, long bytesPerCluster, int chunkBytes)
        {
            ArgumentNullException.ThrowIfNull(runs);
            var dirty = new HashSet<long>();
            foreach (var (startLcn, count) in runs)
            {
                if (startLcn < 0 || count <= 0)
                    continue;
                var first = (startLcn * bytesPerCluster) / chunkBytes;
                var last = (((startLcn + count) * bytesPerCluster) - 1) / chunkBytes;
                for (var c = first; c <= last; c++)
                    dirty.Add(c);
            }
            return dirty;
        }

        // Zero the excluded byte ranges inside one captured chunk (item 4):
        // partial-chunk exclusions must NOT hole the whole chunk (that would
        // zero neighboring file bytes on restore). Operates in place.
        public static void ZeroExcludedRanges(byte[] data, long chunkOffset, IReadOnlyList<(long StartLcn, long ClusterCount)> runs, long bytesPerCluster)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(runs);
            var end = chunkOffset + data.Length;
            foreach (var (startLcn, count) in runs)
            {
                if (startLcn < 0 || count <= 0)
                    continue;
                var start = Math.Max(startLcn * bytesPerCluster, chunkOffset);
                var stop = Math.Min((startLcn + count) * bytesPerCluster, end);
                if (start < stop)
                    Array.Clear(data, (int)(start - chunkOffset), (int)(stop - start));
            }
        }
    }

    // Item 3: per-type policy, safe defaults. Block imaging preserves bytes
    // verbatim, so most types capture; the exceptions refuse or skip LOUD.
    public static class FsTypePolicy
    {
        // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN: a dehydrated
        // stub. Opening one hydrates over the network (gigabytes, slowly, or
        // fails offline) — never touch: skip + Warn.
        public const uint RecallOnDataAccess = 0x00400000;
        public const uint RecallOnOpen = 0x00040000;

        public static bool IsDehydratedPlaceholder(uint fileAttributes) =>
            (fileAttributes & (RecallOnDataAccess | RecallOnOpen)) != 0;

        // OneDrive/CfAPI: skip + loud (never hydrate).
        public static CaptureDecision DecideOneDrive(uint fileAttributes) =>
            IsDehydratedPlaceholder(fileAttributes) ? CaptureDecision.SkipLoud : CaptureDecision.Capture;

        // WOF/CompactOS: block copy preserves compression + backing blobs
        // verbatim; a file copy would expand. Capture.
        public static CaptureDecision DecideWof() => CaptureDecision.Capture;

        // EFS: ciphertext + $EFS metadata capture verbatim; restore boots but
        // files need the user's cert. Capture (documented, not silent).
        public static CaptureDecision DecideEfs() => CaptureDecision.Capture;

        // Server dedup: stubs are useless without SVI\Dedup\ChunkStore, so the
        // store MUST capture with them — never exclude the chunk store.
        public static CaptureDecision DecideDedupStore() => CaptureDecision.Capture;

        // ReFS: the USN/MFT extent path is NTFS-only; ReFS images full-only
        // (incremental parent refused loud → full re-read, never wrong data).
        public static CaptureDecision DecideFilesystem(string? filesystem) =>
            string.Equals(filesystem, "refs", StringComparison.OrdinalIgnoreCase)
                ? CaptureDecision.FullOnlyLoud : CaptureDecision.Capture;
    }
}
