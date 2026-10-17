using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    // Item-13 TRUTH core: SHA-256 digests over raw MFT record bytes, grouped
    // into image-chunk-sized bands. Any extent relocation (defrag/optimize),
    // size, timestamp, or run-list change alters record bytes → the group
    // digest moves → the file's extents re-capture. Whole-record hashing is
    // deliberately write-sensitive (LSN included): conservative, never blind.
    // Completeness: a relocated file's CURRENT runs dirty the new clusters;
    // the abandoned clusters either belong to another changed record or are
    // free — and freed space is caught by the bitmap hole-flip net.
    public static class MftDigest
    {
        public static string RecordDigest(byte[] record)
        {
            ArgumentNullException.ThrowIfNull(record);
            using var sha = SHA256.Create();
            var len = BitConverter.GetBytes(record.Length);
            sha.TransformBlock(len, 0, len.Length, null, 0);
            sha.TransformFinalBlock(record, 0, record.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        // Group i covers MFT-stream bytes [i*groupBytes,(i+1)*groupBytes):
        // records [i*per,(i+1)*per) at `per` records per group. Streaming —
        // no whole-MFT alloc at any file count.
        public static List<string> ComputeGroups(IMftVolume vol, int groupBytes)
        {
            ArgumentNullException.ThrowIfNull(vol);
            if (groupBytes <= 0) throw new ArgumentOutOfRangeException(nameof(groupBytes));
            var per = Math.Max(1, groupBytes / vol.BytesPerRecord);
            var groups = new List<string>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var inGroup = 0;
            for (var rec = 0L; rec < vol.RecordCount; rec++)
            {
                var bytes = vol.GetRecordBytes((ulong)rec);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
                if (++inGroup == per)
                {
                    groups.Add(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                    inGroup = 0;
                }
            }
            if (inGroup > 0)
                groups.Add(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            return groups;
        }

        // Changed group indexes. Growth (new files) dirties the extra groups;
        // shrink means reformat-level surgery → null (full re-read).
        public static List<int>? DiffGroups(IReadOnlyList<string> parent, IReadOnlyList<string> current)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(current);
            if (current.Count < parent.Count)
                return null;
            var changed = new List<int>();
            for (var i = 0; i < current.Count; i++)
            {
                if (i >= parent.Count || !string.Equals(parent[i], current[i], StringComparison.Ordinal))
                    changed.Add(i);
            }
            return changed;
        }
    }

    // Digest planner over the IMftVolume seam (fake-testable on Linux).
    // Null = incremental impossible → caller falls back to full re-read.
    // System-metadata extents for the §12.8 skip comparison: FRN 0..15
    // ($MFT/$MFTMirr/$LogFile/$Bitmap/...) churn on every mount, so a
    // skip gate that counts them as user changes never fires on live
    // NTFS. Capture plans must NOT use this (incrementals re-read sys
    // extents — a stale $MFT corrupts restores); only skip DECISIONS may
    // ignore diffs confined here. Null = unreadable/unknown → the caller
    // stays strict (never skip blind).
    internal static class MftSysExtents
    {
        internal static List<(long StartLcn, long ClusterCount)>? Runs(IMftVolume vol)
        {
            ArgumentNullException.ThrowIfNull(vol);
            var runs = new List<(long StartLcn, long ClusterCount)>();
            try
            {
                for (var sys = 0UL; sys < 16 && (long)sys < vol.RecordCount; sys++)
                {
                    var bytes = vol.GetRecordBytes(sys);
                    var extents = MftExtentReader.ParseRecordExtents(bytes, sys,
                        vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns, skipSequenceCheck: true);
                    if (extents == null)
                        continue;
                    runs.AddRange(extents);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            return runs;
        }
    }

    internal static class MftDigestPlanner
    {
        internal static (HashSet<long> Dirty, List<string> Groups)? PlanDirty(
            IMftVolume vol, SnapshotManifest parent, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(vol);
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(log);
            // The record-size probe is the first MFT touch: a dead snapshot
            // device fails here, before ComputeGroups' own guard — degrade
            // to full like an unreadable MFT, never throw (Store's baseline
            // path wraps ComputeGroups only, so this hole is planner-only).
            int recordBytes;
            try
            {
                recordBytes = vol.BytesPerRecord;
            }
            catch (InvalidDataException ex)
            {
                log.Info($"full image (MFT unreadable: {ex.Message})");
                return null;
            }
            if (recordBytes != parent.MftRecordBytes)
            {
                log.Info("full image (MFT record size changed, reformat?)");
                return null;
            }
            List<string> current;
            try
            {
                current = MftDigest.ComputeGroups(vol, chunkBytes);
            }
            catch (InvalidDataException ex)
            {
                log.Info($"full image (MFT unreadable: {ex.Message})");
                return null;
            }
            var changed = MftDigest.DiffGroups(parent.MftChunkDigests ?? new List<string>(), current);
            if (changed == null)
            {
                log.Info("full image (MFT shrank, reformat?)");
                return null;
            }
            var dirty = new HashSet<long>();
            var per = Math.Max(1, chunkBytes / vol.BytesPerRecord);
            foreach (var g in changed)
            {
                var first = (long)g * per;
                var past = Math.Min(first + per, vol.RecordCount);
                for (var rec = first; rec < past; rec++)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = vol.GetRecordBytes((ulong)rec);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record {rec} unreadable: {ex.Message})");
                        return null;
                    }
                    if (bytes.Length < 48)
                    {
                        log.Info($"full image (MFT record {rec} truncated)");
                        return null;
                    }
                    // Scan FRN carries the record's own seq (no enumeration
                    // here): live records verify, dead slots filter, exactly
                    // like the USN path's reuse checks.
                    var fileRef = ((ulong)BitConverter.ToUInt16(bytes, 0x10) << 48) | (ulong)rec;
                    List<(long StartLcn, long ClusterCount)>? runs;
                    try
                    {
                        runs = MftExtentReader.ParseRecordExtents(bytes, fileRef,
                            vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record {rec} unparseable: {ex.Message})");
                        return null;
                    }
                    if (runs == null)
                        continue; // dead slot → bitmap net covers the freed clusters
                    foreach (var c in CapturePolicy.DirtyChunksForRuns(runs, vol.BytesPerCluster, chunkBytes))
                        dirty.Add(c);
                }
            }
            // NTFS self-metadata ($MFT/$LogFile/$Bitmap/...): their CONTENT
            // mutates without changing their own record bytes, so group
            // digests can't see it — always re-capture FRNs 0..15 (same
            // policy as the USN path, same synthetic-seq bypass).
            for (var sys = 0UL; sys < 16 && (long)sys < vol.RecordCount; sys++)
            {
                byte[] bytes;
                try
                {
                    bytes = vol.GetRecordBytes(sys);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image (MFT record for system file {sys} unreadable: {ex.Message})");
                    return null;
                }
                List<(long StartLcn, long ClusterCount)>? sysRuns;
                try
                {
                    sysRuns = MftExtentReader.ParseRecordExtents(bytes, sys,
                        vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns, skipSequenceCheck: true);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image (MFT record for system file {sys} unreadable: {ex.Message})");
                    return null;
                }
                if (sysRuns == null)
                    continue;
                foreach (var c in CapturePolicy.DirtyChunksForRuns(sysRuns, vol.BytesPerCluster, chunkBytes))
                    dirty.Add(c);
            }
            return (dirty, current);
        }
    }

    // FSCTL-mediated MFT-digest-compare incrementals. Digest = truth (covers
    // journal wrap, untrusted cursors, and relocation without USN entries);
    // USN stays as the hint for pre-digest parents and non-MFT sources; the
    // bitmap hole-flip net unions in freed-cluster reuse. Null = full.
    public sealed class MftDigestChangeSource : IChangeSource
    {
        public IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(log);
            IMftVolume? vol = source as IMftVolume;
            var owned = false;
            if (vol == null)
            {
                vol = TryOpenVolume(source, log);
                owned = vol != null;
            }
            if (vol == null)
                return new UsnChangeSource().Plan(parent, source, chunkBytes, log);
            try
            {
                if (parent.MftChunkDigests == null || parent.MftChunkDigests.Count == 0)
                    return new UsnChangeSource().Plan(parent, source, chunkBytes, log);
                var plan = MftDigestPlanner.PlanDirty(vol, parent, chunkBytes, log);
                if (plan == null)
                    return null;
                var flips = HoleFlipNet(parent, source, vol.BytesPerCluster, chunkBytes);
                if (flips == null)
                    return null;
                foreach (var c in flips)
                    plan.Value.Dirty.Add(c);
                var cursor = UsnChangeSource.TryCurrentCursor(source, log) ?? parent.ChangeCursor ?? "";
                return new IncrementalPlan(plan.Value.Dirty, cursor, plan.Value.Groups, vol.BytesPerRecord);
            }
            finally
            {
                if (owned)
                    vol.Dispose();
            }
        }

        private static IMftVolume? TryOpenVolume(IBlockSource source, ILogger log)
        {
            if (!OperatingSystem.IsWindows() || source is FileBlockSource || source is not IVolumeHandleProvider vol)
                return null; // USN delegate below logs the reason, same as today
            try
            {
                return new MftVolumeReader(vol.SnapshotDevicePath);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Info("MFT digest unavailable, trying USN (" + ex.Message + ")");
                return null;
            }
        }

        private static HashSet<long>? HoleFlipNet(SnapshotManifest parent, IBlockSource source, long bytesPerCluster, int chunkBytes)
        {
            IReadOnlyList<(long Start, long Count)> runs;
            if (source is ISparseBlockSource sparse)
                runs = sparse.GetAllocatedRuns();
            else
                runs = new (long Start, long Count)[] { (0, (source.Length + bytesPerCluster - 1) / bytesPerCluster) };
            return VolumeBitmap.HoleFlipChunks(parent.Chunks, runs, bytesPerCluster, source.Length, chunkBytes);
        }
    }
}
