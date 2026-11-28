using System;
using System.IO;

namespace Corpus
{
    // Pure-C# MS-XCA §2.5 LZNT1 decompressor for NTFS-compressed files (0x0800 flag).
    internal static class Lznt1
    {
        internal static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            var s = 0;
            var d = 0;
            while (s + 2 <= src.Length && d < dst.Length)
            {
                var header = (ushort)(src[s] | (src[s + 1] << 8));
                s += 2;
                if (header == 0) break;
                var chunkLen = (header & 0x0FFF) + 1;
                if (s + chunkLen > src.Length)
                    throw new InvalidDataException("LZNT1 chunk truncated");
                if ((header & 0x8000) == 0)
                {
                    var copy = Math.Min(chunkLen, dst.Length - d);
                    src.Slice(s, copy).CopyTo(dst.Slice(d));
                    s += chunkLen;
                    d += copy;
                    continue;
                }
                d = DecompressChunk(src.Slice(s, chunkLen), dst, d);
                s += chunkLen;
            }
            return d;
        }

        private static int DecompressChunk(ReadOnlySpan<byte> chunk, Span<byte> dst, int d)
        {
            var chunkStart = d;
            var s = 0;
            var split = 12;
            var nextThreshold = 16;
            while (s < chunk.Length && d < dst.Length)
            {
                var flags = chunk[s++];
                for (var bit = 0; bit < 8 && s < chunk.Length && d < dst.Length; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        dst[d++] = chunk[s++];
                        continue;
                    }
                    if (s + 2 > chunk.Length) throw new InvalidDataException("LZNT1 token truncated");
                    while (d - chunkStart > nextThreshold && split > 4) { split--; nextThreshold <<= 1; }
                    var token = (ushort)(chunk[s] | (chunk[s + 1] << 8));
                    s += 2;
                    var offset = (token >> split) + 1;
                    var len = (token & ((1 << split) - 1)) + 3;
                    if (offset <= 0 || offset > d - chunkStart) throw new InvalidDataException("LZNT1 invalid back-reference");
                    for (var k = 0; k < len && d < dst.Length; k++) { dst[d] = dst[d - offset]; d++; }
                }
            }
            return d;
        }
    }
}
