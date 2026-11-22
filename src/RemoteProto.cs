using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TicTack
{
    public enum RemoteOp : uint
    {
        Auth = 1,
        List = 2,
        Stat = 3,
        ReadChunk = 4,
        Hash = 5,
        Subscribe = 6,
        ReadFile = 7,
        HashPrefix = 8,
        Ok = 100,
        Error = 101,
        ChangeEvent = 0x80000001u,
    }

    internal static class RemoteProto
    {
        public const uint Magic = 0x5454524Du; // "TTRM"
        public const ushort WireVersion = 1;
        public const int HeaderBytes = 12;
        public const int MaxFrameBytes = 16 * 1024 * 1024;
        public const int MaxChunkBytes = 4 * 1024 * 1024;
        public const int NonceBytes = 32;
        public const int HmacBytes = 32;

        public static void EncodeHeader(Span<byte> dst, uint op, int payloadLen)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), op);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), (uint)payloadLen);
        }

        public static (uint Op, int Length) DecodeHeader(ReadOnlySpan<byte> src)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
            if (magic != Magic)
                throw new InvalidDataException("Invalid remote frame magic.");
            uint op = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4));
            if (len > MaxFrameBytes)
                throw new InvalidDataException($"Remote frame length {len} exceeds limit {MaxFrameBytes}.");
            return (op, (int)len);
        }

        // Vectored gather-send over raw TCP (maps to WSASend on Windows,
        // writev/sendmsg on Linux) so header + op metadata + payload slices
        // go out without copying into a single heap buffer.
        public static void SendGather(Socket sock, ArraySegment<byte>[] segs)
        {
            int idx = 0;
            while (idx < segs.Length)
            {
                if (segs[idx].Count == 0)
                {
                    idx++;
                    continue;
                }
                var slice = new ArraySegment<ArraySegment<byte>>(segs, idx, segs.Length - idx);
                int sent = sock.Send(slice, SocketFlags.None);
                if (sent <= 0)
                    throw new IOException("Remote socket closed during gather send.");
                while (idx < segs.Length && sent >= segs[idx].Count)
                {
                    sent -= segs[idx].Count;
                    idx++;
                }
                if (idx < segs.Length && sent > 0)
                {
                    var cur = segs[idx];
                    segs[idx] = new ArraySegment<byte>(cur.Array!, cur.Offset + sent, cur.Count - sent);
                }
            }
        }

        public static void SendFrame(Stream stream, Socket? rawSock, uint op, byte[]? part1 = null, byte[]? part2 = null, int part2Offset = 0, int part2Count = -1)
        {
            int len1 = part1?.Length ?? 0;
            int len2 = part2 == null ? 0 : (part2Count >= 0 ? part2Count : part2.Length - part2Offset);
            int total = len1 + len2;
            if (total > MaxFrameBytes)
                throw new InvalidDataException($"Frame payload {total} exceeds limit {MaxFrameBytes}.");

            var hdr = ArrayPool<byte>.Shared.Rent(HeaderBytes);
            try
            {
                EncodeHeader(hdr.AsSpan(0, HeaderBytes), op, total);

                if (rawSock != null)
                {
                    var list = new List<ArraySegment<byte>>(3) { new ArraySegment<byte>(hdr, 0, HeaderBytes) };
                    if (len1 > 0) list.Add(new ArraySegment<byte>(part1!));
                    if (len2 > 0) list.Add(new ArraySegment<byte>(part2!, part2Offset, len2));
                    SendGather(rawSock, list.ToArray());
                    return;
                }

                // Coalesce [hdr | part1 | part2] into one rented buffer so SslStream
                // emits a single TLS record + single syscall instead of 3 writes.
                int frameLen = HeaderBytes + total;
                var coalesced = ArrayPool<byte>.Shared.Rent(frameLen);
                try
                {
                    Buffer.BlockCopy(hdr, 0, coalesced, 0, HeaderBytes);
                    int off = HeaderBytes;
                    if (len1 > 0)
                    {
                        Buffer.BlockCopy(part1!, 0, coalesced, off, len1);
                        off += len1;
                    }
                    if (len2 > 0)
                        Buffer.BlockCopy(part2!, part2Offset, coalesced, off, len2);
                    stream.Write(coalesced, 0, frameLen);
                    stream.Flush();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(coalesced);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(hdr);
            }
        }

        public static (uint Op, byte[] Payload) ReadFrame(Stream stream)
        {
            var hdr = ArrayPool<byte>.Shared.Rent(HeaderBytes);
            int len;
            uint op;
            try
            {
                ReadExact(stream, hdr, 0, HeaderBytes);
                (op, len) = DecodeHeader(hdr.AsSpan(0, HeaderBytes));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(hdr);
            }
            var payload = len == 0 ? Array.Empty<byte>() : new byte[len];
            if (len > 0)
                ReadExact(stream, payload, 0, len);
            return (op, payload);
        }

        public static (uint Op, int Length) ReadFrameInto(Stream stream, byte[] dstBuffer)
        {
            var hdr = ArrayPool<byte>.Shared.Rent(HeaderBytes);
            int len;
            uint op;
            try
            {
                ReadExact(stream, hdr, 0, HeaderBytes);
                (op, len) = DecodeHeader(hdr.AsSpan(0, HeaderBytes));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(hdr);
            }
            if (len > dstBuffer.Length)
                throw new InvalidDataException($"Frame payload {len} exceeds target buffer {dstBuffer.Length}.");
            if (len > 0)
                ReadExact(stream, dstBuffer, 0, len);
            return (op, len);
        }

        public static void ReadExact(Stream stream, byte[] buf, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = stream.Read(buf, offset + got, count - got);
                if (r <= 0)
                    throw new EndOfStreamException("Remote stream closed.");
                got += r;
            }
        }

        public static byte[] ResolvePsk(string? psk, string? pskEnv, string? pskFile)
        {
            string? raw = null;
            if (!string.IsNullOrEmpty(psk))
                raw = psk;
            else if (!string.IsNullOrEmpty(pskEnv))
                raw = Environment.GetEnvironmentVariable(pskEnv);
            else if (!string.IsNullOrEmpty(pskFile) && File.Exists(pskFile))
                raw = File.ReadAllText(pskFile).Trim();

            if (string.IsNullOrEmpty(raw))
                return Array.Empty<byte>();
            return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        }

        public static void AuthServer(Stream stream, Socket? rawSock, byte[] psk)
        {
            var serverNonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(serverNonce);
            var challenge = new byte[2 + NonceBytes];
            BinaryPrimitives.WriteUInt16LittleEndian(challenge.AsSpan(0, 2), WireVersion);
            Buffer.BlockCopy(serverNonce, 0, challenge, 2, NonceBytes);
            SendFrame(stream, rawSock, (uint)RemoteOp.Auth, challenge);

            var (op, resp) = ReadFrame(stream);
            if (op != (uint)RemoteOp.Auth || resp.Length != NonceBytes + HmacBytes)
                throw new AuthenticationException("Invalid remote auth response.");

            var clientNonce = resp.AsSpan(0, NonceBytes);
            var clientMac = resp.AsSpan(NonceBytes, HmacBytes);
            var expected = ComputeAuthMac("TTRM-C", psk, serverNonce, clientNonce);
            if (!CryptographicOperations.FixedTimeEquals(clientMac, expected))
                throw new AuthenticationException("PSK authentication failed.");

            var serverMac = ComputeAuthMac("TTRM-S", psk, clientNonce, serverNonce);
            SendFrame(stream, rawSock, (uint)RemoteOp.Ok, serverMac);
        }

        public static void AuthClient(Stream stream, Socket? rawSock, byte[] psk)
        {
            var (op, challenge) = ReadFrame(stream);
            if (op != (uint)RemoteOp.Auth || challenge.Length != 2 + NonceBytes)
                throw new AuthenticationException("Invalid remote auth challenge.");

            ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(challenge.AsSpan(0, 2));
            if (ver != WireVersion)
                throw new AuthenticationException($"Unsupported remote wire version {ver}.");

            var serverNonce = challenge.AsSpan(2, NonceBytes);
            var clientNonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(clientNonce);

            var clientMac = ComputeAuthMac("TTRM-C", psk, serverNonce, clientNonce);
            var msg = new byte[NonceBytes + HmacBytes];
            Buffer.BlockCopy(clientNonce, 0, msg, 0, NonceBytes);
            Buffer.BlockCopy(clientMac, 0, msg, NonceBytes, HmacBytes);
            SendFrame(stream, rawSock, (uint)RemoteOp.Auth, msg);

            var (okOp, okPayload) = ReadFrame(stream);
            if (okOp != (uint)RemoteOp.Ok || okPayload.Length != HmacBytes)
                throw new AuthenticationException("Remote server rejected PSK authentication.");

            var expectedServerMac = ComputeAuthMac("TTRM-S", psk, clientNonce, serverNonce);
            if (!CryptographicOperations.FixedTimeEquals(okPayload, expectedServerMac))
                throw new AuthenticationException("Remote server mutual auth failed.");
        }

        private static byte[] ComputeAuthMac(string tag, byte[] psk, ReadOnlySpan<byte> n1, ReadOnlySpan<byte> n2)
        {
            var tagBytes = Encoding.ASCII.GetBytes(tag);
            var data = new byte[tagBytes.Length + n1.Length + n2.Length];
            Buffer.BlockCopy(tagBytes, 0, data, 0, tagBytes.Length);
            n1.CopyTo(data.AsSpan(tagBytes.Length));
            n2.CopyTo(data.AsSpan(tagBytes.Length + n1.Length));
            return HMACSHA256.HashData(psk, data);
        }

        public static X509Certificate2 CreateEphemeralCert()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=TicTackRemote", ecdsa, HashAlgorithmName.SHA256);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var pfx = cert.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }

        public static Stream WrapServerTls(NetworkStream net, X509Certificate2 cert)
        {
            var ssl = new SslStream(net, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsServer(cert, clientCertificateRequired: false, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            return ssl;
        }

        public static Stream WrapClientTls(NetworkStream net, string targetHost)
        {
            // Ephemeral cert is verified via PSK HMAC mutual auth immediately after TLS handshake.
            var ssl = new SslStream(net, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            ssl.AuthenticateAsClient(targetHost, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            return ssl;
        }
    }
}
