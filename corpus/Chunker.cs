using System;
using System.Collections.Generic;
using System.IO;

namespace Corpus
{
    // One fixed-size slice of a volume stream. Data is exactly ChunkBytes
    // except the tail chunk of a volume.
    public sealed class Chunk
    {
        public long Index { get; }
        public long Offset { get; }
        public byte[] Data { get; }
        public bool IsHole { get; }

        public Chunk(long index, long offset, byte[] data) : this(index, offset, data, false) { }

        public Chunk(long index, long offset, byte[] data, bool isHole)
        {
            Index = index;
            Offset = offset;
            Data = data;
            IsHole = isHole;
        }
    }

    // Fixed-size streaming chunker (§6: fixed 4 MiB for volume streams —
    // guests allocate contiguously, so CDC buys nothing until shift-churn
    // measurements say otherwise). Yields chunk by chunk; the loop never
    // holds more than one chunk plus one pack buffer.
    public static class Chunker
    {
        public static IEnumerable<Chunk> Read(Stream stream, int chunkBytes)
        {
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            var buf = new byte[chunkBytes];
            long index = 0;
            while (true)
            {
                var filled = 0;
                int n;
                while (filled < chunkBytes && (n = stream.Read(buf, filled, chunkBytes - filled)) > 0)
                    filled += n;
                if (filled == 0) yield break;
                var data = new byte[filled];
                Buffer.BlockCopy(buf, 0, data, 0, filled);
                yield return new Chunk(index, index * (long)chunkBytes, data);
                index++;
            }
        }

        // Sparse variant: hole chunks are seeked past (no read) and yielded
        // with empty data + IsHole; data chunks fill exactly like Read.
        public static IEnumerable<Chunk> ReadSparse(Stream stream, int chunkBytes, Func<long, bool> isHole)
        {
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            ArgumentNullException.ThrowIfNull(isHole);
            if (!stream.CanSeek) throw new NotSupportedException();
            var buf = new byte[chunkBytes];
            long index = 0;
            while (true)
            {
                if (isHole(index))
                {
                    var skip = Math.Min((long)chunkBytes, stream.Length - stream.Position);
                    if (skip <= 0) yield break;
                    stream.Seek(skip, SeekOrigin.Current);
                    yield return new Chunk(index, index * (long)chunkBytes, Array.Empty<byte>(), true);
                    index++;
                    continue;
                }
                var filled = 0;
                int n;
                while (filled < chunkBytes && (n = stream.Read(buf, filled, chunkBytes - filled)) > 0)
                    filled += n;
                if (filled == 0) yield break;
                var data = new byte[filled];
                Buffer.BlockCopy(buf, 0, data, 0, filled);
                yield return new Chunk(index, index * (long)chunkBytes, data, false);
                index++;
            }
        }

        // Chunk grid hole map: chunk i is a hole iff zero allocated
        // clusters intersect it. Two-pointer, O(chunks + runs).
        public static bool[] BuildHoleMap(
            IReadOnlyList<(long Start, long Count)> runs,
            long bytesPerCluster,
            long volumeLength,
            int chunkBytes)
        {
            ArgumentNullException.ThrowIfNull(runs);
            if (bytesPerCluster <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerCluster));
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            var n = (int)((volumeLength + chunkBytes - 1) / chunkBytes);
            var map = new bool[n];
            var r = 0;
            for (var i = 0; i < n; i++)
            {
                var cs = i * (long)chunkBytes;
                var ce = Math.Min((i + 1) * (long)chunkBytes, volumeLength);
                while (r < runs.Count && (runs[r].Start + runs[r].Count) * bytesPerCluster <= cs) r++;
                if (r == runs.Count)
                {
                    Array.Fill(map, true, i, n - i);
                    break;
                }
                var rs = runs[r].Start * bytesPerCluster;
                var re = (runs[r].Start + runs[r].Count) * bytesPerCluster;
                map[i] = re <= cs || rs >= ce;
            }
            return map;
        }
    }
}
