using System;
using System.Buffers;
using System.IO;
using System.Threading;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Corpus
{
    // §12.7 production delta codec: zstd patch-from (ZstdSharp.Port 0.8.8).
    // The base chunk is loaded as the compression dictionary, so the patch
    // only stores what differs — a 1-byte edit on a 1MiB chunk compresses
    // to ~1KiB instead of a second full copy. Production CodecId is 2 (the
    // first delta id; DeltaCodec.cs reserves >= 2 for delta codecs).
    //
    // Recipe notes (spike-verified, kept verbatim):
    // - The four tuned params are set BEFORE LoadDictionary: the dictionary
    //   is baked with the live params, so setting them after is a no-op.
    //   windowLog 23 covers dicts up to 8MiB (ChunkMib-sized bases).
    // - Instance one-shots (Wrap/Unwrap) are used, never Unsafe one-shots
    //   (those can't hold params+dict).
    // - Compressors are NOT thread-safe: one instance per worker thread
    //   (ThreadLocal, mirroring Compression._encoders), never one per
    //   chunk — an LDM instance holds tens of MB, so parking one per
    //   chunk would grow with user data (scale rule).
    // - LoadDictionary runs per call (the dict IS the base chunk, which
    //   differs per chunk): ~50ms/MiB on the capture path, ~free on the
    //   read path next to the hash check. No cache: consecutive chunks
    //   never share a base, so a cache would only add bookkeeping.
    // - Buffers are rented (ArrayPool), never parked per chunk.
    public sealed class ZstdDeltaCodec : IDeltaCodec
    {
        public byte CodecId => 2;

        private readonly int _level;
        private readonly ThreadLocal<Compressor> _encoders;
        private readonly ThreadLocal<Decompressor> _decoders;

        public ZstdDeltaCodec(int level = 3)
        {
            _level = level;
            _encoders = new ThreadLocal<Compressor>(NewEncoder);
            _decoders = new ThreadLocal<Decompressor>(() => new Decompressor());
        }

        private Compressor NewEncoder()
        {
            var c = new Compressor(_level);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 23);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_hashLog, 23);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_chainLog, 22);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            return c;
        }

        // Best-effort: false when the patch is no smaller than the raw
        // bytes (Store compares against whole-compressed itself) and on
        // ANY error — a failed patch must fall back to whole, never fail
        // the image.
        public bool TryCreatePatch(byte[] baseWhole, byte[] current, out byte[] patch)
        {
            patch = Array.Empty<byte>();
            try
            {
                if (baseWhole.Length == 0 || current.Length == 0)
                    return false;
                var enc = _encoders.Value!;
                enc.LoadDictionary(baseWhole);
                var bound = Compressor.GetCompressBound(current.Length);
                var buf = ArrayPool<byte>.Shared.Rent(bound);
                try
                {
                    if (!enc.TryWrap(current, buf, 0, out var written) || written <= 0)
                        return false;
                    if (written >= current.Length)
                        return false;
                    patch = new byte[written];
                    Buffer.BlockCopy(buf, 0, patch, 0, written);
                    return true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            catch (Exception)
            {
                patch = Array.Empty<byte>();
                return false;
            }
        }

        // Reconstruction: throws InvalidDataException on corrupt input
        // (the DeltaChunkReader names the chunk). Frame checksums stay at
        // their default — content identity is hash-checked a layer up.
        public byte[] ApplyPatch(byte[] baseWhole, byte[] patch)
        {
            ArgumentNullException.ThrowIfNull(baseWhole);
            ArgumentNullException.ThrowIfNull(patch);
            if (patch.Length == 0)
                throw new InvalidDataException("empty delta patch");
            try
            {
                var dec = _decoders.Value!;
                // The streaming reader borrows this shared instance
                // (preserveDecompressor) and does not reset it on dispose:
                // a second frame on the same instance fails loud
                // ("operation not authorized") without an explicit reset.
                dec.ResetStream();
                dec.LoadDictionary(baseWhole);
                using var input = new MemoryStream(patch, writable: false);
                using var z = new DecompressionStream(input, dec, 128 * 1024, checkEndOfStream: true, preserveDecompressor: true, leaveOpen: false);
                using var output = new MemoryStream();
                z.CopyTo(output);
                if (output.Length == 0)
                    throw new InvalidDataException("delta patch decoded to empty");
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"corrupt delta patch ({ex.Message})", ex);
            }
        }
    }
}
