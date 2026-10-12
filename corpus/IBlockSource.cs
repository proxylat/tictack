using System;
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

    public sealed class FileBlockSource : IBlockSource
    {
        private readonly string _path;
        private bool _disposed;

        public FileBlockSource(string path)
        {
            _path = path;
        }

        public long Length => new FileInfo(_path).Length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
