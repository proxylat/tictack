using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DiscUtils.Streams;
using DiscUtils.Vhdx;
using TicTack;
using ZstdSharp;

namespace Corpus
{
    // §12.1 direct single-file backup: extent stream → one VHDX / WIM / ESD,
    // no chunk store, no manifests. Every refusal is a distinct exception
    // carrying the paths involved; the CLI maps them to exit codes.

    public class SingleFileBackupException : Exception
    {
        public SingleFileBackupException(string message) : base(message) { }
        public SingleFileBackupException(string message, Exception inner) : base(message, inner) { }
    }

    // Bare --to-* flag: no <file|dir> given.
    public sealed class SingleFileTargetMissingException : SingleFileBackupException
    {
        public string Flag { get; }

        public SingleFileTargetMissingException(string flag)
            : base($"bare {flag} refuses: a target <file|dir> is required")
        {
            Flag = flag;
        }
    }

    // Collision: never overwrite, never auto-suffix — not even auto-names.
    public sealed class SingleFileTargetExistsException : SingleFileBackupException
    {
        public string TargetPath { get; }

        public SingleFileTargetExistsException(string targetPath)
            : base($"target {targetPath} already exists — refusing to overwrite (pick another path)")
        {
            TargetPath = targetPath;
        }

        public SingleFileTargetExistsException(string targetPath, Exception inner)
            : base($"target {targetPath} already exists — refusing to overwrite (pick another path)", inner)
        {
            TargetPath = targetPath;
        }
    }

    // Target inside the live volume being captured: space-exhaustion risk.
    public sealed class SingleFileTargetOnSourceException : SingleFileBackupException
    {
        public string SourceVolume { get; }
        public string TargetPath { get; }

        public SingleFileTargetOnSourceException(string sourceVolume, string targetPath)
            : base($"target {targetPath} is on the source volume {sourceVolume} — writing the backup into the live volume being captured risks space-exhaustion mid-run")
        {
            SourceVolume = sourceVolume;
            TargetPath = targetPath;
        }
    }

    public sealed class SingleFileSourceNotFoundException : SingleFileBackupException
    {
        public string SourcePath { get; }

        public SingleFileSourceNotFoundException(string sourcePath)
            : base($"source {sourcePath} does not exist")
        {
            SourcePath = sourcePath;
        }
    }

    public sealed class SingleFileDismException : SingleFileBackupException
    {
        public string ImagePath { get; }
        public int ExitCode { get; }

        public SingleFileDismException(string imagePath, string arguments, DismResult result)
            : base($"DISM failed for {imagePath} (exit {result.ExitCode}): dism.exe {arguments}\n{result.Stderr}".TrimEnd())
        {
            ImagePath = imagePath;
            ExitCode = result.ExitCode;
        }
    }

    public sealed class SingleFileVerifyException : SingleFileBackupException
    {
        public string ImagePath { get; }

        public SingleFileVerifyException(string imagePath, string detail)
            : base($"verify failed for {imagePath}: {detail}")
        {
            ImagePath = imagePath;
        }
    }

    // §12.11 append base missing: --append adds an index to an existing
    // WIM, it never creates one.
    public sealed class SingleFileBaseNotFoundException : SingleFileBackupException
    {
        public string BasePath { get; }

        public SingleFileBaseNotFoundException(string basePath)
            : base($"append base {basePath} does not exist — --append adds an index to an existing WIM, it never creates one")
        {
            BasePath = basePath;
        }
    }

    // §12.11 ESD/solid lock: refuse-to-append is the locked default. The
    // on-box DISM probe LANDED 2026-10-17 (DISM appends/splits ESD fine) —
    // the refusal stays as Corpus policy, not a DISM limit.
    public sealed class SingleFileAppendRefusedException : SingleFileBackupException
    {
        public string ImagePath { get; }

        public SingleFileAppendRefusedException(string imagePath, string reason)
            : base($"refusing --append to {imagePath}: {reason}")
        {
            ImagePath = imagePath;
        }
    }

    // --zstd with --to-wim/--to-esd: already compressed, refuses loud.
    public sealed class SingleFileZstdRefusedException : SingleFileBackupException
    {
        public SingleFileZstdRefusedException(SingleFileKind kind)
            : base($"--zstd refuses with {ToFlag(kind)} (WIM/ESD are already compressed; --zstd is --to-vhdx only)")
        {
        }

        private static string ToFlag(SingleFileKind kind) => kind switch
        {
            SingleFileKind.Vhdx => "--to-vhdx",
            SingleFileKind.Wim => "--to-wim",
            _ => "--to-esd",
        };
    }

    public enum SingleFileKind
    {
        Vhdx,
        Wim,
        Esd,
    }

    // TEMPORARY (§12.1 phase 1): A/B selector for the writer spike. Same
    // volume, same excludes, wall-clock + output bytes; VirtDisk ships only
    // if >15% faster, else DiscUtils ships. Delete the loser + this enum +
    // ParseKind the day the verdict lands — no dual maintenance either way.
    public enum VhdxWriterKind
    {
        DiscUtils,
        VirtDisk,
    }

    // Extent-stream sink seam: the backup loop writes data extents at byte
    // offsets; ranges never written stay sparse (dynamic VHDX reads them as
    // zero without allocating). There is deliberately no Skip call — not
    // writing IS the skip.
    public interface IExtentSink : IDisposable
    {
        string Path { get; }
        long CapacityBytes { get; }
        void Write(long offset, byte[] data);
        void Flush();
    }

