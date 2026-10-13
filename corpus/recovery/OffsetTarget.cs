using System;
using System.IO;

namespace Corpus
{
    // Whole-disk restore window (disk-spec §6): a per-extent view over a
    // shared target. The offset-0 path is the BaseOffset==0 case, so the
    // single-gen --restore flow (plain FileBlockTarget/RawDiskTarget, never
    // an IPartitionTarget) is untouched.
    public interface IPartitionTarget : IBlockTarget
    {
        long BaseOffset { get; }
    }

    public sealed class OffsetBlockTarget : IPartitionTarget
    {
        private readonly IBlockTarget _inner;
        private bool _disposed;

        public OffsetBlockTarget(IBlockTarget inner, long baseOffset)
        {
            ArgumentNullException.ThrowIfNull(inner);
            if (baseOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(baseOffset), "extent offset must be non-negative");
            _inner = inner;
            BaseOffset = baseOffset;
        }

        public long BaseOffset { get; }

        internal IBlockTarget Inner => _inner;

        public string DisplayName => _inner.DisplayName;

        public long Capacity => _inner.Capacity - BaseOffset;

        // The inner stream pre-seeked to the extent start. File targets open
        // without truncation (the disk file is grown once up front; a
        // per-extent Create would wipe sibling extents); raw targets have no
        // truncation concept so the inner open is used as-is.
        public Stream OpenWrite()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Stream s = _inner is FileBlockTarget file && File.Exists(file.Path)
                ? new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.Read)
                : _inner.OpenWrite();
            s.Seek(BaseOffset, SeekOrigin.Begin);
            return s;
        }

        // Window only — the inner lifetime belongs to the caller (one shared
        // target spans every extent of a disk restore).
        public void Dispose()
        {
            _disposed = true;
        }
    }
}
