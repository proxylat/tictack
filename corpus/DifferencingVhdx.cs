using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using DiscUtils.Vhdx;
using TicTack;

namespace Corpus
{
    // §12.6 differencing-VHDX incremental outputs: `--to-vhdx --parent
    // base.vhdx` writes dirty extents only into a child disk; the parent
    // supplies the rest via read fall-through (PAYLOAD_BLOCK_NOT_PRESENT =
    // ask the parent). Depth 1 is structural: the parent must be a full
    // dynamic VHDX, diff-of-diff refuses loud.
    //
    // SPIKE VERDICT (DiscUtils CAN — LTRData.DiscUtils.Vhdx 1.0.90):
    // Disk.InitializeDifferencing(childPath, parentPath) creates the child
    // with a parent locator; new Disk(childPath) resolves the parent for
    // read fall-through; DiskImageFile.NeedsParent + Information.HasParent /
    // ParentLocatorEntries (IDictionary<string,string>) give the depth-1
    // check. No VirtDisk P/Invoke needed; the §12.1 writer spike stays shut.
    //
    // Parent identity (size+mtime+hash) lives in a sidecar JSON next to the
    // child (<child>.parent.json): the VHDX parent locator carries a path,
    // not our hash. Recorded at creation, re-checked at every consumption.

    public class DifferencingVhdxException : Exception
    {
        public DifferencingVhdxException(string message) : base(message) { }
        public DifferencingVhdxException(string message, Exception inner) : base(message, inner) { }
    }

    // --parent path does not exist at creation.
    public sealed class DifferencingParentMissingException : DifferencingVhdxException
    {
        public string ParentPath { get; }

        public DifferencingParentMissingException(string parentPath)
            : base($"parent {parentPath} does not exist — a differencing VHDX needs its full parent at creation")
        {
            ParentPath = parentPath;
        }
    }

    // Recorded parent gone at consumption (moved/renamed/deleted since the
    // child was created). A child without its parent is unreadable.
    public sealed class DifferencingOrphanException : DifferencingVhdxException
    {
        public string ChildPath { get; }
        public string RecordedParent { get; }

        public DifferencingOrphanException(string childPath, string recordedParent)
            : base($"orphan child {childPath}: recorded parent {recordedParent} no longer exists (moved, renamed, or deleted) — the child is unreadable without it; restore the parent to that path or repair the locator with Set-VHD -ParentPath")
        {
            ChildPath = childPath;
            RecordedParent = recordedParent;
        }
    }

    // Parent changed under a live child (size, mtime, or hash drift), or a
    // creation-time parent that is not this volume's full image. A changed
    // parent invalidates the child — fail loud, never read through it.
    public sealed class DifferencingParentChangedException : DifferencingVhdxException
    {
        public string ParentPath { get; }

        public DifferencingParentChangedException(string parentPath, string detail)
            : base($"parent {parentPath} fails identity: {detail}")
        {
            ParentPath = parentPath;
        }
    }

    // Non-full parent: itself a differencing disk (diff-of-diff) or a fixed
    // VHDX. Chains are the disease — depth 1, structural.
    public sealed class DifferencingDepthException : DifferencingVhdxException
    {
        public string ParentPath { get; }

        public DifferencingDepthException(string parentPath, string detail)
            : base($"parent {parentPath} is not a full dynamic VHDX: {detail}")
        {
            ParentPath = parentPath;
        }
    }

    // Sidecar provenance record (<child>.parent.json). Plain record, default
    // System.Text.Json shape like SnapshotManifest — zero new deps.
    public sealed record DifferencingParentRecord(
        int Version,
        string ChildName,
        string ParentPath,
        long ParentLength,
        long ParentMtimeUtcTicks,
        string ParentSha256,
        long Capacity,
        int ChunkBytes,
        List<long> DirtyChunks,
        long CreatedUtcTicks);

    public static class DifferencingVhdx
    {
        public const string SidecarSuffix = ".parent.json";
        public const int SidecarVersion = 1;

