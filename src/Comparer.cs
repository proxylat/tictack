using System;
using System.IO;
using System.Security.Cryptography;

namespace TicTack
{
    // Single SHA-256 implementation for comparison and validation: same
    // accessor seam, same lowercase hex format.
    internal static class FileHasher
    {
        internal static string ComputeHex(string path, IFileAccessor? accessor)
        {
            path = PathUtil.EnsureExtended(path);
            using (var sha256 = SHA256.Create())
            using (var stream = accessor != null ? accessor.OpenRead(path) : File.OpenRead(path))
            {
                return Convert.ToHexStringLower(sha256.ComputeHash(stream));
            }
        }
    }

    public static class ComparerFactory
    {
        public static IFileComparer Create(VerificationLevel level, IFileAccessor? accessor = null)
        {
            switch (level)
            {
                case VerificationLevel.Size: return new SizeComparer();
                case VerificationLevel.Hash: return new HashComparer(accessor);
                case VerificationLevel.Full: return new FullComparer(accessor);
                default: return new DateSizeComparer();
            }
        }
    }

    public class SizeComparer : IFileComparer
    {
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
