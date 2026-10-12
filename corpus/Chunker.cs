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

        public Chunk(long index, long offset, byte[] data)
        {
            Index = index;
            Offset = offset;
            Data = data;
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
    }
}