        // Diff-plan log cap: the INF plan line names every dirty index up to
        // this many, then `(+N more)`. True dirty is a handful of chunks, so
        // the full list is normally visible; the cap only bounds log spam
        // when the plan itself is sick.
        internal const int MaxPlanIndexesLogged = 32;

        private const int IoBufferBytes = 1024 * 1024;

        public static string SidecarPath(string childPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childPath);
            return childPath + SidecarSuffix;
        }

        // Creation-time parent gate (existence → full/type → capacity),
        // shared by the dirty plan and the create: curated exceptions fire
        // BEFORE any open, never a raw FileNotFoundException + stack.
        // Returns the parent capacity both callers record/compare.
        private static long GateParent(IBlockSource source, string parentPath)
        {
            if (!File.Exists(parentPath))
                throw new DifferencingParentMissingException(parentPath);
            try
            {
                AssertFullParent(parentPath);
            }
            catch (Exception ex) when (ex is not DifferencingVhdxException
                && (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException))
            {
                throw new DifferencingVhdxException($"parent {parentPath} is not a readable VHDX ({ex.GetType().Name}: {ex.Message})", ex);
            }
            var wantCapacity = VhdxExporter.AlignedCapacity(source.Length);
            var parentCapacity = ReadCapacity(parentPath);
            if (parentCapacity != wantCapacity)
                throw new DifferencingParentChangedException(parentPath,
                    $"size mismatch: parent capacity {parentCapacity} != source length {source.Length} (aligned {wantCapacity}) — the parent must be the full VHDX of this volume");
            return parentCapacity;
        }

        // Allocated runs snapshot for extent-aware live reads (null = dense
        // source, full reads). Fail closed on a nonsense cluster size:
        // guessing hole boundaries risks a child that verify approves on
        // matching zeros.
        private sealed record SparseRuns(IReadOnlyList<(long Start, long Count)> Runs, long BytesPerCluster);

        private static SparseRuns? TakeSparseRuns(IBlockSource source, string parentPath)
        {
            if (source is not ISparseBlockSource sparse)
                return null;
            if (sparse.BytesPerCluster <= 0)
                throw new DifferencingVhdxException($"cannot diff {parentPath}: sparse source reports BytesPerCluster={sparse.BytesPerCluster} — refusing to guess hole boundaries");
            return new SparseRuns(sparse.GetAllocatedRuns(), sparse.BytesPerCluster);
        }

        // Extent-aware live fill of buf[0..length) for volume bytes
        // [offset, offset+length): allocated subranges are seek+read, holes
        // stay zero and are NEVER read — unallocated shadow regions are the
        // B7d garbage source. Dense sources (runs null) keep the plain
        // sequential-shape read. Short reads fail loud with the caller's
        // message (plan, confirm, write, and verify each name their own).
        private static void FillLiveChunk(Stream live, byte[] buf, long offset, int length, SparseRuns? runs, string shortReadMessage)
        {
            if (runs == null)
            {
                live.Seek(offset, SeekOrigin.Begin);
                if (ReadExactly(live, buf, length) != length)
                    throw new DifferencingVhdxException(shortReadMessage);
                return;
            }
            Array.Clear(buf, 0, length);
            foreach (var (start, len) in AllocatedByteRanges(runs.Runs, runs.BytesPerCluster, offset, length))
            {
                live.Seek(start, SeekOrigin.Begin);
                var at = (int)(start - offset);
                var filled = 0;
                int n;
                while (filled < len && (n = live.Read(buf, at + filled, len - filled)) > 0)
                    filled += n;
                if (filled != len)
                    throw new DifferencingVhdxException(shortReadMessage);
            }
        }

