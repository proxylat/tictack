using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TicTack;

namespace Corpus
{
    // Pure-C# localhost-only (127.0.0.1) read-only NBD (Network Block Device) server
    // using the fixed-newstyle protocol. Enables instant 1-second QEMU/KVM VM boot or
    // Linux nbd-client mounting directly from CPK1 packs with zero export wait.
    public sealed class NbdServer : IDisposable
    {
        private const ulong NbdMagic = 0x4e42444d41474943UL; // "NBDMAGIC"
        private const ulong OptMagic = 0x49484156454F5054UL; // "IHAVEOPT"
        private const ulong ReplyMagic = 0x3e889045565a9UL;
        private const uint RequestMagic = 0x25609513u;
        private const uint SimpleReplyMagic = 0x67446698u;
        private const ushort FlagFixedNewstyle = 1;
        private const ushort FlagNoZeroes = 2;
        private const ushort TransFlagHasFlags = 1;
        private const ushort TransFlagReadOnly = 2;

        private readonly Func<int, byte[], int, long, int> _readAt;
        private readonly long _exportSize;
        private readonly string _exportName;
        private readonly ILogger _log;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;
        private bool _disposed;

        public int Port { get; }

        internal NbdServer(long exportSize, string exportName, Func<int, byte[], int, long, int> readAt, ILogger log, int port = 0)
        {
            _exportSize = exportSize;
            _exportName = exportName;
            _readAt = readAt;
            _log = log;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public static NbdServer StartForSnapshot(string storeRoot, string dbPath, int generation, ILogger log, int port = 0)
        {
            var key = ChunkId.EnsureKey(storeRoot);
            var manifestsDir = Path.Combine(storeRoot, "manifests");
            SnapshotManifest? found = null;
            foreach (var (gen, name) in ManifestStore.List(manifestsDir))
            {
                if (gen == generation)
                {
                    found = ManifestStore.Load(Path.Combine(manifestsDir, $"{gen:D6}-{name}.json"), key);
                    break;
                }
            }
            if (found == null)
                throw new InvalidDataException($"NBD: snapshot {generation} not found");
            var reader = new SnapshotVolumeReader(storeRoot, dbPath, found, new ZstdDeltaCodec());
            var gate = new object();
            return new NbdServer(found.VolumeLength, $"gen-{generation}", (count, buf, off, pos) =>
            {
                lock (gate) { reader.Read(pos, buf, off, count); }
                return count;
            }, log, port);
        }

        public static NbdServer StartForLayout(string storeRoot, string dbPath, int layoutGen, ILogger log, int port = 0)
        {
            using var store = new Store(storeRoot, dbPath, 4, 3, log);
            var layout = store.GetLayout(layoutGen);
            var capacity = OvfExporter.ComputeDiskCapacity(layout);
            var headers = new MemoryStream();
            var readers = new List<(long Start, long End, SnapshotVolumeReader Reader)>();
            var key = ChunkId.EnsureKey(storeRoot);
            var manifestsDir = Path.Combine(storeRoot, "manifests");
            foreach (var part in layout.Partitions)
            {
                if (part.Skipped || part.SnapshotGeneration <= 0) continue;
                SnapshotManifest? mf = null;
                foreach (var (gen, name) in ManifestStore.List(manifestsDir))
                {
                    if (gen == part.SnapshotGeneration)
                    {
                        mf = ManifestStore.Load(Path.Combine(manifestsDir, $"{gen:D6}-{name}.json"), key);
                        break;
                    }
                }
                if (mf != null)
                    readers.Add((part.OffsetBytes, part.OffsetBytes + mf.VolumeLength, new SnapshotVolumeReader(storeRoot, dbPath, mf, new ZstdDeltaCodec())));
            }
            // Synthesize GPT/MBR headers into memory for instant LBA 0..33 / backup GPT reads.
            GptWriter.WriteWholeDisk(headers, layout, capacity, (_, _) => { });
            var hdrBytes = headers.ToArray();
            var gate = new object();
            return new NbdServer(capacity, $"layout-{layoutGen}", (count, buf, off, pos) =>
            {
                Array.Clear(buf, off, count);
                lock (gate)
                {
                    foreach (var (start, end, r) in readers)
                    {
                        var rStart = Math.Max(pos, start);
                        var rEnd = Math.Min(pos + count, end);
                        if (rEnd > rStart)
                            r.Read(rStart - start, buf, off + (int)(rStart - pos), (int)(rEnd - rStart));
                    }
                    // Overlay synthesized GPT/MBR header & backup GPT sectors.
                    OverlayHeader(hdrBytes, pos, count, buf, off, 0, Math.Min(hdrBytes.Length, 1024 * 1024));
                    var tailStart = Math.Max(0, hdrBytes.Length - 64 * 1024);
                    OverlayHeader(hdrBytes, pos, count, buf, off, tailStart, hdrBytes.Length);
                }
                return count;
            }, log, port);
        }

        private static void OverlayHeader(byte[] hdr, long pos, int count, byte[] buf, int off, int regionStart, int regionEnd)
        {
            var s = Math.Max(pos, regionStart);
            var e = Math.Min(pos + count, regionEnd);
            if (e > s)
                Buffer.BlockCopy(hdr, (int)s, buf, off + (int)(s - pos), (int)(e - s));
        }

        public void Start()
        {
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log.Info($"nbd: listening read-only on nbd://127.0.0.1:{Port}/{_exportName} ({_exportSize} bytes)");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { _log.Debug("nbd accept error: " + ex.Message); }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var stream = client.GetStream();
                var hs = new byte[18];
                BinaryPrimitives.WriteUInt64BigEndian(hs.AsSpan(0, 8), NbdMagic);
                BinaryPrimitives.WriteUInt64BigEndian(hs.AsSpan(8, 8), OptMagic);
                BinaryPrimitives.WriteUInt16BigEndian(hs.AsSpan(16, 2), FlagFixedNewstyle | FlagNoZeroes);
                await stream.WriteAsync(hs, ct).ConfigureAwait(false);
                var cflagsBuf = new byte[4];
                if (!await ReadExactAsync(stream, cflagsBuf, ct).ConfigureAwait(false)) return;
                if (!await NegotiateOptionsAsync(stream, ct).ConfigureAwait(false)) return;
                await ServeRequestsAsync(stream, ct).ConfigureAwait(false);
            }
        }

