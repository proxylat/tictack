using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZstdSharp;
using ZstdSharp.Unsafe;

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
        // Level is set per call (the store level varies by config/tests).
        private static readonly ThreadLocal<Compressor> _encoders = new(() =>
        {
            var ctx = new Compressor(3);
            ctx.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
            return ctx;
        });

        public static (byte[] Payload, byte Codec) Encode(byte[] plain, int level)
        {
            var enc = _encoders.Value!;
            enc.Level = level;
            var bound = Compressor.GetCompressBound(plain.Length);
            var buf = ArrayPool<byte>.Shared.Rent(bound);
            try
            {
                // Bound-sized: TryWrap cannot fail; a false is treated as
                // incompressible (raw passthrough, same as before).
                if (!enc.TryWrap(plain, buf, 0, out var written) || written >= plain.Length)
                    return (plain, CodecRaw);
                var outBuf = new byte[written];
                Buffer.BlockCopy(buf, 0, outBuf, 0, written);
                return (outBuf, CodecZstd);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
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
                using var zstd = new DecompressionStream(input, 128 * 1024, checkEndOfStream: true, leaveOpen: false);
                using var output = new MemoryStream(payload.Length);
                zstd.CopyTo(output);
                return output.ToArray();
            }
            throw new InvalidDataException($"Unknown codec: {codec}");
        }
    }
}