        // Allocated byte ranges intersecting [offset, offset+length), from
        // cluster-unit runs (sorted, non-overlapping — the volume-bitmap
        // shape). Lower-bound binary search + forward walk: O(log R +
        // overlaps), no per-chunk full scan.
        private static List<(long Start, int Length)> AllocatedByteRanges(
            IReadOnlyList<(long Start, long Count)> runs, long bytesPerCluster, long offset, int length)
        {
            var ranges = new List<(long Start, int Length)>();
            if (runs.Count == 0 || length <= 0)
                return ranges;
            var end = offset + length;
            var lo = 0;
            var hi = runs.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if ((runs[mid].Start + runs[mid].Count) * bytesPerCluster <= offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            for (var i = lo; i < runs.Count; i++)
            {
                var rs = runs[i].Start * bytesPerCluster;
                if (rs >= end)
                    break;
                var re = (runs[i].Start + runs[i].Count) * bytesPerCluster;
                var s = Math.Max(rs, offset);
                var e = Math.Min(re, end);
                if (e > s)
                    ranges.Add((s, (int)(e - s)));
            }
            return ranges;
        }

        // Index-list rendering for the plan line and the unstable refusal:
        // full list when small, first-N + count when huge.
        private static string FormatIndexes(IReadOnlyList<long> indexes)
            => indexes.Count <= MaxPlanIndexesLogged
                ? $"[{string.Join(", ", indexes)}]"
                : $"[{string.Join(", ", indexes.Take(MaxPlanIndexesLogged))} (+{indexes.Count - MaxPlanIndexesLogged} more)]";

        private static string UnstableSuffix(IReadOnlyList<long> unstableIndexes)
            => unstableIndexes.Count == 0 ? "" : $"; unstable chunks: {FormatIndexes(unstableIndexes)}";

        // §12.6 dirty computation: chunk-wise compare of the live source
        // against the full parent VHDX on the store chunk grid. Byte
        // compare only — equality is all the child needs. Short parent
        // reads (parent smaller than the source) count as dirty; a parent
        // larger than the source is a size mismatch the gate refuses.
        //
        // The plan is CONFIRMED by a second, independently-opened live
        // pass over dirty candidates only. A single sequential pass
        // over-reported ~60x on-box (stable non-truth bytes from the
        // shadow read stack over unallocated regions) while the
        // seek/verify pattern stayed clean, so one pass is not trusted:
        // a candidate the confirm pass reads parent-equal is dropped as
        // first-pass garbage; a candidate whose bytes disagree across the
        // two passes fails LOUD (truth unknown — writing either copy
        // risks a poisoned child that verify could approve on matching
        // garbage). Only live bytes hashed here (32 B/chunk, bounded);
        // the parent is a stable file the base verify already pins.
        //
        // B7d root cause: reads over UNALLOCATED shadow regions return
        // stable-membership / nondeterministic-byte garbage (412
        // candidates vs ~3 true dirty, 56 cross-pass unstable, and two
        // reads of one frozen vss-dump disagreeing — live writes cannot
        // explain that). So sparse sources never traverse holes at all:
        // allocated subranges are seek+read, holes stay zero (zeros by
        // bitmap definition, matching the hole-skipped parent BackupVhdx
        // writes). Dense sources (plain files don't desync) keep the full
        // read. Every candidate logs a 3-way trace (index + pass hashes +
        // parent-equal) BEFORE the loud refusal, so a refused diff still
        // leaves its index list in the log; the INF plan line names the
        // kept dirty indexes (capped).
        public static HashSet<long> ComputeDirtyVsParent(IBlockSource source, string parentPath, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            ArgumentNullException.ThrowIfNull(log);
            GateParent(source, parentPath);
            SparseRuns? runs = TakeSparseRuns(source, parentPath);
            var candidates = new List<(long Index, byte[] Hash)>();
            long total = 0;
            using (var disk = new Disk(parentPath, FileAccess.Read))
            using (var parent = disk.Content)
            using (var live = source.OpenRead())
            {
                var a = new byte[chunkBytes];
                var b = new byte[chunkBytes];
                for (long offset = 0; offset < source.Length; offset += chunkBytes, total++)
                {
                    var length = (int)Math.Min(chunkBytes, source.Length - offset);
                    FillLiveChunk(live, a, offset, length, runs, $"short source read at chunk {total} (offset {offset}) — cannot diff {parentPath}");
                    parent.Seek(offset, SeekOrigin.Begin);
                    var got = ReadExactly(parent, b, length);
                    if (got != length || !a.AsSpan(0, length).SequenceEqual(b.AsSpan(0, length)))
                        candidates.Add((total, SHA256.HashData(a.AsSpan(0, length))));
                }
                // Confirm through a fresh, seeked live pass (fresh fills,
                // not the first pass's segment state) plus a parent re-read.
                var dirty = new HashSet<long>();
                var dropped = 0;
                var unstable = 0;
                var unstableIndexes = new List<long>();
                if (candidates.Count > 0)
                {
                    using var live2 = source.OpenRead();
                    var c = new byte[chunkBytes];
                    var d = new byte[chunkBytes];
                    foreach (var (index, hash) in candidates)
                    {
                        var offset = index * (long)chunkBytes;
                        var length = (int)Math.Min(chunkBytes, source.Length - offset);
                        FillLiveChunk(live2, c, offset, length, runs, $"short source re-read at chunk {index} (offset {offset}) — cannot diff {parentPath}");
                        parent.Seek(offset, SeekOrigin.Begin);
                        // Short parent re-read mirrors pass 1 (dirty, never
                        // clean); a clean live2-vs-parent2 drops first-pass
                        // garbage from either side; anything else needs
                        // pass-1 agreement or it is untrustworthy.
                        var parentEqual = ReadExactly(parent, d, length) == length && c.AsSpan(0, length).SequenceEqual(d.AsSpan(0, length));
                        var hash2 = SHA256.HashData(c.AsSpan(0, length));
                        var stable = hash2.AsSpan().SequenceEqual(hash);
                        string decision;
                        if (parentEqual)
                        {
                            dropped++;
                            decision = "dropped";
                        }
                        else if (stable)
                        {
                            dirty.Add(index);
                            decision = "kept";
                        }
                        else
                        {
                            unstable++;
                            unstableIndexes.Add(index);
                            decision = "unstable";
                        }
                        log.Info($"diff confirm chunk {index}: pass1={Convert.ToHexString(hash)} pass2={Convert.ToHexString(hash2)} parentEqual={(parentEqual ? "true" : "false")} decision={decision}");
                    }
                }
                if (unstable > 0)
                    throw new DifferencingVhdxException($"cannot diff {parentPath}: live source reads disagree across two passes ({unstable} of {candidates.Count} candidates unstable{UnstableSuffix(unstableIndexes)}) — refusing to build a child from untrustworthy bytes (re-run; if it persists, probe shadow-read stability with --vss-dump and the host disk SMART)");
                var kept = dirty.OrderBy(i => i).ToList();
                log.Info($"diff plan vs {parentPath}: {dirty.Count} dirty of {total} chunks {FormatIndexes(kept)}" + (dropped > 0 ? $" ({dropped} first-pass candidates dropped on confirm)" : ""));
                return dirty;
            }
        }

        // Create a depth-1 differencing VHDX: parent checks (exists, full
        // dynamic, capacity matches this source) → record identity → create
        // the child → write every dirty chunk from the incremental plan →
        // sidecar (temp+rename) → full verify. Returns the child path.
        // Target rules (auto-name, collision, on-source, bare) are the
        // shared SingleFileTarget ones — same flag family, same refusals.
        public static string CreateDiff(IBlockSource source, string volume, string? targetArg, string parentPath,
            IReadOnlySet<long> dirtyChunks, int chunkBytes, ILogger log, DateTime? now = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
            ArgumentNullException.ThrowIfNull(dirtyChunks);
            ArgumentNullException.ThrowIfNull(log);
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            var target = SingleFileTarget.Resolve(targetArg, "--to-vhdx", volume, "vhdx", now);
            var fullTarget = Path.GetFullPath(target);
            var fullParent = Path.GetFullPath(parentPath);
            var samePath = OperatingSystem.IsWindows()
                ? fullTarget.Equals(fullParent, StringComparison.OrdinalIgnoreCase)
                : fullTarget.Equals(fullParent, StringComparison.Ordinal);
            if (samePath)
                throw new DifferencingVhdxException($"parent {parentPath} is the target itself ({target}) — a differencing VHDX cannot parent itself");
            var parentCapacity = GateParent(source, parentPath);
            var chunkCount = (source.Length + chunkBytes - 1) / chunkBytes;
            foreach (var index in dirtyChunks)
            {
                if (index < 0 || index >= chunkCount)
                    throw new DifferencingVhdxException($"dirty chunk {index} is outside this source (0..{chunkCount - 1}, {chunkBytes}-byte grid) — the incremental plan belongs to another volume");
            }

            var info = new FileInfo(fullParent);
            var record = new DifferencingParentRecord(
                SidecarVersion,
                Path.GetFileName(target),
                fullParent,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                ShaFile(fullParent),
                parentCapacity,
                chunkBytes,
                dirtyChunks.OrderBy(i => i).ToList(),
                DateTime.UtcNow.Ticks);

            using (var disk = Disk.InitializeDifferencing(target, fullParent))
            {
                using var stream = source.OpenRead();
                var runs = TakeSparseRuns(source, parentPath);
                foreach (var index in record.DirtyChunks)
                {
                    var offset = index * (long)chunkBytes;
                    var length = (int)Math.Min(chunkBytes, source.Length - offset);
                    var data = new byte[length];
                    FillLiveChunk(stream, data, offset, length, runs, $"short source read at dirty chunk {index} (offset {offset}) — cannot write {target}");
                    // Every dirty chunk is written EVEN IF ALL ZERO: an
                    // unwritten block falls through to the parent, which
                    // holds the OLD bytes — a zeroed range would resurrect.
                    disk.Content.Seek(offset, SeekOrigin.Begin);
                    disk.Content.Write(data, 0, data.Length);
                }
                disk.Content.Flush();
            }

            try
            {
                WriteSidecar(target, record);
                VerifyDiff(target, source, log);
            }
            catch (Exception)
            {
                // A refused child must not linger: a verify-rejected child
                // still mounts, so remove our just-written child + sidecar
                // (both are ours — collision refuses pre-existing targets).
                TryDelete(target);
                TryDelete(SidecarPath(target));
                throw;
            }
            log.Info($"created differencing VHDX {target} on parent {fullParent} ({record.DirtyChunks.Count} dirty chunks)");
            return target;
        }

        // Verify entry: re-check parent identity (size+mtime+hash) from the
        // sidecar, then readability — open the pair through the locator and
        // SHA-compare every dirty range against the source. Loud on all
        // four refusals (missing parent, orphan, size-mismatch, non-full).
        public static void VerifyDiff(string childPath, IBlockSource source, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childPath);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(childPath))
                throw new SingleFileSourceNotFoundException(childPath);
            using (var child = new DiskImageFile(childPath, FileAccess.Read, false))
            {
                if (!child.NeedsParent)
                    throw new DifferencingVhdxException($"verify refuses {childPath}: not a differencing VHDX (point --parent verification at the child, not the full)");
            }
            var sidecar = SidecarPath(childPath);
            if (!File.Exists(sidecar))
                throw new DifferencingVhdxException($"verify refuses {childPath}: provenance record {sidecar} is missing — parent identity cannot be checked");
            DifferencingParentRecord? record;
            try
            {
                record = JsonSerializer.Deserialize(File.ReadAllText(sidecar), CorpusJsonContext.Default.DifferencingParentRecord);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                throw new DifferencingVhdxException($"verify refuses {childPath}: provenance record {sidecar} is unreadable ({ex.Message})", ex);
            }
            if (record == null)
                throw new DifferencingVhdxException($"verify refuses {childPath}: provenance record {sidecar} is empty");
            if (!File.Exists(record.ParentPath))
                throw new DifferencingOrphanException(childPath, record.ParentPath);
            var info = new FileInfo(record.ParentPath);
            if (info.Length != record.ParentLength)
                throw new DifferencingParentChangedException(record.ParentPath,
                    $"size mismatch: now {info.Length} bytes, was {record.ParentLength} at creation of {childPath} — the parent changed under a live child");
            if (info.LastWriteTimeUtc.Ticks != record.ParentMtimeUtcTicks)
                throw new DifferencingParentChangedException(record.ParentPath,
                    $"mtime mismatch (was {new DateTime(record.ParentMtimeUtcTicks, DateTimeKind.Utc):O}) — the parent changed under live child {childPath}");
            var hash = ShaFile(record.ParentPath);
            if (!string.Equals(hash, record.ParentSha256, StringComparison.Ordinal))
                throw new DifferencingParentChangedException(record.ParentPath,
                    $"SHA-256 mismatch — the parent changed under live child {childPath} (same size+mtime, different bytes)");
            AssertFullParent(record.ParentPath);

