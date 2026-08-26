using System;
using System.IO;
using System.Security.Cryptography;

namespace TicTack
{
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
        public bool AreEqual(string sourcePath, string destPath)
        {
            try
            {
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return sInfo.Exists && dInfo.Exists && sInfo.Length == dInfo.Length;
            }
            catch { return false; }
        }
    }

    public class DateSizeComparer : IFileComparer
    {
        public bool AreEqual(string sourcePath, string destPath)
        {
            try
            {
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                return sInfo.Exists && dInfo.Exists
                    && sInfo.Length == dInfo.Length
                    && sInfo.LastWriteTimeUtc == dInfo.LastWriteTimeUtc;
            }
            catch { return false; }
        }
    }

    public class HashComparer : IFileComparer
    {
        private readonly IFileAccessor? _accessor;
        public HashComparer(IFileAccessor? accessor = null) { _accessor = accessor; }

        public bool AreEqual(string sourcePath, string destPath)
        {
            try
            {
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!sInfo.Exists || !dInfo.Exists) return false;
                if (sInfo.Length != dInfo.Length) return false;
                return ComputeHash(sourcePath) == ComputeHash(destPath);
            }
            catch { return false; }
        }

        private string ComputeHash(string path)
        {
            path = PathUtil.EnsureExtended(path);
            using (var sha256 = SHA256.Create())
            using (var stream = _accessor != null ? _accessor.OpenRead(path) : File.OpenRead(path))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    public class FullComparer : IFileComparer
    {
        private readonly IFileAccessor? _accessor;
        public FullComparer(IFileAccessor? accessor = null) { _accessor = accessor; }

        public bool AreEqual(string sourcePath, string destPath)
        {
            try
            {
                var sInfo = new FileInfo(PathUtil.EnsureExtended(sourcePath));
                var dInfo = new FileInfo(PathUtil.EnsureExtended(destPath));
                if (!sInfo.Exists || !dInfo.Exists) return false;
                if (sInfo.Length != dInfo.Length) return false;
                if (sInfo.LastWriteTimeUtc != dInfo.LastWriteTimeUtc) return false;
                return new HashComparer(_accessor).AreEqual(sourcePath, destPath);
            }
            catch { return false; }
        }
    }
}