        private async Task<bool> NegotiateOptionsAsync(NetworkStream stream, CancellationToken ct)
        {
            var optHdr = new byte[16];
            while (await ReadExactAsync(stream, optHdr, ct).ConfigureAwait(false))
            {
                if (BinaryPrimitives.ReadUInt64BigEndian(optHdr.AsSpan(0, 8)) != OptMagic) return false;
                var opt = BinaryPrimitives.ReadUInt32BigEndian(optHdr.AsSpan(8, 4));
                var len = BinaryPrimitives.ReadUInt32BigEndian(optHdr.AsSpan(12, 4));
                var payload = len > 0 ? new byte[Math.Min(len, 65536)] : Array.Empty<byte>();
                if (payload.Length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) return false;

                if (opt == 1)
                {
                    var rep = new byte[10];
                    BinaryPrimitives.WriteUInt64BigEndian(rep.AsSpan(0, 8), (ulong)_exportSize);
                    BinaryPrimitives.WriteUInt16BigEndian(rep.AsSpan(8, 2), TransFlagHasFlags | TransFlagReadOnly);
                    await stream.WriteAsync(rep, ct).ConfigureAwait(false);
                    return true;
                }
                if (opt is 6 or 7)
                {
                    await SendGoInfoAsync(stream, opt, ct).ConfigureAwait(false);
                    if (opt == 7) return true;
                    continue;
                }
                if (opt == 2) return false;
                var unsup = new byte[20];
                BinaryPrimitives.WriteUInt64BigEndian(unsup.AsSpan(0, 8), ReplyMagic);
                BinaryPrimitives.WriteUInt32BigEndian(unsup.AsSpan(8, 4), opt);
                BinaryPrimitives.WriteUInt32BigEndian(unsup.AsSpan(12, 4), 0x80000001u);
                await stream.WriteAsync(unsup, ct).ConfigureAwait(false);
            }
            return false;
        }

        private async Task SendGoInfoAsync(NetworkStream stream, uint opt, CancellationToken ct)
        {
            var info = new byte[32];
            BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(0, 8), ReplyMagic);
            BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(8, 4), opt);
            BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(12, 4), 3);
            BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(16, 4), 12);
            BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(20, 2), 0);
            BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(22, 8), (ulong)_exportSize);
            BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(30, 2), TransFlagHasFlags | TransFlagReadOnly);
            await stream.WriteAsync(info, ct).ConfigureAwait(false);
            var ack = new byte[20];
            BinaryPrimitives.WriteUInt64BigEndian(ack.AsSpan(0, 8), ReplyMagic);
            BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(8, 4), opt);
            BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(12, 4), 1);
            await stream.WriteAsync(ack, ct).ConfigureAwait(false);
        }

        private async Task ServeRequestsAsync(NetworkStream stream, CancellationToken ct)
        {
            var req = new byte[28];
            while (await ReadExactAsync(stream, req, ct).ConfigureAwait(false))
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(0, 4)) != RequestMagic) return;
                var cmdType = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6, 2));
                var handle = req.AsSpan(8, 8).ToArray();
                var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(req.AsSpan(16, 8));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(24, 4));
                if (cmdType == 2) return;
                var reply = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(0, 4), SimpleReplyMagic);
                Buffer.BlockCopy(handle, 0, reply, 8, 8);
                if (cmdType != 0 || offset < 0 || offset + length > _exportSize)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), cmdType == 0 ? 22u : 30u);
                    await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                    continue;
                }
                var data = new byte[length];
                _readAt(length, data, 0, offset);
                await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
            }
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            var got = 0;
            while (got < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(got, buf.Length - got), ct).ConfigureAwait(false);
                if (n == 0) return false;
                got += n;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptLoop?.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}
