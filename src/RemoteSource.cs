using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    internal sealed class RemoteConn : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly bool _useTls;
        private readonly byte[] _psk;
        private TcpClient? _tcp;
        private Stream? _stream;
        private Socket? _rawSock;

        public RemoteConn(string host, int port, bool useTls, byte[] psk)
        {
            _host = host;
            _port = port;
            _useTls = useTls;
            _psk = psk;
        }

        public bool IsConnected => _stream != null && _tcp != null && _tcp.Connected;

        public void Connect(int timeoutMs = 10000)
        {
            Close();
            var tcp = new TcpClient { NoDelay = true };
            if (!tcp.ConnectAsync(_host, _port).Wait(timeoutMs))
            {
                tcp.Dispose();
                throw new TimeoutException($"Timed out connecting to remote {_host}:{_port}.");
            }

            tcp.Client.ReceiveBufferSize = RemoteProto.MaxChunkBytes;
            tcp.Client.SendBufferSize = RemoteProto.MaxChunkBytes;
            tcp.Client.ReceiveTimeout = 30000;
            tcp.Client.SendTimeout = 30000;

            var net = tcp.GetStream();
            Stream stream;
            Socket? rawSock = null;
            if (_useTls)
            {
                stream = RemoteProto.WrapClientTls(net, _host);
            }
            else
            {
                stream = net;
                rawSock = tcp.Client;
            }

            if (_psk.Length > 0)
                RemoteProto.AuthClient(stream, rawSock, _psk);

            _tcp = tcp;
            _stream = stream;
            _rawSock = rawSock;
        }

        public (uint Op, byte[] Payload) Request(RemoteOp op, byte[]? payload = null)
        {
            bool wasConnected = _stream != null && _tcp != null && _tcp.Connected;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureConnected();
                    RemoteProto.SendFrame(_stream!, _rawSock, (uint)op, payload);
                    var (respOp, respPayload) = RemoteProto.ReadFrame(_stream!);
                    if (respOp == (uint)RemoteOp.Error)
                        throw new IOException("Remote agent error: " + Encoding.UTF8.GetString(respPayload));
                    return (respOp, respPayload);
                }
                catch (Exception ex) when (attempt == 0 && wasConnected && (ex is IOException || ex is EndOfStreamException || ex is SocketException))
                {
                    // Transparently reconnect once if an idle pooled socket went stale.
                    Close();
                    wasConnected = false;
                }
            }
            throw new IOException("Remote request failed after reconnect.");
        }

        public void SendOnly(RemoteOp op, byte[]? payload = null)
        {
            EnsureConnected();
            RemoteProto.SendFrame(_stream!, _rawSock, (uint)op, payload);
        }

        public (uint Op, byte[] Payload) ReadNext()
        {
            if (_stream == null) throw new IOException("Not connected.");
            return RemoteProto.ReadFrame(_stream);
        }

        public (uint Op, int Length) ReadNextInto(byte[] dstBuffer)
        {
            if (_stream == null) throw new IOException("Not connected.");
            return RemoteProto.ReadFrameInto(_stream, dstBuffer);
        }

        public void SetReadTimeout(int ms)
        {
            if (_tcp != null) _tcp.Client.ReceiveTimeout = ms;
        }

        private void EnsureConnected()
        {
            if (_stream == null || _tcp == null || !_tcp.Connected)
                Connect();
        }

        public void Close()
        {
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _stream = null;
            _tcp = null;
            _rawSock = null;
        }

        public void Dispose() => Close();
    }

    public sealed class RemoteFileAccessor : IFileAccessor, ISourceStat, IHashProvider, IPrefixHashProvider, IDisposable
    {
        private readonly string _basePrefix;
        private readonly string _host;
        private readonly int _port;
        private readonly bool _useTls;
        private readonly byte[] _psk;
        private readonly ConcurrentBag<RemoteConn> _pool = new ConcurrentBag<RemoteConn>();
        private readonly ConcurrentDictionary<string, FileSnapshot> _statCache = new ConcurrentDictionary<string, FileSnapshot>(StringComparer.Ordinal);
        private bool _disposed;

        public string BaseUri => _basePrefix;

        public RemoteFileAccessor(string endpoint, bool useTls, byte[] psk)
        {
            var (host, port) = ParseHostPort(endpoint);
            _host = host;
            _port = port;
            _useTls = useTls;
            _psk = psk;
            _basePrefix = $"remote://{host}:{port}";
        }

        internal RemoteConn LeaseConn() =>
            _pool.TryTake(out var conn) ? conn : new RemoteConn(_host, _port, _useTls, _psk);

        internal void ReturnConn(RemoteConn conn, bool broken)
        {
            if (broken || _disposed || _pool.Count >= 16)
            {
                conn.Dispose();
                return;
            }
            _pool.Add(conn);
        }

        public static string MakeBaseUri(string endpoint)
        {
            var (host, port) = ParseHostPort(endpoint);
            return $"remote://{host}:{port}";
        }

        public Stream OpenRead(string path)
        {
            var rel = ToRelative(path);
            if (!_statCache.TryGetValue(rel, out var snap) && !TryStat(path, out snap))
                throw new FileNotFoundException("Remote file not found.", path);
            return new RemoteReadStream(this, rel, snap.Length);
        }

        public bool TryStat(string path, out FileSnapshot snapshot)
        {
            var rel = ToRelative(path);
            var conn = LeaseConn();
            bool broken = false;
            try
            {
                var (_, payload) = conn.Request(RemoteOp.Stat, Encoding.UTF8.GetBytes(rel));
                if (payload.Length < 1 || payload[0] == 0)
                {
                    _statCache.TryRemove(rel, out _);
                    snapshot = default;
                    return false;
                }
                long len = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1, 8));
                long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(9, 8));
                var attr = (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(17, 4));
                snapshot = new FileSnapshot(len, ticks, attr);
                _statCache[rel] = snapshot;
                return true;
            }
            catch
            {
                broken = true;
                snapshot = default;
                return false;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
        }

        public bool Exists(string path) => TryStat(path, out _);

        public bool TryComputeHash(string path, out string hexHash)
        {
            var rel = ToRelative(path);
            var conn = LeaseConn();
            bool broken = false;
            try
            {
                var (_, payload) = conn.Request(RemoteOp.Hash, Encoding.UTF8.GetBytes(rel));
                hexHash = Encoding.UTF8.GetString(payload);
                return hexHash.Length == 64;
            }
            catch
            {
                broken = true;
                hexHash = string.Empty;
                return false;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
        }

        public bool TryComputePrefixHash(string path, long prefixLen, out string hexHash)
        {
            var rel = ToRelative(path);
            var relBytes = Encoding.UTF8.GetBytes(rel);
            var req = new byte[10 + relBytes.Length];
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(0, 8), prefixLen);
            BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(8, 2), (ushort)relBytes.Length);
            Buffer.BlockCopy(relBytes, 0, req, 10, relBytes.Length);

            var conn = LeaseConn();
            bool broken = false;
            try
            {
                var (_, payload) = conn.Request(RemoteOp.HashPrefix, req);
                hexHash = Encoding.UTF8.GetString(payload);
                return hexHash.Length == 64;
            }
            catch
            {
                broken = true;
                hexHash = string.Empty;
                return false;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
        }

        public bool DirectoryExists(string path)
        {
            var conn = LeaseConn();
            bool broken = false;
            try
            {
                if (!conn.IsConnected)
                    conn.Connect();
                return true;
            }
            catch
            {
                broken = true;
                return false;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
        }

        public IEnumerable<string> EnumerateFiles(string path, SearchOption option)
        {
            var list = new List<string>();
            var conn = LeaseConn();
            bool broken = false;
            try
            {
                conn.SendOnly(RemoteOp.List);
                while (true)
                {
                    var (op, payload) = conn.ReadNext();
                    if (op == (uint)RemoteOp.Error)
                        throw new IOException("Remote list error: " + Encoding.UTF8.GetString(payload));
                    if (payload.Length < 5)
                        throw new InvalidDataException("Invalid OP_LIST response.");

                    bool hasMore = payload[0] != 0;
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4));
                    int off = 5;
                    for (uint i = 0; i < count; i++)
                    {
                        ushort relLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(off, 2));
                        off += 2;
                        var rel = Encoding.UTF8.GetString(payload, off, relLen);
                        off += relLen;
                        long len = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(off, 8));
                        long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(off + 8, 8));
                        var attr = (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(off + 16, 4));
                        off += 20;

                        _statCache[rel] = new FileSnapshot(len, ticks, attr);
                        list.Add($"{_basePrefix}/{rel}");
                    }

                    if (!hasMore) break;
                }
            }
            catch
            {
                broken = true;
                throw;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
            return list;
        }

        internal int ReadChunk(string rel, long offset, byte[] dst, int dstOffset, int count)
        {
            var relBytes = Encoding.UTF8.GetBytes(rel);
            var req = new byte[14 + relBytes.Length];
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(0, 8), offset);
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(8, 4), count);
            BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(12, 2), (ushort)relBytes.Length);
            Buffer.BlockCopy(relBytes, 0, req, 14, relBytes.Length);

            var conn = LeaseConn();
            bool broken = false;
            try
            {
                var (_, payload) = conn.Request(RemoteOp.ReadChunk, req);
                if (payload.Length < 4)
                    throw new InvalidDataException("Invalid OP_READ_CHUNK response.");
                int got = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
                if (got < 0 || 4 + got != payload.Length || got > count)
                    throw new InvalidDataException("Corrupt OP_READ_CHUNK length.");
                if (got > 0)
                    Buffer.BlockCopy(payload, 4, dst, dstOffset, got);
                return got;
            }
            catch
            {
                broken = true;
                throw;
            }
            finally
            {
                ReturnConn(conn, broken);
            }
        }

        internal RemoteConn BeginReadFile(string rel, long offset)
        {
            var relBytes = Encoding.UTF8.GetBytes(rel);
            var req = new byte[10 + relBytes.Length];
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(0, 8), offset);
            BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(8, 2), (ushort)relBytes.Length);
            Buffer.BlockCopy(relBytes, 0, req, 10, relBytes.Length);

            var conn = LeaseConn();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    conn.SendOnly(RemoteOp.ReadFile, req);
                    return conn;
                }
                catch (Exception ex) when (attempt == 0 && (ex is IOException || ex is EndOfStreamException || ex is SocketException))
                {
                    conn.Close();
                }
                catch
                {
                    ReturnConn(conn, broken: true);
                    throw;
                }
            }
            ReturnConn(conn, broken: true);
            throw new IOException("Remote read-file failed.");
        }

        internal static int ReadNextFileFrame(RemoteConn conn, byte[] dstBuffer)
        {
            var (op, len) = conn.ReadNextInto(dstBuffer);
            if (op == (uint)RemoteOp.Error)
                throw new IOException("Remote read-file error: " + Encoding.UTF8.GetString(dstBuffer, 0, len));
            return len;
        }

        internal void EndReadFile(RemoteConn conn, bool aborted) =>
            ReturnConn(conn, broken: aborted);

        private string ToRelative(string path)
        {
            if (path.StartsWith(_basePrefix, StringComparison.Ordinal))
                return path.Substring(_basePrefix.Length).TrimStart('/');
            return path.TrimStart('/');
        }

        internal static (string Host, int Port) ParseHostPort(string endpoint)
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || colon == endpoint.Length - 1)
                throw new ArgumentException("Remote endpoint must be host:port.", nameof(endpoint));
            var host = endpoint.Substring(0, colon).Trim('[', ']');
            if (!int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                throw new ArgumentException("Invalid remote port: " + endpoint, nameof(endpoint));
            return (host, port);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            while (_pool.TryTake(out var conn))
                conn.Dispose();
        }
    }

    internal sealed class RemoteReadStream : Stream
    {
        private readonly RemoteFileAccessor _accessor;
        private readonly string _rel;
        private readonly long _length;
        private long _pos;
        private byte[]? _buf;
        private int _bufPos;
        private int _bufLen;
        private RemoteConn? _streamConn;
        private bool _eof;
        private bool _disposed;

        public RemoteReadStream(RemoteFileAccessor accessor, string rel, long length)
        {
            _accessor = accessor;
            _rel = rel;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length || count <= 0) return 0;

            if (_streamConn == null && count == 1)
            {
                int single = _accessor.ReadChunk(_rel, _pos, buffer, offset, 1);
                _pos += single;
                return single;
            }

            if (_streamConn == null)
            {
                _buf = ArrayPool<byte>.Shared.Rent(RemoteProto.MaxChunkBytes);
                _streamConn = _accessor.BeginReadFile(_rel, _pos);
            }

            int total = 0;
            while (total < count && !_eof)
            {
                if (_bufPos >= _bufLen)
                {
                    _bufPos = 0;
                    _bufLen = RemoteFileAccessor.ReadNextFileFrame(_streamConn, _buf!);
                    if (_bufLen == 0)
                    {
                        _eof = true;
                        _accessor.EndReadFile(_streamConn, aborted: false);
                        _streamConn = null;
                        break;
                    }
                }

                int avail = _bufLen - _bufPos;
                int copy = Math.Min(avail, count - total);
                Buffer.BlockCopy(_buf!, _bufPos, buffer, offset + total, copy);
                _bufPos += copy;
                _pos += copy;
                total += copy;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0) throw new IOException("Seek before beginning.");
            if (target != _pos && _streamConn != null)
            {
                _accessor.EndReadFile(_streamConn, aborted: true);
                _streamConn = null;
                _bufPos = 0;
                _bufLen = 0;
                _eof = false;
            }
            _pos = target;
            return _pos;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                if (_streamConn != null)
                {
                    _accessor.EndReadFile(_streamConn, aborted: !_eof);
                    _streamConn = null;
                }
                if (_buf != null)
                {
                    ArrayPool<byte>.Shared.Return(_buf);
                    _buf = null;
                }
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class RemoteFileMonitor : IFileMonitor, IGapSource
    {
        private readonly string _host;
        private readonly int _port;
        private readonly bool _useTls;
        private readonly byte[] _psk;
        private readonly string _baseUri;
        private readonly int _restartDelayMs;
        private readonly ILogger _log;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private bool _disposed;

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;
        public event EventHandler? GapDetected;

        public RemoteFileMonitor(string endpoint, bool useTls, byte[] psk, int restartDelaySeconds, ILogger log)
        {
            var (host, port) = RemoteFileAccessor.ParseHostPort(endpoint);
            _host = host;
            _port = port;
            _useTls = useTls;
            _psk = psk;
            _baseUri = $"remote://{host}:{port}";
            _restartDelayMs = Math.Max(1, restartDelaySeconds) * 1000;
            _log = log;
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _loopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }

        private void RunLoop(CancellationToken ct)
        {
            bool connectedOnce = false;
            while (!ct.IsCancellationRequested)
            {
                using var conn = new RemoteConn(_host, _port, _useTls, _psk);
                using var reg = ct.Register(conn.Close);
                try
                {
                    conn.Connect();
                    conn.Request(RemoteOp.Subscribe);
                    conn.SetReadTimeout(Timeout.Infinite);

                    if (connectedOnce)
                    {
                        _log.Info($"Reconnected to remote {_host}:{_port}; requesting covering scan.");
                        GapDetected?.Invoke(this, EventArgs.Empty);
                    }
                    connectedOnce = true;

                    while (!ct.IsCancellationRequested)
                    {
                        var (op, payload) = conn.ReadNext();
                        if (op != (uint)RemoteOp.ChangeEvent || payload.Length < 5)
                            continue;
                        DispatchEvent(payload);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Error?.Invoke(this, new MonitorErrorEventArgs(ex));
                    _log.Warn($"Remote monitor {_host}:{_port} disconnected ({ex.Message}); retrying in {_restartDelayMs}ms.");
                    if (ct.WaitHandle.WaitOne(_restartDelayMs)) break;
                }
            }
        }

        private void DispatchEvent(byte[] payload)
        {
            var changeType = (ChangeType)payload[0];
            ushort relLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1, 2));
            var rel = Encoding.UTF8.GetString(payload, 3, relLen);
            int off = 3 + relLen;
            ushort oldLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(off, 2));
            string? oldPath = oldLen > 0
                ? $"{_baseUri}/{Encoding.UTF8.GetString(payload, off + 2, oldLen)}"
                : null;
            Changed?.Invoke(this, new FileChangedEventArgs(changeType, $"{_baseUri}/{rel}", oldPath));
        }
    }
}
