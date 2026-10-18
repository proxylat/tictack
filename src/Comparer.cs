using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace TicTack
{
    // Single SHA-256 implementation for comparison and validation: same
    // accessor seam, same lowercase hex format.
    internal static class FileHasher
    {
        // One chunk size for every hash/copy loop (the old CopyToAsync
        // default, so throughput is unchanged). Rented, never newed (Fix 2).
        internal const int BufferSize = 81920;

        internal static string ComputeHex(string path, IFileAccessor? accessor)
        {
            path = PathUtil.EnsureExtended(path);
            using (var stream = accessor != null ? accessor.OpenRead(path) : File.OpenRead(path))
                return ComputeHex(stream);
        }

        // Pooled incremental hash over an open stream: one pass, no
        // per-call buffer, same digest as SHA256.HashData (pinned by test).
        internal static string ComputeHex(Stream stream)
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
                    sha256.AppendData(buffer, 0, read);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return Convert.ToHexStringLower(sha256.GetHashAndReset());
        }

        // Sync copy that hashes the transferred bytes in the same pass, so
        // callers validate with one re-read instead of two (Fix 1). Returns
        // the lowercase hex SHA-256 of the bytes written.
        internal static string CopyAndHash(Stream source, Stream destination)
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, BufferSize)) > 0)
                {
                    sha256.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return Convert.ToHexStringLower(sha256.GetHashAndReset());
        }

        // Length pre-check + full-content compare, for the archive/recovery
        // paths that used to reimplement SHA-256 (and uppercase hex) locally.
        internal static bool SameContent(string left, string right)
        {
            try
            {
                var leftInfo = new FileInfo(PathUtil.EnsureExtended(left));
                var rightInfo = new FileInfo(PathUtil.EnsureExtended(right));
                if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length) return false;
                return ComputeHex(left, null) == ComputeHex(right, null);
            }
            catch { return false; }
        }
    }

    public static class ComparerFactory
    {
        public static IFileComparer Create(VerificationLevel level, IFileAccessor? accessor = null)
        {
            switch (level)
            {
                case VerificationLevel.Size: return new SizeComparer();
                case VerificationLevel.DateAndSize: return new DateSizeComparer();
                case VerificationLevel.Hash: return new HashComparer(accessor);
                case VerificationLevel.Full: return new FullComparer(accessor);
                default: throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown verification level");
            }
        }
    }

    public class SizeComparer : IFileComparer
    {
        // Metadata-only: never reads file contents.
        public bool RequiresContentRead => false;

        public bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return false;
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return dInfo.Exists && source.Value.Length == dInfo.Length;
            }
            catch { return false; }
        }
    }

    public class DateSizeComparer : IFileComparer
    {
        // Metadata-only: never reads file contents.
        public bool RequiresContentRead => false;

        public bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return false;
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return dInfo.Exists
                    && source.Value.Length == dInfo.Length
                    && source.Value.LastWriteTimeUtcTicks == dInfo.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }
        }
    }

    public class HashComparer : IFileComparer
    {
        private readonly IFileAccessor? _accessor;
        public HashComparer(IFileAccessor? accessor = null) { _accessor = accessor; }

        // Single-file hash for the copy-time validation fast path (hashes only this path).
        internal string HashFile(string path) => FileHasher.ComputeHex(path, _accessor);

        // Reads both files end to end when metadata matches.
        public bool RequiresContentRead => true;

        public bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return false;
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!dInfo.Exists) return false;
                if (source.Value.Length != dInfo.Length) return false;
                return FileHasher.ComputeHex(sourcePath, _accessor) == FileHasher.ComputeHex(destPath, _accessor);
            }
            catch { return false; }
        }
    }

    public class FullComparer : IFileComparer
    {
        private readonly HashComparer _hash;
        public FullComparer(IFileAccessor? accessor = null) { _hash = new HashComparer(accessor); }

        // Delegates to HashComparer when metadata matches.
        public bool RequiresContentRead => true;

        public bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null)
        {
            try
            {
                var source = sourceSnapshot;
                if (!source.HasValue)
                {
                    if (!FileSnapshot.TryRead(sourcePath, out var current)) return false;
                    source = current;
                }
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!dInfo.Exists) return false;
                if (source.Value.Length != dInfo.Length) return false;
                if (source.Value.LastWriteTimeUtcTicks != dInfo.LastWriteTimeUtc.Ticks) return false;
                return _hash.AreEqual(sourcePath, destPath, source);
            }
            catch { return false; }
        }
    }
}