    public static class ExtentSinkFactory
    {
        public static IExtentSink Create(string path, long volumeLength, VhdxWriterKind kind) => kind switch
        {
            VhdxWriterKind.DiscUtils => new DiscUtilsExtentSink(path, volumeLength),
            VhdxWriterKind.VirtDisk => new VirtDiskExtentSink(path, volumeLength),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown VHDX writer"),
        };

        // Temporary flag text; null/empty = spike default (DiscUtils).
        public static VhdxWriterKind ParseKind(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return VhdxWriterKind.DiscUtils;
            if (value.Equals("discutils", StringComparison.OrdinalIgnoreCase))
                return VhdxWriterKind.DiscUtils;
            if (value.Equals("virtdisk", StringComparison.OrdinalIgnoreCase))
                return VhdxWriterKind.VirtDisk;
            throw new SingleFileBackupException($"unknown VHDX writer '{value}' (want discutils|virtdisk)");
        }
    }

    // Candidate A: DiscUtils direct. In-tree, proven, Linux-testable.
    // Dynamic VHDX, 1 MiB blocks (§12.1), 512 B logical sectors (DiscUtils
    // default — pinned by VhdxTests.SectorSize, not set here).
    public sealed class DiscUtilsExtentSink : IExtentSink
    {
        private readonly FileStream _file;
        private readonly Disk _disk;
        private bool _disposed;

        public string Path { get; }
        public long CapacityBytes { get; }

