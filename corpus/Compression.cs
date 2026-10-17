using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZstdSharp;

namespace Corpus
{
    // Per-chunk codec framing: zstd when it shrinks, raw passthrough when it
    // doesn't (incompressible chunks must not grow from framing overhead).
    // §12.4: pooled per-thread contexts (no fresh context per chunk);
    // frame checksums off (ChunkId already hashes the plaintext).
    public static class Compression
    {
        public const byte CodecRaw = 0;
        public const byte CodecZstd = 1;

        // Native contexts are NOT thread-safe — one per worker thread,
        // reused across chunks (no disposal point for a static pool; the
        // finalizer frees, and pool threads are sticky so reuse is high).
        private static readonly ThreadLocal<ZstdCompressionContext> _encoders = new(() =>
        {
            var ctx = new ZstdCompressionContext();
            ctx.SetParameter(ZstdCompressionParameter.ChecksumFlag, 0);
            return ctx;
        });

        public static (byte[] Payload, byte Codec) Encode(byte[] plain, int level)
        {
            var compressed = _encoders.Value!.Compress(plain, 0, plain.Length, level);
            if (compressed.Length >= plain.Length)
                return (plain, CodecRaw);
            return (compressed, CodecZstd);
        }

        // Order-preserving parallel encode over a bounded window: results[i]
        // is plains[i] encoded. The caller still pack-adds sequentially.
        public static (byte[] Payload, byte Codec)[] EncodeBatch(IReadOnlyList<byte[]> plains, int level)
        {
            var results = new (byte[] Payload, byte Codec)[plains.Count];
            if (plains.Count == 1)
            {
                // Incremental dirty-sets are often tiny — skip the Parallel
                // machinery when there is nothing to parallelize.
                results[0] = Encode(plains[0], level);
                return results;
            }
            Parallel.For(0, plains.Count, i => { results[i] = Encode(plains[i], level); });
            return results;
        }

        public static byte[] Decode(byte[] payload, byte codec)
        {
            if (codec == CodecRaw)
                return payload;
            if (codec == CodecZstd)
            {
                // Zstd.Decompress needs the uncompressed size up front, which
                // Decode isn't given — stream it so the payload stays a pure
                // zstd frame any tool can read.
                using var input = new MemoryStream(payload, writable: false);
                using var zstd = new ZstdStream(input, ZstdStreamMode.Decompress, leaveOpen: false);
                using var output = new MemoryStream(payload.Length);
                zstd.CopyTo(output);
                return output.ToArray();
            }
            throw new InvalidDataException($"Unknown codec: {codec}");
        }
    }
}
