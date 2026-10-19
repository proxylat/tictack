using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

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

        // Content-defined chunking (BuzHash rolling hash, kopia-DYNAMIC
        // style): boundaries fall where the masked 32-byte window hash is
        // zero past minBytes, forced at maxBytes. Mean lands within ~2x of
        // avgBytes (min plus the largest power of two <= avg - min, modulo
        // the usual data-dependent wobble). Chunk
        // identity stays content-hash based, so dedup/store/verify paths
        // are untouched — only boundaries move. Off by default: Read stays
        // the fixed path, byte-identical.
        public static (int Min, int Max) BuzHashBounds(int avgBytes)
        {
            if (avgBytes <= 0) throw new ArgumentOutOfRangeException(nameof(avgBytes));
            var min = Math.Max(avgBytes / 4, 64);
            var max = (int)Math.Min(4L * avgBytes, 64L * 1024 * 1024);
            if (max < avgBytes) max = avgBytes;
            if (min > max) min = max;
            return (min, max);
        }

        public static IEnumerable<Chunk> ReadVariable(Stream stream, int avgBytes, int minBytes, int maxBytes)
        {
            if (avgBytes <= 0) throw new ArgumentOutOfRangeException(nameof(avgBytes));
            if (minBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minBytes));
            if (maxBytes < minBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (avgBytes < minBytes || avgBytes > maxBytes) throw new ArgumentOutOfRangeException(nameof(avgBytes));
            ArgumentNullException.ThrowIfNull(stream);
            return ReadVariableCore(stream, MaskFor(avgBytes - minBytes), minBytes, maxBytes);
        }

        private const int BuzWindow = 32;

        // Fixed SplitMix64 seed: boundaries are deterministic across
        // runs, hosts, and architectures (pure integer math, no floats).
        private static readonly uint[] BuzTable = BuildBuzTable();

        private static uint[] BuildBuzTable()
        {
            var t = new uint[256];
            var s = 0x9E3779B97F4A7C15ul;
            for (var i = 0; i < 256; i++)
            {
                s += 0x9E3779B97F4A7C15ul;
                var z = s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
                t[i] = (uint)((z ^ (z >> 31)) >> 32);
            }
            return t;
        }

        private static uint MaskFor(int span)
        {
            if (span < 2) return 0;
            var bits = 31 - BitOperations.LeadingZeroCount((uint)span);
            return ((1u << bits) - 1u);
        }

        private static IEnumerable<Chunk> ReadVariableCore(Stream stream, uint mask, int minBytes, int maxBytes)
        {
            var window = new byte[BuzWindow];
            var rpos = 0;
            uint h = 0;
            long hashed = 0;
            var chunk = new byte[maxBytes];
            var len = 0;
            long offset = 0;
            long index = 0;
            var tmp = new byte[128 * 1024];
            int n;
            while ((n = stream.Read(tmp, 0, tmp.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var b = tmp[i];
                    if (hashed >= BuzWindow)
                        h = BitOperations.RotateLeft(h, 1) ^ BuzTable[window[rpos]] ^ BuzTable[b];
                    else
                        h = BitOperations.RotateLeft(h, 1) ^ BuzTable[b];
                    window[rpos] = b;
                    rpos = (rpos + 1) & (BuzWindow - 1);
                    hashed++;
                    chunk[len++] = b;
                    if (len < minBytes || ((h & mask) != 0 && len < maxBytes))
                        continue;
                    var data = new byte[len];
                    Buffer.BlockCopy(chunk, 0, data, 0, len);
                    yield return new Chunk(index, offset, data);
                    index++;
                    offset += len;
                    len = 0;
                }
            }
            if (len > 0)
            {
                var data = new byte[len];
                Buffer.BlockCopy(chunk, 0, data, 0, len);
                yield return new Chunk(index, offset, data);
            }
        }
    }
}