        public DiscUtilsExtentSink(string path, long volumeLength)
        {
            ArgumentNullException.ThrowIfNull(path);
            Path = path;
            CapacityBytes = VhdxExporter.AlignedCapacity(volumeLength);
            try
            {
                _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException ex)
            {
                throw new SingleFileTargetExistsException(path, ex);
            }
            try
            {
                _disk = Disk.InitializeDynamic(_file, Ownership.None, CapacityBytes, null, SingleFileBackup.VhdxBlockBytes);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        public void Write(long offset, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _disk.Content.Seek(offset, SeekOrigin.Begin);
            _disk.Content.Write(data, 0, data.Length);
        }

        public void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _disk.Content.Flush();
            _file.Flush(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disk.Dispose();
            _file.Dispose();
        }
    }

    // Candidate B: VirtDisk P/Invoke (kernel parser). Windows-only: the
    // file is created, attached with no drive letter, written through the
    // physical-drive path, then detached. Needs elevation for attach.
    public sealed class VirtDiskExtentSink : IExtentSink
    {
        private const uint DeviceVhdx = 3;
        private const uint AttachNoDriveLetter = 2;
        private const uint SectorBytes = 512;
        private static readonly Guid VendorMicrosoft = new("ec984aec-a0f9-47e9-901f-71415a66345b");

        private IntPtr _handle;
        private FileStream? _device;
        private bool _disposed;

        public string Path { get; }
        public long CapacityBytes { get; }

        public VirtDiskExtentSink(string path, long volumeLength)
        {
            ArgumentNullException.ThrowIfNull(path);
            // Pure first: empty-length refusal is identical on every OS.
            var capacity = VhdxExporter.AlignedCapacity(volumeLength);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException($"VirtDisk VHDX writer needs Windows (virtdisk.dll); {path} was not created — use the discutils writer");
            Path = path;
            CapacityBytes = capacity;
            var storage = new VirtualStorageType { DeviceId = DeviceVhdx, VendorId = VendorMicrosoft };
            var create = new CreateVdiskParameters { Version = 1 };
            create.V1 = new CreateVdiskVersion1
            {
                UniqueId = Guid.Empty,
                MaximumSize = (ulong)capacity,
                BlockSizeInBytes = (uint)SingleFileBackup.VhdxBlockBytes,
                SectorSizeInBytes = SectorBytes,
                ParentPath = null,
                SourcePath = null,
            };
            var rc = CreateVirtualDisk(ref storage, path, 0, IntPtr.Zero, 0, 0, ref create, IntPtr.Zero, out _handle);
            if (rc != 0)
            {
                _handle = IntPtr.Zero;
                throw new IOException($"CreateVirtualDisk({path}) failed: virtdisk error {rc} (0x{rc:X8})");
            }
            try
            {
                var attach = new AttachVdiskParameters { Version = 1, Reserved = 0 };
                rc = AttachVirtualDisk(_handle, IntPtr.Zero, AttachNoDriveLetter, 0, ref attach, IntPtr.Zero);
                if (rc != 0)
                    throw new IOException($"AttachVirtualDisk({path}) failed: virtdisk error {rc} (0x{rc:X8}) — elevation required");
                var chars = new char[512];
                uint bytes = (uint)chars.Length * 2;
                rc = GetVirtualDiskPhysicalPath(_handle, ref bytes, chars);
                if (rc != 0)
                    throw new IOException($"GetVirtualDiskPhysicalPath({path}) failed: virtdisk error {rc} (0x{rc:X8})");
                var physical = new string(chars).TrimEnd('\0');
                _device = new FileStream(physical, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        public void Write(long offset, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _device!.Seek(offset, SeekOrigin.Begin);
            _device.Write(data, 0, data.Length);
        }

        public void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _device!.Flush(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                _device?.Dispose();
            }
            catch
            {
                // Best-effort: Dispose must not throw; detach still runs.
            }
            finally
            {
                _device = null;
            }
            if (_handle != IntPtr.Zero)
            {
                DetachVirtualDisk(_handle, 0, 0);
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VirtualStorageType
        {
            public uint DeviceId;
            public Guid VendorId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CreateVdiskVersion1
        {
            public Guid UniqueId;
            public ulong MaximumSize;
            public uint BlockSizeInBytes;
            public uint SectorSizeInBytes;
            public string? ParentPath;
            public string? SourcePath;
        }

        // CREATE_VIRTUAL_DISK_PARAMETERS, Version=1: the union starts at
        // offset 8 (largest member aligns 8 past the 4-byte version).
        [StructLayout(LayoutKind.Explicit)]
        private struct CreateVdiskParameters
        {
            [FieldOffset(0)]
            public int Version;
            [FieldOffset(8)]
            public CreateVdiskVersion1 V1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttachVdiskParameters
        {
            public int Version;
            public uint Reserved;
        }

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint CreateVirtualDisk(ref VirtualStorageType storageType, string path, uint accessMask, IntPtr securityDescriptor, uint flags, uint providerFlags, ref CreateVdiskParameters parameters, IntPtr overlapped, out IntPtr handle);

        [DllImport("virtdisk.dll")]
        private static extern uint AttachVirtualDisk(IntPtr handle, IntPtr securityDescriptor, uint flags, uint providerFlags, ref AttachVdiskParameters parameters, IntPtr overlapped);

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetVirtualDiskPhysicalPath(IntPtr handle, ref uint diskPathSizeInBytes, [Out] char[] diskPath);

        [DllImport("virtdisk.dll")]
        private static extern uint DetachVirtualDisk(IntPtr handle, uint flags, uint providerFlags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    // Shared target rules: dir form auto-names <vol>-<timestamp>.<ext>,
    // collision refuses, target-on-source refuses, bare flag refuses.
    public static class SingleFileTarget
    {
        public static string Resolve(string? targetArg, string flag, string volume, string ext, DateTime? now = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(flag);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentException.ThrowIfNullOrWhiteSpace(ext);
            if (string.IsNullOrWhiteSpace(targetArg))
                throw new SingleFileTargetMissingException(flag);
            var resolved = Directory.Exists(targetArg) || targetArg.EndsWith('/') || targetArg.EndsWith('\\')
                ? System.IO.Path.Combine(targetArg, BuildAutoName(volume, ext, now ?? DateTime.Now))
                : targetArg;
            if (File.Exists(resolved) || Directory.Exists(resolved))
                throw new SingleFileTargetExistsException(resolved);
            if (IsTargetOnSource(volume, resolved))
                throw new SingleFileTargetOnSourceException(volume, resolved);
            return resolved;
        }

        public static string BuildAutoName(string volume, string ext, DateTime now)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentException.ThrowIfNullOrWhiteSpace(ext);
            return SanitizeVolume(volume) + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "." + ext;
        }

        // "T:" → "T"; mount paths and GUID paths degrade to flat stems.
        public static string SanitizeVolume(string volume)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            var v = volume.Trim();
            if (v.StartsWith(@"\\?\", StringComparison.Ordinal))
                v = v.Substring(4);
            else if (v.StartsWith(@"\\.\", StringComparison.Ordinal))
                v = v.Substring(4);
            v = v.TrimEnd(':', '\\', '/');
            var invalid = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars()) { ':', '\\', '/' };
            var sb = new StringBuilder(v.Length);
            foreach (var c in v)
                sb.Append(invalid.Contains(c) ? '-' : c);
            return sb.Length == 0 ? "vol" : sb.ToString();
        }

        // String-prefix check on normalized separators, both raw (catches
        // Windows-style paths even when tests run on Linux) and GetFullPath
        // resolved (catches relative targets under a volume mount path).
        // Same volume named two ways (T: vs GUID path) is NOT detected —
        // the CLI passes --volume verbatim, same family as --to paths.
        public static bool IsTargetOnSource(string sourceVolume, string targetPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceVolume);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
            var root = Normalize(sourceVolume).TrimEnd('/') + "/";
            if (IsUnder(Normalize(targetPath), root))
                return true;
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(targetPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false; // Invalid path: sink creation fails loud naming it.
            }
            return IsUnder(Normalize(full), root);
        }

        private static string Normalize(string p) => p.Replace('\\', '/');

        private static bool IsUnder(string path, string root) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // Generated WimScript.ini translating CapturePolicy exclusions for
    // DISM /ConfigFile. Verbatim lines: DISM matches names/paths itself.
    public static class WimScript
    {
        public static string BuildIni(IReadOnlyList<string> exclusions)
        {
            ArgumentNullException.ThrowIfNull(exclusions);
            var sb = new StringBuilder("[ExclusionList]\r\n");
            foreach (var e in exclusions)
            {
                if (!string.IsNullOrWhiteSpace(e))
                    sb.Append(e.Trim()).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    // Mount-sample SHA: first maxFiles under the mounted image (sorted, so
    // deterministic), each SHA-256-compared against the capture source.
    // Pure directory compare — Linux-testable with two seeded trees.
    public static class WimSample
    {
        public static (int Sampled, List<string> Problems) Compare(string mountedDir, string sourceDir, int maxFiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mountedDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
            if (maxFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maxFiles));
            var rels = Directory.EnumerateFiles(mountedDir, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(mountedDir, f))
                .OrderBy(r => r, StringComparer.Ordinal)
                .Take(maxFiles)
                .ToList();
            var problems = new List<string>();
            foreach (var rel in rels)
            {
                var src = System.IO.Path.Combine(sourceDir, rel);
                if (!File.Exists(src))
                {
                    problems.Add($"{rel}: mounted file missing from capture source");
                    continue;
                }
                if (!string.Equals(ShaFile(System.IO.Path.Combine(mountedDir, rel)), ShaFile(src), StringComparison.Ordinal))
                    problems.Add($"{rel}: SHA-256 mismatch vs capture source");
            }
            return (rels.Count, problems);
        }

        private static string ShaFile(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(s));
        }
    }

    public sealed record DismResult(int ExitCode, string Stdout, string Stderr);

    public interface IDismRunner
    {
        DismResult Run(string arguments);
    }

    // Inbox DISM shell-out (zero deps). Unbounded wait: captures run for
    // hours — the 10-minute house default would kill them.
    public sealed class DismRunner : IDismRunner
    {
        public DismResult Run(string arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DISM capture needs Windows (inbox dism.exe)");
            var (code, stdout, stderr, _) = ProcessRunner.RunAsync(
                "dism.exe " + arguments, System.IO.Path.GetTempPath(), true, CancellationToken.None, Timeout.Infinite)
                .GetAwaiter().GetResult();
            return new DismResult(code, stdout, stderr);
        }
    }

    public static class SingleFileBackup
    {
        public const int VhdxBlockBytes = 1024 * 1024;
        public const int ExtentChunkBytes = 1024 * 1024; // aligned to VHDX blocks: never straddles an allocation unit
        public const int MaxWimSamples = 64;

        // --zstd sidecar codec, locked by the §12.4 verdict: streaming +
        // LDM at windowLog 27 (stock-`zstd -d` compatible) + level 3.
        public const int ZstdLevel = 3;
        public const int ZstdWindowLog = 27;
        public const int ZstdFrameBytes = 64 * 1024 * 1024;

        private const int VerifyBufferBytes = 1024 * 1024;

        public static void GuardZstd(SingleFileKind kind, bool zstd)
        {
            if (zstd && kind != SingleFileKind.Vhdx)
                throw new SingleFileZstdRefusedException(kind);
        }

        // VHDX single-file backup: stream extents (sparse holes + zero
        // chunks unwritten), then the mandatory verify pass. Returns the
        // final image path. exclusions null = resolve from the source
        // (Store pattern); WIM callers pass CLI/config lists instead.
        public static string BackupVhdx(IBlockSource source, string volume, string? targetArg, VhdxWriterKind writer, IExclusionResolver? exclusions, ILogger log, DateTime? now = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentNullException.ThrowIfNull(log);
            var target = SingleFileTarget.Resolve(targetArg, "--to-vhdx", volume, "vhdx", now);
            var resolver = exclusions ?? source as IExclusionResolver;
            var exclBpc = resolver?.BytesPerCluster ?? 0;
            var exclRuns = exclBpc > 0 ? resolver!.ExcludedRuns() : null;
            var written = new List<(long Start, long Length)>();
            var skipped = new List<(long Start, long Length)>();
            using (var sink = ExtentSinkFactory.Create(target, source.Length, writer))
            using (var stream = source.OpenRead())
            {
                bool[]? holeMap = null;
                if (source is ISparseBlockSource sparse)
                    holeMap = Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, ExtentChunkBytes);
                var chunks = holeMap != null
                    ? Chunker.ReadSparse(stream, ExtentChunkBytes, i => i < holeMap.Length && holeMap[i])
                    : Chunker.Read(stream, ExtentChunkBytes);
                foreach (var chunk in chunks)
                {
                    if (chunk.IsHole)
                    {
                        skipped.Add((chunk.Offset, Math.Min((long)ExtentChunkBytes, source.Length - chunk.Offset)));
                        continue;
                    }
                    if (exclRuns != null && exclRuns.Count > 0)
                        CapturePolicy.ZeroExcludedRanges(chunk.Data, chunk.Offset, exclRuns, exclBpc);
                    if (IsAllZero(chunk.Data))
                    {
                        skipped.Add((chunk.Offset, chunk.Data.Length));
                        continue;
                    }
                    sink.Write(chunk.Offset, chunk.Data);
                    written.Add((chunk.Offset, chunk.Data.Length));
                }
                sink.Flush();
            }
            if (resolver != null && resolver.ExclusionNames.Count > 0)
                log.Warn($"excluded extents restored as zeros: {string.Join(", ", resolver.ExclusionNames)}");
            VerifyVhdx(target, source, written, skipped, log, exclRuns, exclBpc);
            log.Info($"backed up volume {volume} ({source.Length} bytes, {written.Count} extents, {skipped.Count} sparse) to {target}");
            return target;
        }

        // VHDX verify: re-read every written extent and compare to source;
        // skipped ranges must read back zero AND hold no allocation.
        // Excluded sub-ranges inside written extents (partial-chunk pagefile
        // etc.) read back zero by design — asserted as zero, never compared
        // to the live source (proven on-box 2026-10-17: comparing them
        // false-fails against live pagefile bytes).
        public static void VerifyVhdx(string vhdxPath, IBlockSource source, IReadOnlyList<(long Start, long Length)> written, IReadOnlyList<(long Start, long Length)> skipped, ILogger log, IReadOnlyList<(long StartLcn, long ClusterCount)>? exclRuns = null, long exclBpc = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(vhdxPath);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(written);
            ArgumentNullException.ThrowIfNull(skipped);
            ArgumentNullException.ThrowIfNull(log);
            using var disk = new Disk(vhdxPath, FileAccess.Read);
            if (disk.Capacity < source.Length)
                throw new SingleFileVerifyException(vhdxPath, $"capacity {disk.Capacity} < source length {source.Length}");
            var content = disk.Content;
            using var src = source.OpenRead();
            var bufA = new byte[VerifyBufferBytes];
            var bufB = new byte[VerifyBufferBytes];
            var excl = ExcludedByteRanges(exclRuns, exclBpc);
            var compared = 0L;
            foreach (var (start, length) in written)
            {
                CompareRange(src, content, start, length, bufA, bufB, vhdxPath, excl);
                compared += length;
            }
            foreach (var (start, length) in skipped)
            {
                AssertZeroRange(content, start, length, bufB, vhdxPath);
                if (content is SparseStream sparse && sparse.GetExtentsInRange(start, length).Any())
                    throw new SingleFileVerifyException(vhdxPath, $"skipped range [{start},{start + length}) is allocated on disk (not sparse)");
            }
            log.Info($"verified {vhdxPath}: {compared} bytes re-read OK, {skipped.Count} skipped ranges sparse");
        }

        // Snapshot-dir liveness: VSS shadow device paths
        // (\\?\GLOBALROOT\...) are not Win32-visible, so
        // Directory.Exists is always false on them even while the
        // snapshot we hold is live (proven on-box 2026-10-17). A
        // GLOBALROOT path that fails to open as a file is still a live
        // device object — DISM does the real work; anything else keeps
        // the old loud refusal. A dead snapshot surfaces as
        // File/DirectoryNotFound (device object gone).
        private static bool SnapshotDirLive(string path)
        {
            if (Directory.Exists(path))
                return true;
            if (!path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                using var probe = new FileStream(path.TrimEnd('\\'), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        // WIM single-file backup: capture from our VSS snapshot path via
        // inbox DISM, then DISM verify + mount-sample SHA. Windows-only
        // (DismRunner throws loud); the arg shapes + ini + sampling are
        // Linux-provable behind IDismRunner fakes.
        public static string BackupWim(string snapshotPath, string volume, string? targetArg, IReadOnlyList<string>? exclusions, IDismRunner dism, ILogger log, DateTime? now = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentNullException.ThrowIfNull(dism);
            ArgumentNullException.ThrowIfNull(log);
            if (!SnapshotDirLive(snapshotPath))
                throw new SingleFileSourceNotFoundException(snapshotPath);
            var target = SingleFileTarget.Resolve(targetArg, "--to-wim", volume, "wim", now);
            var ini = WriteTempIni(CapturePolicy.EffectiveSkip(exclusions));
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(target);
                RunDism(dism, target, $"/Capture-Image /ImageFile:{ProcessRunner.QuoteArg(target)} /CaptureDir:{ProcessRunner.QuoteArg(snapshotPath)} /Name:{ProcessRunner.QuoteArg(name)} /Compress:max /CheckIntegrity /ConfigFile:{ProcessRunner.QuoteArg(ini)}");
                VerifyWim(target, snapshotPath, dism, log, mountSample: true);
            }
            finally
            {
                TryDelete(ini);
            }
            log.Info($"backed up volume {volume} from {snapshotPath} to {target}");
            return target;
        }

        // ESD: capture to a temp WIM (fully verified), export with
        // /Compress:recovery, delete the temp. The ESD itself gets DISM
        // info verification — solid ESDs are not mount-verified (DISM
        // mounts are a WIM path; the temp WIM carried the byte proof).
        public static string BackupEsd(string snapshotPath, string volume, string? targetArg, IReadOnlyList<string>? exclusions, IDismRunner dism, ILogger log, DateTime? now = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume);
            ArgumentNullException.ThrowIfNull(dism);
            ArgumentNullException.ThrowIfNull(log);
            if (!SnapshotDirLive(snapshotPath))
                throw new SingleFileSourceNotFoundException(snapshotPath);
            var target = SingleFileTarget.Resolve(targetArg, "--to-esd", volume, "esd", now);
            var tempWim = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corpus-" + Guid.NewGuid().ToString("N") + ".wim");
            var ini = WriteTempIni(CapturePolicy.EffectiveSkip(exclusions));
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(target);
                RunDism(dism, tempWim, $"/Capture-Image /ImageFile:{ProcessRunner.QuoteArg(tempWim)} /CaptureDir:{ProcessRunner.QuoteArg(snapshotPath)} /Name:{ProcessRunner.QuoteArg(name)} /Compress:max /CheckIntegrity /ConfigFile:{ProcessRunner.QuoteArg(ini)}");
                VerifyWim(tempWim, snapshotPath, dism, log, mountSample: true);
                RunDism(dism, target, $"/Export-Image /SourceImageFile:{ProcessRunner.QuoteArg(tempWim)} /SourceIndex:1 /DestinationImageFile:{ProcessRunner.QuoteArg(target)} /Compress:recovery /CheckIntegrity");
                VerifyWim(target, snapshotPath, dism, log, mountSample: false);
            }
            finally
            {
                TryDelete(tempWim);
                TryDelete(ini);
            }
            log.Info($"backed up volume {volume} from {snapshotPath} to {target}");
            return target;
        }

        public static void VerifyWim(string imagePath, string sourceDir, IDismRunner dism, ILogger log, bool mountSample = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
            ArgumentNullException.ThrowIfNull(dism);
            ArgumentNullException.ThrowIfNull(log);
            RunDism(dism, imagePath, $"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(imagePath)}");
            if (!mountSample)
            {
                log.Info($"verified {imagePath}: DISM image info OK");
                return;
            }
            var mountDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corpus-wim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mountDir);
            try
            {
                RunDism(dism, imagePath, $"/Mount-Image /ImageFile:{ProcessRunner.QuoteArg(imagePath)} /Index:1 /MountDir:{ProcessRunner.QuoteArg(mountDir)} /ReadOnly");
                Exception? sampleError = null;
                Exception? unmountError = null;
                var sampled = 0;
                try
                {
                    var (n, problems) = WimSample.Compare(mountDir, sourceDir, MaxWimSamples);
                    sampled = n;
                    if (problems.Count > 0)
                        sampleError = new SingleFileVerifyException(imagePath, "mount samples mismatch: " + string.Join("; ", problems.Take(5)));
                }
                finally
                {
                    var u = dism.Run($"/Unmount-Image /MountDir:{ProcessRunner.QuoteArg(mountDir)} /Discard");
                    if (u.ExitCode != 0)
                    {
                        var msg = $"unmount of {mountDir} failed (exit {u.ExitCode}): {u.Stderr} — clean up with DISM /Unmount-Image /Discard";
                        if (sampleError == null)
                            unmountError = new SingleFileVerifyException(imagePath, msg);
                        else
                            log.Warn(msg);
                    }
                }
                if (unmountError != null)
                    throw unmountError;
                if (sampleError != null)
                    throw sampleError;
                log.Info($"verified {imagePath}: DISM image info OK, {sampled} mount samples match");
            }
            finally
            {
                TryDeleteDir(mountDir);
            }
        }

        // §12.11 index name: gen-<n>-<timestamp>. The CLI passes its gen
        // counter + now; AppendWim forms the name (it never takes a free
        // name — one shape, no ambiguity across indexes).
        public static string BuildAppendImageName(int gen, DateTime now)
        {
            if (gen < 0) throw new ArgumentOutOfRangeException(nameof(gen));
            return "gen-" + gen + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        // Count "Index : N" lines in DISM /Get-WimInfo stdout.
        public static int CountImageIndexes(string getWimInfoStdout)
        {
            ArgumentNullException.ThrowIfNull(getWimInfoStdout);
            var count = 0;
            foreach (var line in getWimInfoStdout.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Index :", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        // Solid heuristic on /Get-WimInfo stdout ("solid" anywhere,
        // case-insensitive). Corpus policy (§12.11): the on-box probe
        // LANDED 2026-10-17 (DISM handles solid images itself).
        public static bool IsSolidImageInfo(string getWimInfoStdout)
        {
            ArgumentNullException.ThrowIfNull(getWimInfoStdout);
            return getWimInfoStdout.Contains("solid", StringComparison.OrdinalIgnoreCase);
        }

        // §12.11 append: a new snapshot as a new image index in an EXISTING
        // plain (non-solid) WIM via DISM /Append-Image from our VSS snapshot
        // path with the 12.1 flags (/CheckIntegrity + same WimScript.ini
        // exclusions; no /Compress — /Append-Image inherits the WIM's
        // compression, /Compress is capture-only) and 12.11 index naming.
        // Returns the base path (now holding one more index). Verify is
        // count+1 then apply + SHA-sample of the new index. Base must exist;
        // ESD/solid refuses loud (Corpus policy, locked §12.11 — see
        // SingleFileAppendRefusedException).
        public static string AppendWim(string snapshotPath, string baseWim, int gen, IReadOnlyList<string>? exclusions, IDismRunner dism, ILogger log, DateTime? now = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(baseWim);
            ArgumentNullException.ThrowIfNull(dism);
            ArgumentNullException.ThrowIfNull(log);
            if (gen < 0) throw new ArgumentOutOfRangeException(nameof(gen));
            if (!SnapshotDirLive(snapshotPath))
                throw new SingleFileSourceNotFoundException(snapshotPath);
            if (!File.Exists(baseWim))
                throw new SingleFileBaseNotFoundException(baseWim);
            if (baseWim.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
                throw new SingleFileAppendRefusedException(baseWim, "ESD is solid-compressed; --append is plain-WIM only by Corpus policy (on-box probe 2026-10-17: DISM itself appends and splits ESD fine — the refusal is our locked §12.11 default, not a DISM limit)");
            var before = RunGetIndexCount(dism, baseWim);
            var name = BuildAppendImageName(gen, now ?? DateTime.Now);
            var ini = WriteTempIni(CapturePolicy.EffectiveSkip(exclusions));
            try
            {
                RunDism(dism, baseWim, $"/Append-Image /ImageFile:{ProcessRunner.QuoteArg(baseWim)} /CaptureDir:{ProcessRunner.QuoteArg(snapshotPath)} /Name:{ProcessRunner.QuoteArg(name)} /CheckIntegrity /ConfigFile:{ProcessRunner.QuoteArg(ini)}");
                VerifyAppendedIndex(baseWim, snapshotPath, before + 1, dism, log);
            }
            finally
            {
                TryDelete(ini);
            }
            log.Info($"appended {snapshotPath} as index {before + 1} ({name}) to {baseWim}");
            return baseWim;
        }

        // §12.11 verify of the appended index: /Get-WimInfo count must equal
        // the expected new index, then mount that index read-only + SHA
        // sample against the capture source (same sample/unmount discipline
        // as VerifyWim, with the index pinned instead of 1).
        public static void VerifyAppendedIndex(string imagePath, string sourceDir, int expectedIndex, IDismRunner dism, ILogger log)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
            ArgumentNullException.ThrowIfNull(dism);
            ArgumentNullException.ThrowIfNull(log);
            if (expectedIndex <= 0) throw new ArgumentOutOfRangeException(nameof(expectedIndex));
            var count = RunGetIndexCount(dism, imagePath);
            if (count != expectedIndex)
                throw new SingleFileVerifyException(imagePath, $"image index count {count}, want {expectedIndex} — append did not land a new index");
            var mountDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corpus-wim-append-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mountDir);
            try
            {
                RunDism(dism, imagePath, $"/Mount-Image /ImageFile:{ProcessRunner.QuoteArg(imagePath)} /Index:{expectedIndex} /MountDir:{ProcessRunner.QuoteArg(mountDir)} /ReadOnly");
                Exception? sampleError = null;
                Exception? unmountError = null;
                var sampled = 0;
                try
                {
                    var (n, problems) = WimSample.Compare(mountDir, sourceDir, MaxWimSamples);
                    sampled = n;
                    if (problems.Count > 0)
                        sampleError = new SingleFileVerifyException(imagePath, "mount samples mismatch: " + string.Join("; ", problems.Take(5)));
                }
                finally
                {
                    var u = dism.Run($"/Unmount-Image /MountDir:{ProcessRunner.QuoteArg(mountDir)} /Discard");
                    if (u.ExitCode != 0)
                    {
                        var msg = $"unmount of {mountDir} failed (exit {u.ExitCode}): {u.Stderr} — clean up with DISM /Unmount-Image /Discard";
                        if (sampleError == null)
                            unmountError = new SingleFileVerifyException(imagePath, msg);
                        else
                            log.Warn(msg);
                    }
                }
                if (unmountError != null)
                    throw unmountError;
                if (sampleError != null)
                    throw sampleError;
                log.Info($"verified {imagePath} index {expectedIndex}: DISM image info OK, {sampled} mount samples match");
            }
            finally
            {
                TryDeleteDir(mountDir);
            }
        }

        // One /Get-WimInfo round trip: solid refuses loud (locked default),
        // else the index count. Shared by the pre-append gate and verify.
        private static int RunGetIndexCount(IDismRunner dism, string imagePath)
        {
            var r = dism.Run($"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(imagePath)}");
            if (r.ExitCode != 0)
                throw new SingleFileDismException(imagePath, $"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(imagePath)}", r);
            if (IsSolidImageInfo(r.Stdout))
                throw new SingleFileAppendRefusedException(imagePath, "solid-compressed image; --append is plain-WIM only by Corpus policy (on-box probe 2026-10-17: DISM itself handles solid images — the refusal is our locked §12.11 default, not a DISM limit)");
            return CountImageIndexes(r.Stdout);
        }

        // --zstd cold-archive sidecar (--to-vhdx only; GuardZstd refuses the
        // rest): streaming multi-frame compress AFTER the VHDX verify
        // greened, then the original is deleted and `.zst` appended.
        // ZstdSharp 0.7.2 has no parameterised streaming (ZstdStream takes
        // only a level), so each bounded frame is compressed with a
        // windowLog-27 + LDM context and concatenated — stock `zstd -d`
        // reads multi-frame streams transparently. NbWorkers is NOT set:
        // the port throws "Unsupported parameter" for workers > 0
        // (probed); frame parallelism is a later optimization, not here.
        public static string CompressSidecar(string imagePath, ILogger log, int level = ZstdLevel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(imagePath))
                throw new SingleFileSourceNotFoundException(imagePath);
            var zst = imagePath + ".zst";
            if (File.Exists(zst) || Directory.Exists(zst))
                throw new SingleFileTargetExistsException(zst);
            var length = new FileInfo(imagePath).Length;
            if (length == 0)
                throw new SingleFileBackupException($"refusing to compress empty image {imagePath}");
            var partial = zst + ".partial";
            var frames = new List<(long ZLen, long PLen)>();
            byte[] hash;
            using (var ctx = new ZstdCompressionContext())
            {
                ctx.SetParameter(ZstdCompressionParameter.WindowLog, ZstdWindowLog);
                ctx.SetParameter(ZstdCompressionParameter.EnableLongDistanceMatching, 1);
                ctx.SetParameter(ZstdCompressionParameter.ChecksumFlag, 0);
                using var input = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var sha = SHA256.Create();
                var buf = new byte[ZstdFrameBytes];
                while (true)
                {
                    var filled = 0;
                    int n;
                    while (filled < buf.Length && (n = input.Read(buf, filled, buf.Length - filled)) > 0)
                        filled += n;
                    if (filled == 0)
                        break;
                    sha.TransformBlock(buf, 0, filled, null, 0);
                    var frame = ctx.Compress(buf, 0, filled, level);
                    output.Write(frame, 0, frame.Length);
                    frames.Add((frame.Length, filled));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = sha.Hash!;
                output.Flush(true);
            }
            // Deletion guard: decode every frame back and compare the hash
            // BEFORE the only verified copy is removed.
            VerifySidecarFrames(partial, frames, hash);
            File.Move(partial, zst);
            File.Delete(imagePath);
            log.Info($"compressed {imagePath} ({length} bytes) to {zst}");
            return zst;
        }

        private static void VerifySidecarFrames(string partial, List<(long ZLen, long PLen)> frames, byte[] expectHash)
        {
            using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            foreach (var (zlen, plen) in frames)
            {
                var zbuf = new byte[zlen];
                var filled = 0;
                int n;
                while (filled < zbuf.Length && (n = input.Read(zbuf, filled, zbuf.Length - filled)) > 0)
                    filled += n;
                if (filled != zbuf.Length)
                    throw new SingleFileVerifyException(partial, "sidecar truncated mid-frame — original kept");
                using var ms = new MemoryStream(zbuf, writable: false);
                using var z = new ZstdStream(ms, ZstdStreamMode.Decompress, leaveOpen: false);
                using var plain = new MemoryStream((int)Math.Min(plen, int.MaxValue));
                z.CopyTo(plain);
                if (plain.Length != plen)
                    throw new SingleFileVerifyException(partial, $"sidecar frame decoded to {plain.Length}, want {plen} — original kept");
                sha.TransformBlock(plain.GetBuffer(), 0, (int)plain.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (!CryptographicOperations.FixedTimeEquals(sha.Hash!, expectHash))
                throw new SingleFileVerifyException(partial, "sidecar decode hash mismatch — original kept");
        }

        // LCN exclusion runs to sorted merged byte ranges (same skip
        // semantics as CapturePolicy.ZeroExcludedRanges); null when empty.
        private static List<(long Start, long Length)>? ExcludedByteRanges(IReadOnlyList<(long StartLcn, long ClusterCount)>? runs, long bytesPerCluster)
        {
            if (runs == null || runs.Count == 0 || bytesPerCluster <= 0)
                return null;
            var ranges = new List<(long Start, long Length)>();
            foreach (var (lcn, count) in runs)
            {
                if (lcn < 0 || count <= 0)
                    continue;
                ranges.Add((lcn * bytesPerCluster, count * bytesPerCluster));
            }
            if (ranges.Count == 0)
                return null;
            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(long Start, long Length)> { ranges[0] };
            foreach (var r in ranges.Skip(1))
            {
                var last = merged[^1];
                if (r.Start <= last.Start + last.Length)
                    merged[^1] = (last.Start, Math.Max(last.Length, r.Start + r.Length - last.Start));
                else
                    merged.Add(r);
            }
            return merged;
        }

        private static void CompareRange(Stream src, Stream content, long start, long length, byte[] bufA, byte[] bufB, string vhdxPath, IReadOnlyList<(long Start, long Length)>? excl = null)
        {
            src.Seek(start, SeekOrigin.Begin);
            content.Seek(start, SeekOrigin.Begin);
            var remaining = length;
            var pos = start;
            var e = 0;
            while (remaining > 0)
            {
                var want = (int)Math.Min(bufA.Length, remaining);
                var na = ReadExactly(src, bufA, want);
                var nb = ReadExactly(content, bufB, want);
                if (na != want || nb != want)
                    throw new SingleFileVerifyException(vhdxPath, $"short read at offset {pos} (source {na}, image {nb}, want {want})");
                for (var i = 0; i < want; i++, pos++)
                {
                    while (e < (excl?.Count ?? 0) && pos >= excl![e].Start + excl[e].Length)
                        e++;
                    if (excl != null && e < excl.Count && pos >= excl[e].Start)
                    {
                        if (bufB[i] != 0)
                            throw new SingleFileVerifyException(vhdxPath, $"excluded byte at offset {pos} reads 0x{bufB[i]:X2} (want 0x00)");
                    }
                    else if (bufA[i] != bufB[i])
                        throw new SingleFileVerifyException(vhdxPath, $"byte mismatch at offset {pos} (source 0x{bufA[i]:X2}, image 0x{bufB[i]:X2})");
                }
                remaining -= want;
            }
        }

        private static void AssertZeroRange(Stream content, long start, long length, byte[] buf, string vhdxPath)
        {
            content.Seek(start, SeekOrigin.Begin);
            var remaining = length;
            var pos = start;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buf.Length, remaining);
                var n = ReadExactly(content, buf, want);
                if (n != want)
                    throw new SingleFileVerifyException(vhdxPath, $"short read at offset {pos} ({n}, want {want})");
                for (var i = 0; i < want; i++)
                {
                    if (buf[i] != 0)
                        throw new SingleFileVerifyException(vhdxPath, $"skipped range [{start},{start + length}) reads non-zero at offset {pos + i}");
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

        private static bool IsAllZero(byte[] data)
        {
            foreach (var b in data)
            {
                if (b != 0) return false;
            }
            return true;
        }

        private static void RunDism(IDismRunner dism, string imagePath, string arguments)
        {
            var r = dism.Run(arguments);
            if (r.ExitCode != 0)
                throw new SingleFileDismException(imagePath, arguments, r);
        }

        private static string WriteTempIni(IReadOnlyList<string> exclusions)
        {
            var ini = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corpus-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(ini, WimScript.BuildIni(exclusions), Encoding.ASCII);
            return ini;
        }

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

        private static void TryDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
