using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public sealed class RemoteAgentServer : IDisposable
    {
        private readonly string _rootFull;
        private readonly string _listen;
        private readonly IPAddress? _allowIp;
        private readonly int _maxClients;
        private readonly bool _useTls;
        private readonly byte[] _psk;
        private readonly ILogger _log;
        private readonly FileAccessor _accessor = new FileAccessor();
        private readonly ConcurrentDictionary<int, Stream> _subscribers = new ConcurrentDictionary<int, Stream>();
        private readonly ConcurrentDictionary<int, Socket?> _subscriberSocks = new ConcurrentDictionary<int, Socket?>();
        private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime BannedUntilUtc)> _authFailures = new ConcurrentDictionary<IPAddress, (int, DateTime)>();
        private readonly object _subSendLock = new object();
        private const int AuthMaxFailures = 5;
        private static readonly TimeSpan AuthBanDuration = TimeSpan.FromSeconds(60);

        private TcpListener? _listener;
        private X509Certificate2? _cert;
        private IFileMonitor? _watcher;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private int _activeClients;
        private int _nextClientId;
        private bool _disposed;

        private static readonly StringComparison RootCmp =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public int LocalPort { get; private set; }

        public RemoteAgentServer(
            string root,
            string listen,
            string? allowIp,
            int maxClients,
            bool useTls,
            byte[] psk,
            ILogger log)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("remote_sync.root is required.", nameof(root));
            _rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(_rootFull))
                throw new DirectoryNotFoundException("Remote agent root does not exist: " + _rootFull);

            _listen = listen;
            _allowIp = string.IsNullOrWhiteSpace(allowIp) ? null : IPAddress.Parse(allowIp.Trim());
            _maxClients = Math.Max(1, maxClients);
            _useTls = useTls;
            _psk = psk;
            _log = log;
        }

        public void Start()
        {
            if (_cts != null) return;
            var (ip, port) = ParseEndpoint(_listen);
            if (_useTls)
                _cert = RemoteProto.CreateEphemeralCert();

            _listener = new TcpListener(ip, port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _watcher = OperatingSystem.IsWindows()
                ? new FileWatcherMonitor(_rootFull, 1024, 5)
                : new FsWatchMonitor(_rootFull, 1024, 5);
            _watcher.Changed += OnRootChanged;
            _watcher.Error += (_, e) => _log.Warn("Remote agent watcher error: " + e.Exception.Message);
            _watcher.Start();

            _cts = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
            _log.Info($"Remote agent listening on {ip}:{LocalPort} (root={_rootFull}, tls={_useTls}, allow_ip={_allowIp?.ToString() ?? "any"})");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _watcher?.Stop(); } catch { }
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            try { _acceptTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _cert?.Dispose();
            _cert = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }

        private void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                Socket sock;
                try { sock = _listener.AcceptSocket(); }
                catch { break; }

                if (!IsPeerAllowed(sock))
                {
                    CloseQuiet(sock);
                    continue;
                }

                if (Interlocked.Increment(ref _activeClients) > _maxClients)
                {
                    Interlocked.Decrement(ref _activeClients);
                    _log.Warn($"Remote agent max_clients ({_maxClients}) reached; dropping connection.");
                    CloseQuiet(sock);
                    continue;
                }

                int id = Interlocked.Increment(ref _nextClientId);
                _ = Task.Run(() => HandleClient(id, sock, ct), CancellationToken.None);
            }
        }

        private bool IsPeerAllowed(Socket sock)
        {
            if (sock.RemoteEndPoint is not IPEndPoint ep) return false;
            var addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            if (IsPeerBanned(addr))
            {
                _log.Warn($"Remote agent dropped banned peer {addr}");
                return false;
            }
            if (_allowIp == null) return true;
            if (IPAddress.IsLoopback(addr) || addr.Equals(_allowIp))
                return true;
            _log.Warn($"Remote agent rejected non-allowlisted peer {addr}");
            return false;
        }

        private bool IsPeerBanned(IPAddress addr)
        {
            if (_authFailures.TryGetValue(addr, out var state) && state.BannedUntilUtc > DateTime.UtcNow)
                return true;
            return false;
        }

        private void RecordAuthFailure(IPAddress? addr)
        {
            if (addr == null) return;
            var now = DateTime.UtcNow;
            _authFailures.AddOrUpdate(addr,
                _ => (1, DateTime.MinValue),
                (_, prev) =>
                {
                    int next = prev.Count + 1;
                    var until = next >= AuthMaxFailures ? now.Add(AuthBanDuration) : DateTime.MinValue;
                    if (next == AuthMaxFailures)
                        _log.Warn($"Remote agent banning peer {addr} for {(int)AuthBanDuration.TotalSeconds}s after {AuthMaxFailures} auth failures.");
                    return (next, until);
                });
        }

        private void ClearAuthFailure(IPAddress? addr)
        {
            if (addr != null) _authFailures.TryRemove(addr, out _);
        }

        private static void ConfigureClientSocket(Socket sock)
        {
            sock.NoDelay = true;
            sock.ReceiveBufferSize = RemoteProto.MaxChunkBytes;
            sock.SendBufferSize = RemoteProto.MaxChunkBytes;
            sock.ReceiveTimeout = 15000;
            sock.SendTimeout = 30000;
        }

        private void HandleClient(int id, Socket sock, CancellationToken ct)
        {
            Stream? stream = null;
            Socket? rawSock = null;
            IPAddress? peerAddr = (sock.RemoteEndPoint as IPEndPoint)?.Address;
            if (peerAddr != null && peerAddr.IsIPv4MappedToIPv6) peerAddr = peerAddr.MapToIPv4();
            try
            {
                ConfigureClientSocket(sock);

                var net = new NetworkStream(sock, ownsSocket: true);
                if (_useTls && _cert != null)
                {
                    stream = RemoteProto.WrapServerTls(net, _cert);
                }
                else
                {
                    stream = net;
                    rawSock = sock;
                }

                if (_psk.Length > 0)
                {
                    try
                    {
                        RemoteProto.AuthServer(stream, rawSock, _psk);
                        ClearAuthFailure(peerAddr);
                    }
                    catch (AuthenticationException)
                    {
                        RecordAuthFailure(peerAddr);
                        throw;
                    }
                }

                sock.ReceiveTimeout = 60000;
                ClientLoop(id, stream, rawSock, ct);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                RecordAuthFailure(peerAddr);
                _log.Warn($"Remote client #{id} security/frame violation: {ex.Message}");
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is OperationCanceledException)
            {
                _log.Debug($"Remote client #{id} disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Remote client #{id} error: {ex.Message}");
            }
            finally
            {
                _subscribers.TryRemove(id, out _);
                _subscriberSocks.TryRemove(id, out _);
                try { stream?.Dispose(); } catch { }
                CloseQuiet(sock);
                Interlocked.Decrement(ref _activeClients);
            }
        }

        private void ClientLoop(int id, Stream stream, Socket? rawSock, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, payload) = RemoteProto.ReadFrame(stream);
                switch ((RemoteOp)op)
                {
                    case RemoteOp.List:
                        HandleList(stream, rawSock);
                        break;
                    case RemoteOp.Stat:
                        HandleStat(stream, rawSock, payload);
                        break;
                    case RemoteOp.ReadChunk:
                        HandleReadChunk(stream, rawSock, payload);
                        break;
                    case RemoteOp.ReadFile:
                        HandleReadFile(stream, rawSock, payload);
                        break;
                    case RemoteOp.Hash:
                        HandleHash(stream, rawSock, payload);
                        break;
                    case RemoteOp.HashPrefix:
                        HandleHashPrefix(stream, rawSock, payload);
                        break;
                    case RemoteOp.Subscribe:
                        _subscribers[id] = stream;
                        _subscriberSocks[id] = rawSock;
                        sockKeepAlive(stream);
                        RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok);
                        WaitUntilClosed(ct);
                        return;
                    default:
                        SendError(stream, rawSock, "Unsupported remote opcode.");
                        break;
                }
            }
        }

        private static void sockKeepAlive(Stream stream)
        {
            if (stream is NetworkStream ns)
                ns.Socket.ReceiveTimeout = Timeout.Infinite;
        }

        private static void WaitUntilClosed(CancellationToken ct)
        {
            ct.WaitHandle.WaitOne();
        }

        private void HandleList(Stream stream, Socket? rawSock)
        {
            var buf = new MemoryStream(64 * 1024);
            var hdr = new byte[5];
            buf.Write(hdr, 0, 5);
            uint count = 0;
            Span<byte> entry = stackalloc byte[20];

            foreach (var full in Directory.EnumerateFiles(_rootFull, "*", SearchOption.AllDirectories))
            {
                if (IsReparseEscape(full)) continue;
                FileInfo fi;
                try { fi = new FileInfo(full); if (!fi.Exists) continue; }
                catch { continue; }

                var rel = Path.GetRelativePath(_rootFull, full).Replace('\\', '/');
                var relBytes = Encoding.UTF8.GetBytes(rel);
                if (relBytes.Length > ushort.MaxValue) continue;

                BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(0, 2), (ushort)relBytes.Length);
                buf.Write(entry.Slice(0, 2));
                buf.Write(relBytes, 0, relBytes.Length);
                BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(0, 8), fi.Length);
                BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(8, 8), fi.LastWriteTimeUtc.Ticks);
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(16, 4), (int)fi.Attributes);
                buf.Write(entry);
                count++;

                if (buf.Length >= RemoteProto.MaxChunkBytes)
                {
                    FlushListBatch(stream, rawSock, buf, count, hasMore: true);
                    buf.SetLength(5);
                    count = 0;
                }
            }

            FlushListBatch(stream, rawSock, buf, count, hasMore: false);
        }

        private static void FlushListBatch(Stream stream, Socket? rawSock, MemoryStream buf, uint count, bool hasMore)
        {
            var data = buf.GetBuffer();
            data[0] = hasMore ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1, 4), count);
            RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, part2: data, part2Offset: 0, part2Count: (int)buf.Length);
        }

        private void HandleStat(Stream stream, Socket? rawSock, byte[] payload)
        {
            var rel = Encoding.UTF8.GetString(payload);
            var full = ResolveJailedPath(rel);
            if (!FileSnapshot.TryRead(full, out var snap))
            {
                RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, new byte[] { 0 });
                return;
            }

            var resp = new byte[1 + 8 + 8 + 4];
            resp[0] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(1, 8), snap.Length);
            BinaryPrimitives.WriteInt64LittleEndian(resp.AsSpan(9, 8), snap.LastWriteTimeUtcTicks);
            BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(17, 4), (int)snap.Attributes);
            RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, resp);
        }

        private void HandleReadChunk(Stream stream, Socket? rawSock, byte[] payload)
        {
            if (payload.Length < 14)
            {
                SendError(stream, rawSock, "Malformed read request.");
                return;
            }

            long offset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
            int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
            ushort pathLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2));
            if (offset < 0 || count <= 0 || count > RemoteProto.MaxChunkBytes || 14 + pathLen != payload.Length)
            {
                SendError(stream, rawSock, "Invalid read range.");
                return;
            }

            var rel = Encoding.UTF8.GetString(payload, 14, pathLen);
            var full = ResolveJailedPath(rel);

            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                int totalRead = 0;
                using (var fs = _accessor.OpenRead(full))
                {
                    if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
                    while (totalRead < count)
                    {
                        int r = fs.Read(rented, totalRead, count - totalRead);
                        if (r <= 0) break;
                        totalRead += r;
                    }
                }

                var lenBuf = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, totalRead);
                RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, lenBuf, rented, 0, totalRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void HandleReadFile(Stream stream, Socket? rawSock, byte[] payload)
        {
            if (payload.Length < 10)
            {
                SendError(stream, rawSock, "Malformed read-file request.");
                return;
            }

            long offset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
            ushort pathLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
            if (offset < 0 || 10 + pathLen != payload.Length)
            {
                SendError(stream, rawSock, "Invalid read-file request.");
                return;
            }

            var rel = Encoding.UTF8.GetString(payload, 10, pathLen);
            var full = ResolveJailedPath(rel);

            var rented = ArrayPool<byte>.Shared.Rent(RemoteProto.MaxChunkBytes);
            try
            {
                using var fs = _accessor.OpenRead(full);
                if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
                while (true)
                {
                    int read = fs.Read(rented, 0, RemoteProto.MaxChunkBytes);
                    if (read <= 0) break;
                    RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, part2: rented, part2Offset: 0, part2Count: read);
                }
                // Empty OP_OK frame marks EOF for the streaming transfer.
                RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void HandleHash(Stream stream, Socket? rawSock, byte[] payload)
        {
            var rel = Encoding.UTF8.GetString(payload);
            var full = ResolveJailedPath(rel);
            string hex;
            using (var fs = _accessor.OpenRead(full))
            {
                hex = Convert.ToHexStringLower(SHA256.HashData(fs));
            }
            var bytes = Encoding.UTF8.GetBytes(hex);
            RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, bytes);
        }

        private void HandleHashPrefix(Stream stream, Socket? rawSock, byte[] payload)
        {
            if (payload.Length < 10)
            {
                SendError(stream, rawSock, "Malformed hash-prefix request.");
                return;
            }
            long prefixLen = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
            ushort pathLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
            if (prefixLen < 0 || 10 + pathLen != payload.Length)
            {
                SendError(stream, rawSock, "Invalid hash-prefix request.");
                return;
            }
            var rel = Encoding.UTF8.GetString(payload, 10, pathLen);
            var full = ResolveJailedPath(rel);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                using var fs = _accessor.OpenRead(full);
                long remaining = prefixLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int r = fs.Read(buf, 0, toRead);
                    if (r <= 0) break;
                    sha.AppendData(buf, 0, r);
                    remaining -= r;
                }
                if (remaining > 0)
                {
                    SendError(stream, rawSock, "Source shorter than requested prefix.");
                    return;
                }
                var hex = Convert.ToHexStringLower(sha.GetHashAndReset());
                RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Ok, Encoding.UTF8.GetBytes(hex));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private static void SendError(Stream stream, Socket? rawSock, string msg)
        {
            RemoteProto.SendFrame(stream, rawSock, (uint)RemoteOp.Error, Encoding.UTF8.GetBytes(msg));
        }

        private void OnRootChanged(object? sender, FileChangedEventArgs e)
        {
            if (_subscribers.IsEmpty) return;
            if (!TryMakeRelative(e.FullPath, out var rel)) return;
            string? oldRel = null;
            if (e.OldFullPath != null && !TryMakeRelative(e.OldFullPath, out oldRel))
                oldRel = null;

            var relBytes = Encoding.UTF8.GetBytes(rel);
            var oldBytes = oldRel != null ? Encoding.UTF8.GetBytes(oldRel) : Array.Empty<byte>();
            if (relBytes.Length > ushort.MaxValue || oldBytes.Length > ushort.MaxValue) return;

            var msg = new byte[1 + 2 + relBytes.Length + 2 + oldBytes.Length];
            msg[0] = (byte)e.ChangeType;
            BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(1, 2), (ushort)relBytes.Length);
            Buffer.BlockCopy(relBytes, 0, msg, 3, relBytes.Length);
            int off = 3 + relBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(off, 2), (ushort)oldBytes.Length);
            if (oldBytes.Length > 0)
                Buffer.BlockCopy(oldBytes, 0, msg, off + 2, oldBytes.Length);

            lock (_subSendLock)
            {
                foreach (var kv in _subscribers)
                {
                    try
                    {
                        _subscriberSocks.TryGetValue(kv.Key, out var rawSock);
                        RemoteProto.SendFrame(kv.Value, rawSock, (uint)RemoteOp.ChangeEvent, msg);
                    }
                    catch
                    {
                        _subscribers.TryRemove(kv.Key, out _);
                        _subscriberSocks.TryRemove(kv.Key, out _);
                    }
                }
            }
        }

        private bool TryMakeRelative(string fullPath, out string rel)
        {
            var norm = Path.GetFullPath(fullPath);
            if (!IsInsideRoot(norm))
            {
                rel = string.Empty;
                return false;
            }
            rel = Path.GetRelativePath(_rootFull, norm).Replace('\\', '/');
            return !string.IsNullOrEmpty(rel) && rel != ".";
        }

        public string ResolveJailedPath(string relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath) || relPath.IndexOf('\0') >= 0)
                throw new UnauthorizedAccessException("Empty or null remote path.");
            if (relPath.StartsWith("\\\\", StringComparison.Ordinal) || relPath.Contains(':', StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Device/UNC/ADS paths are forbidden.");
            if (Path.IsPathRooted(relPath))
                throw new UnauthorizedAccessException("Rooted remote path forbidden.");

            var normalized = relPath.Replace('\\', '/').TrimStart('/');

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                if (seg == "..")
                    throw new UnauthorizedAccessException("Parent traversal forbidden.");
            }

            var candidate = Path.GetFullPath(Path.Combine(_rootFull, Path.Combine(segments)));
            if (!IsInsideRoot(candidate))
                throw new UnauthorizedAccessException("Path escapes remote root.");

            if (IsReparseEscape(candidate))
                throw new UnauthorizedAccessException("Symlink/reparse escape forbidden.");

            return candidate;
        }

        private bool IsInsideRoot(string fullCandidate)
        {
            if (fullCandidate.Equals(_rootFull, RootCmp))
                return true;
            return fullCandidate.Length > _rootFull.Length
                && fullCandidate.StartsWith(_rootFull, RootCmp)
                && (fullCandidate[_rootFull.Length] == Path.DirectorySeparatorChar
                    || fullCandidate[_rootFull.Length] == Path.AltDirectorySeparatorChar);
        }

        private bool IsReparseEscape(string fullPath)
        {
            var cur = _rootFull;
            var rel = Path.GetRelativePath(_rootFull, fullPath);
            if (rel == ".") return false;
            foreach (var seg in rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                cur = Path.Combine(cur, seg);
                try
                {
                    var attr = File.GetAttributes(cur);
                    if ((attr & FileAttributes.ReparsePoint) != FileAttributes.None)
                    {
                        var target = Directory.Exists(cur)
                            ? new DirectoryInfo(cur).ResolveLinkTarget(true)?.FullName
                            : new FileInfo(cur).ResolveLinkTarget(true)?.FullName;
                        if (target == null || !IsInsideRoot(Path.GetFullPath(target)))
                            return true;
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return false;
        }

        internal static (IPAddress Ip, int Port) ParseEndpoint(string endpoint)
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || colon == endpoint.Length - 1)
                throw new ArgumentException("Endpoint must be host:port.", nameof(endpoint));
            var host = endpoint.Substring(0, colon).Trim('[', ']');
            if (!int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 0 || port > 65535)
                throw new ArgumentException("Invalid port in endpoint: " + endpoint, nameof(endpoint));
            var ip = host == "*" || host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(host);
            return (ip, port);
        }

        private static void CloseQuiet(Socket s)
        {
            try { s.Shutdown(SocketShutdown.Both); } catch { }
            try { s.Dispose(); } catch { }
        }
    }
}