            using var disk = new Disk(childPath, FileAccess.Read);
            if (disk.Capacity < source.Length)
                throw new DifferencingVhdxException($"verify refuses {childPath}: pair capacity {disk.Capacity} < source length {source.Length}");
            using var src = source.OpenRead();
            var runs = TakeSparseRuns(source, record.ParentPath);
            var bufA = new byte[IoBufferBytes];
            var bufB = new byte[IoBufferBytes];
            var compared = 0L;
            foreach (var index in record.DirtyChunks)
            {
                var offset = index * (long)record.ChunkBytes;
                var length = Math.Min((long)record.ChunkBytes, source.Length - offset);
                try
                {
                    CompareRange(src, disk.Content, offset, length, bufA, bufB, childPath, runs);
                }
                catch (PairMismatchException mismatch)
                {
                    throw DescribeMismatch(childPath, record.ParentPath, mismatch);
                }
                compared += length;
            }
            log.Info($"verified {childPath}: parent {record.ParentPath} identity OK, {compared} bytes re-read OK through the pair");
        }

        // Byte-level mismatch inside the pair: internal carrier (never
        // escapes VerifyDiff, which rethrows the curated message below).
        private sealed class PairMismatchException : DifferencingVhdxException
        {
            public long Offset { get; }
            public byte SourceByte { get; }
            public byte PairByte { get; }

            public PairMismatchException(string childPath, long offset, byte sourceByte, byte pairByte)
                : base($"verify refuses {childPath}: byte mismatch at offset {offset} (source 0x{sourceByte:X2}, pair 0x{pairByte:X2})")
            {
                Offset = offset;
                SourceByte = sourceByte;
                PairByte = pairByte;
            }
        }

        // Attribution for a pair mismatch: the parent byte at the failing
        // offset discriminates a missing child block (pair == parent: the
        // child carries nothing here — plan garbage or a lost block) from
        // live-read instability (pair holds non-parent bytes yet differs
        // from the re-read source: the live source answered differently at
        // plan/write/verify time — not a lost write).
        private static DifferencingVhdxException DescribeMismatch(string childPath, string parentPath, PairMismatchException mismatch)
        {
            var parentByte = TryReadParentByte(parentPath, mismatch.Offset);
            var parentPart = parentByte.HasValue ? $", parent 0x{parentByte.Value:X2}" : " (parent re-read unavailable)";
            if (parentByte.HasValue && parentByte.Value == mismatch.PairByte)
                return new DifferencingVhdxException($"{mismatch.Message}{parentPart}: pair matches the parent here, so the child carries nothing for this range (plan marked it dirty but no child block landed) — suspect first-pass plan garbage or a lost child block");
            return new DifferencingVhdxException($"{mismatch.Message}{parentPart}: the child block IS present (differs from the parent) but differs from the re-read source — the live source returned different bytes at plan/write/verify time (unstable reads), not a lost write");
        }

        // One sector read of the parent's virtual bytes at an offset (VHDX
        // content reads require sector-aligned counts). Best-effort: null
        // when the parent cannot be re-read (the mismatch stays loud).
        private static byte? TryReadParentByte(string parentPath, long offset)
        {
            try
            {
                using var disk = new Disk(parentPath, FileAccess.Read);
                var sector = (offset / disk.BlockSize) * disk.BlockSize;
                var buf = new byte[disk.BlockSize];
                disk.Content.Seek(sector, SeekOrigin.Begin);
                if (ReadExactly(disk.Content, buf, buf.Length) != buf.Length)
                    return null;
                return buf[offset - sector];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                return null;
            }
        }

        // Best-effort removal of our own just-written artifacts after a
        // refused create (house TryDelete idiom: specific catches, silent).
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Full = dynamic with no parent. Differencing (HasParent) names its
        // grandparent from the locator; fixed is refused as non-dynamic.
        private static void AssertFullParent(string parentPath)
        {
            using var image = new DiskImageFile(parentPath, FileAccess.Read, false);
            if (image.NeedsParent || image.Information.HasParent)
            {
                // Locator keys are MS-VHDX names (absolute_win32_path,
                // relative_path, parent_linkage GUID); prefer the readable
                // path, fall back to whatever the locator carries.
                var entries = image.Information.ParentLocatorEntries;
                var grandparent = entries.TryGetValue("absolute_win32_path", out var abs) ? abs
                    : entries.TryGetValue("relative_path", out var rel) ? rel
                    : entries.Values.FirstOrDefault() ?? image.ParentUniqueId.ToString();
                throw new DifferencingDepthException(parentPath,
                    $"it is itself a differencing disk (grandparent {grandparent}) — diff-of-diff refused, depth 1 only");
            }
            if (!image.Information.IsDynamicDisk)
                throw new DifferencingDepthException(parentPath, "it is a fixed VHDX — the parent must be a dynamic full image");
        }

        private static long ReadCapacity(string parentPath)
        {
            using var disk = new Disk(parentPath, FileAccess.Read);
            return disk.Capacity;
        }

        private static void WriteSidecar(string target, DifferencingParentRecord record)
        {
            var sidecar = SidecarPath(target);
            if (File.Exists(sidecar) || Directory.Exists(sidecar))
                throw new SingleFileTargetExistsException(sidecar);
            var partial = sidecar + ".tmp";
            File.WriteAllText(partial, JsonSerializer.Serialize(record, CorpusJsonContext.Default.DifferencingParentRecord));
            File.Move(partial, sidecar);
        }

        private static string ShaFile(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            var buf = new byte[IoBufferBytes];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }

        private static void CompareRange(Stream src, Stream content, long start, long length, byte[] bufA, byte[] bufB, string childPath, SparseRuns? runs)
        {
            content.Seek(start, SeekOrigin.Begin);
            var remaining = length;
            var pos = start;
            while (remaining > 0)
            {
                var want = (int)Math.Min(bufA.Length, remaining);
                // Live side is extent-aware too: hole bytes are zeros by
                // bitmap definition (the child carries explicit zeros for
                // them), never re-read from the shadow.
                FillLiveChunk(src, bufA, pos, want, runs, $"verify refuses {childPath}: short source re-read at offset {pos}");
                var nb = ReadExactly(content, bufB, want);
                if (nb != want)
                    throw new DifferencingVhdxException($"verify refuses {childPath}: short read at offset {pos} (pair {nb}, want {want})");
                for (var i = 0; i < want; i++)
                {
                    if (bufA[i] != bufB[i])
                        throw new PairMismatchException(childPath, pos + i, bufA[i], bufB[i]);
                }
                remaining -= want;
                pos += want;
            }
        }

        private static int ReadExactly(Stream s, byte[] buf, int want)
        {
            var filled = 0;
            int n;
            while (filled < want && (n = s.Read(buf, filled, want - filled)) > 0)
                filled += n;
            return filled;
        }
    }
}
