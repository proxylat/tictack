using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Corpus
{
    // Pack file layout (all integers little-endian):
    //   V1 blobs: [storedLen u32][plainLen u32][codec u8][digest 32B][payload storedLen B] * N
    //   V2 blobs: [storedLen u32][plainLen u32][codec|0x80 u8][digest 32B][nonce 12B][payload storedLen B] * N
    //   index:   [digest 32B][offset u64][stored u32][plain u32][codec u8] * N
    //   trailer: [indexOffset u64][count u32][magic "CPK1"/"CPK2" 4B]
    // Blobs stream first so the writer never seeks; the trailing index lets a
    // reader locate any blob from its (pack, offset, stored) row. The magic
    // proves the seal finished — a crash mid-seal leaves a .tmp, never a pack.
    // Readers key off the per-blob 0x80 flag bit, NOT the trailer magic, so
    // any CPK1/CPK2 mix reads correctly; the magic is whole-file
    // identification for external tools.
    public sealed class PackWriter : IDisposable
    {
        public const long DefaultMaxPackBytes = 16L * 1024 * 1024;

        public const byte CodecEncryptedFlag = 0x80;

        internal const int BlobHeaderBytes = 41;

        internal const int BlobHeaderBytesV2 = 53;

        private readonly string _packsDir;
        private readonly long _maxPackBytes;
        private readonly bool _v2;
        private readonly List<(byte[] Digest, long Offset, uint Stored, uint Plain, byte Codec)> _index = new List<(byte[] Digest, long Offset, uint Stored, uint Plain, byte Codec)>();
        private FileStream? _stream;
        private string? _tmpPath;
        private string? _packName;
        private long _currentSize;
        private bool _disposed;

        public PackWriter(string packsDir, long maxPackBytes = DefaultMaxPackBytes, bool v2 = false)
        {
            ArgumentNullException.ThrowIfNull(packsDir);
            if (maxPackBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackBytes));
            _packsDir = packsDir;
            _maxPackBytes = maxPackBytes;
            _v2 = v2;
            Directory.CreateDirectory(_packsDir);
        }

        // Offset is the blob start. Rotates when the next blob would overflow
        // the pack; a lone oversized blob still fits (it starts a fresh pack).
        // v2 packs are all-encrypted: every Add needs a 12B nonce (random per
        // blob — no counter state to crash-lose and reuse). v1 + nonce throws.
        public (string PackName, long Offset) Add(byte[] digest32, byte[] payload, int plainSize, byte codec, byte[]? nonce12 = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(digest32);
            ArgumentNullException.ThrowIfNull(payload);
            if (digest32.Length != 32) throw new ArgumentException("digest must be 32 bytes", nameof(digest32));
            if (plainSize < 0) throw new ArgumentOutOfRangeException(nameof(plainSize));
            if (nonce12 != null && !_v2) throw new ArgumentException("nonce requires a v2 writer", nameof(nonce12));
            if (_v2 && (nonce12 == null || nonce12.Length != ChunkCrypto.NonceBytes)) throw new ArgumentException($"v2 packs need a {ChunkCrypto.NonceBytes}B nonce per blob", nameof(nonce12));

            var headerBytes = _v2 ? BlobHeaderBytesV2 : BlobHeaderBytes;
            var nextBlob = (long)headerBytes + payload.Length;
            if (_stream != null && _currentSize > 0 && _currentSize + nextBlob > _maxPackBytes)
                SealCurrent();
            if (_stream == null)
                OpenNew();

            var storedCodec = _v2 ? (byte)(codec | CodecEncryptedFlag) : codec;
            var offset = _currentSize;
            Span<byte> header = stackalloc byte[9];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)plainSize);
            header[8] = storedCodec;
            _stream!.Write(header);
            _stream.Write(digest32, 0, 32);
            if (_v2)
                _stream.Write(nonce12!, 0, ChunkCrypto.NonceBytes);
            if (payload.Length > 0)
                _stream.Write(payload, 0, payload.Length);
            var copy = new byte[32];
            Buffer.BlockCopy(digest32, 0, copy, 0, 32);
            _index.Add((copy, offset, (uint)payload.Length, (uint)plainSize, storedCodec));
            _currentSize += nextBlob;
            return (_packName!, offset);
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SealCurrent();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SealCurrent();
        }

        private void OpenNew()
        {
            while (true)
            {
                var name = "pack-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".pack";
                var tmp = Path.Combine(_packsDir, name + ".tmp");
                if (File.Exists(tmp) || File.Exists(Path.Combine(_packsDir, name)))
                    continue;
                _packName = name;
                _tmpPath = tmp;
                _stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _currentSize = 0;
                return;
            }
        }

        private void SealCurrent()
        {
            var stream = _stream;
            if (stream == null) return;
            var indexOffset = _currentSize;
            Span<byte> entry = stackalloc byte[17];
            foreach (var (digest, offset, stored, plain, codec) in _index)
            {
                stream.Write(digest, 0, 32);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)offset);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(8), stored);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(12), plain);
                entry[16] = codec;
                stream.Write(entry);
            }
            Span<byte> trailer = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(trailer, (ulong)indexOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer.Slice(8), (uint)_index.Count);
            trailer[12] = (byte)'C';
            trailer[13] = (byte)'P';
            trailer[14] = (byte)'K';
            trailer[15] = _v2 ? (byte)'2' : (byte)'1';
            stream.Write(trailer);
            // fsync before rename: the sealed pack survives power loss.
            stream.Flush(true);
            stream.Dispose();
            _stream = null;
            File.Move(_tmpPath!, Path.Combine(_packsDir, _packName!));
            _tmpPath = null;
            _packName = null;
            _index.Clear();
            _currentSize = 0;
        }
    }

    public sealed record PackBlob(byte[] Payload, byte Codec, byte[] Nonce);  // Codec WITHOUT 0x80; Nonce.Length==0 ⇔ unencrypted

    public static class PackReader
    {
        // Single-open read: the 9-byte header prefix already carries the codec
        // (byte 8), so report it instead of re-opening per chunk to re-read it.
        // The flag bit selects the 41B vs 53B header — trailer-agnostic.
        public static PackBlob ReadBlob(string packPath, long offset, int storedBytes)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (storedBytes < 0) throw new ArgumentOutOfRangeException(nameof(storedBytes));
            using (var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                Span<byte> header = stackalloc byte[9];
                Fill(stream, header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != (uint)storedBytes)
                    throw new InvalidDataException("stored length mismatch at offset " + offset);
                var encrypted = (header[8] & PackWriter.CodecEncryptedFlag) != 0;
                var codec = (byte)(header[8] & ~PackWriter.CodecEncryptedFlag);
                stream.Seek(offset + PackWriter.BlobHeaderBytes, SeekOrigin.Begin);
                byte[] nonce;
                if (encrypted)
                {
                    // Nonce sits between digest and payload; the stream is
                    // already positioned — fall through to the payload read.
                    nonce = new byte[ChunkCrypto.NonceBytes];
                    var filled = 0;
                    while (filled < nonce.Length)
                    {
                        var n = stream.Read(nonce, filled, nonce.Length - filled);
                        if (n == 0) throw new InvalidDataException("truncated pack nonce at offset " + offset);
                        filled += n;
                    }
                }
                else
                {
                    nonce = Array.Empty<byte>();
                }
                var payload = new byte[storedBytes];
                var pfilled = 0;
                while (pfilled < storedBytes)
                {
                    var n = stream.Read(payload, pfilled, storedBytes - pfilled);
                    if (n == 0) throw new InvalidDataException("truncated pack payload at offset " + offset);
                    pfilled += n;
                }
                return new PackBlob(payload, codec, nonce);
            }
        }

        // Legacy shim: old callers/tests read V1 payloads through this.
        public static byte[] ReadPayload(string packPath, long offset, int storedBytes, out byte codec)
        {
            var blob = ReadBlob(packPath, offset, storedBytes);
            codec = blob.Codec;
            return blob.Payload;
        }

        private static void Fill(Stream stream, Span<byte> buf)
        {
            var filled = 0;
            while (filled < buf.Length)
            {
                var n = stream.Read(buf.Slice(filled));
                if (n == 0) throw new InvalidDataException("truncated pack blob header");
                filled += n;
            }
        }
    }
}
