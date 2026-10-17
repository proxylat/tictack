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

        private const int IoBufferBytes = 1024 * 1024;

        public static string SidecarPath(string childPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childPath);
            return childPath + SidecarSuffix;
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
            if (!File.Exists(fullParent))
                throw new DifferencingParentMissingException(parentPath);
            AssertFullParent(fullParent);
            var wantCapacity = VhdxExporter.AlignedCapacity(source.Length);
            var parentCapacity = ReadCapacity(fullParent);
            if (parentCapacity != wantCapacity)
                throw new DifferencingParentChangedException(parentPath,
                    $"size mismatch: parent capacity {parentCapacity} != source length {source.Length} (aligned {wantCapacity}) — the parent must be the full VHDX of this volume");
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
                foreach (var index in record.DirtyChunks)
                {
                    var offset = index * (long)chunkBytes;
                    var length = (int)Math.Min(chunkBytes, source.Length - offset);
                    var data = new byte[length];
                    stream.Seek(offset, SeekOrigin.Begin);
                    if (ReadExactly(stream, data, length) != length)
                        throw new DifferencingVhdxException($"short source read at dirty chunk {index} (offset {offset}) — cannot write {target}");
                    // Every dirty chunk is written EVEN IF ALL ZERO: an
                    // unwritten block falls through to the parent, which
                    // holds the OLD bytes — a zeroed range would resurrect.
                    disk.Content.Seek(offset, SeekOrigin.Begin);
                    disk.Content.Write(data, 0, data.Length);
                }
                disk.Content.Flush();
            }

            WriteSidecar(target, record);
            VerifyDiff(target, source, log);
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
                record = JsonSerializer.Deserialize<DifferencingParentRecord>(File.ReadAllText(sidecar));
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
            var bufA = new byte[IoBufferBytes];
            var bufB = new byte[IoBufferBytes];
            var compared = 0L;
            foreach (var index in record.DirtyChunks)
            {
                var offset = index * (long)record.ChunkBytes;
                var length = Math.Min((long)record.ChunkBytes, source.Length - offset);
                CompareRange(src, disk.Content, offset, length, bufA, bufB, childPath);
                compared += length;
            }
            log.Info($"verified {childPath}: parent {record.ParentPath} identity OK, {compared} bytes re-read OK through the pair");
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
            File.WriteAllText(partial, JsonSerializer.Serialize(record));
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

        private static void CompareRange(Stream src, Stream content, long start, long length, byte[] bufA, byte[] bufB, string childPath)
        {
            src.Seek(start, SeekOrigin.Begin);
            content.Seek(start, SeekOrigin.Begin);
            var remaining = length;
            var pos = start;
            while (remaining > 0)
            {
                var want = (int)Math.Min(bufA.Length, remaining);
                var na = ReadExactly(src, bufA, want);
                var nb = ReadExactly(content, bufB, want);
                if (na != want || nb != want)
                    throw new DifferencingVhdxException($"verify refuses {childPath}: short read at offset {pos} (source {na}, pair {nb}, want {want})");
                for (var i = 0; i < want; i++)
                {
                    if (bufA[i] != bufB[i])
                        throw new DifferencingVhdxException($"verify refuses {childPath}: byte mismatch at offset {pos + i} (source 0x{bufA[i]:X2}, pair 0x{bufB[i]:X2})");
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
