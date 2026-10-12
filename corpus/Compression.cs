using System.IO;
using ZstdSharp;

namespace Corpus
{
    // Per-chunk codec framing: zstd when it shrinks, raw passthrough when it
    // doesn't (incompressible chunks must not grow from framing overhead).
    public static class Compression
    {
        public const byte CodecRaw = 0;
        public const byte CodecZstd = 1;

        public static (byte[] Payload, byte Codec) Encode(byte[] plain, int level)
        {
            // Static one-shot: 0.7.2 has no Compressor/Decompressor types.
            var compressed = Zstd.Compress(plain, level);
            if (compressed.Length >= plain.Length)
                return (plain, CodecRaw);
            return (compressed, CodecZstd);
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
