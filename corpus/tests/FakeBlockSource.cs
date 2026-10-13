namespace Corpus;

// Read-counting block source: wraps an inner source (file or sparse),
// records which chunk-grid indexes the imaging loop actually reads.
// Proves incrementals skip clean chunks. Always ISparseBlockSource:
// dense inners report one fully-allocated run (hole map all false,
// behavior-identical to dense); sparse inners delegate their runs.
internal sealed class CountingBlockSource : ISparseBlockSource
{
    private readonly IBlockSource _inner;
    private readonly int _chunkBytes;
    private bool _disposed;

    public HashSet<long> ReadChunks { get; } = new();

    public CountingBlockSource(string path, int chunkBytes)
        : this(new FileBlockSource(path), chunkBytes)
    {
    }

    public CountingBlockSource(IBlockSource inner, int chunkBytes)
    {
        _inner = inner;
        _chunkBytes = chunkBytes;
    }

    public long Length => _inner.Length;

    public long BytesPerCluster =>
        _inner is ISparseBlockSource s ? s.BytesPerCluster : 4096;

    public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns()
    {
        if (_inner is ISparseBlockSource s)
            return s.GetAllocatedRuns();
        var clusters = (Length + BytesPerCluster - 1) / BytesPerCluster;
        return new (long Start, long Count)[] { (0, clusters) };
    }

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CountingStream(_inner.OpenRead(), _chunkBytes, ReadChunks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _chunkBytes;
        private readonly HashSet<long> _read;

        public CountingStream(Stream inner, int chunkBytes, HashSet<long> read)
        {
            _inner = inner;
            _chunkBytes = chunkBytes;
            _read = read;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var pos = _inner.Position;
            var n = _inner.Read(buffer, offset, count);
            Mark(pos, n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var pos = _inner.Position;
            var n = _inner.Read(buffer);
            Mark(pos, n);
            return n;
        }

        // Only bytes actually returned count (a zero read at EOF must not
        // phantom-mark the past-the-end index); every overlapped chunk is
        // marked, including partial first/last chunks of unaligned reads.
        private void Mark(long pos, int n)
        {
            if (n <= 0) return;
            for (var c = pos / _chunkBytes; c <= (pos + n - 1) / _chunkBytes; c++)
                _read.Add(c);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
