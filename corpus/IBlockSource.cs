using System;
using System.Collections.Generic;
using System.IO;

namespace Corpus
{
    // Block-source seam: the imaging loop reads volumes through this, so the
    // VSS/bitmap reader (Windows-only) plugs in later without touching the
    // chunk→pack→index→manifest core. FileBlockSource is the Linux-provable
    // stand-in: a seeded .img file plays the volume.
    public interface IBlockSource : IDisposable
    {
        long Length { get; }
        Stream OpenRead();
    }

    public interface ISparseBlockSource : IBlockSource
    {
        long BytesPerCluster { get; }
        IReadOnlyList<(long Start, long Count)> GetAllocatedRuns();
    }

    // Signals a clean capture completion to VSS-backed sources even when
    // sparse holes or incremental seeks skip the tail of the volume.
    public interface ICompletableBlockSource : IBlockSource
    {
        void MarkCompleted();
    }

    public sealed class FileBlockSource : IBlockSource
    {
        private readonly string _path;
        private readonly bool _useUnbuffered;
        private bool _disposed;

        public FileBlockSource(string path)
            : this(path, true)
        {
        }

        // §12.12 seam: --buffered-io passes false (default ON).
        public FileBlockSource(string path, bool useUnbuffered)
        {
            _path = path;
            _useUnbuffered = useUnbuffered;
        }

        public long Length => new FileInfo(_path).Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ExtentStreams.OpenExtentRead(_path, _useUnbuffered);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
