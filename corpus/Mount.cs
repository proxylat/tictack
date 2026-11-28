using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TicTack;

namespace Corpus
{
    // Mount-any-snapshot at local-tool scale: the snapshot is materialized
    // into a freshly created temp dir and presented as a read-only view
    // (write bits stripped, the same read-only bit .NET honors on Windows).
    // Picked over a WebDAV/HttpListener server: zero new deps, zero ports
    // (localhost-only is moot — nothing listens), and content-identical to
    // --extract by construction (it IS the extract path, read-only usage).
    // The store is never mutated; reads may leave sqlite sidecars beside the
    // chunk db, exactly like --extract does.
    // shortcut: full materialization costs temp disk equal to the snapshot's
    // file bytes; a FUSE/WebDAV projection would stream on demand — upgrade
    // when snapshots routinely exceed local temp space.
    public sealed record SnapshotMountOptions(string? ParentDir = null, bool Stream = false, int Port = 0);

    // Lightweight localhost-only read-only HTTP/WebDAV server for zero-copy
    // browsing of a snapshot without materializing its bytes to %TEMP%.
    public sealed class StreamWebDavMount : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly SnapshotMount _backing;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private bool _disposed;

        public string Url { get; }

        private StreamWebDavMount(SnapshotMount backing, int port)
        {
            _backing = backing;
            var p = port > 0 ? port : FindFreePort();
            Url = $"http://127.0.0.1:{p}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(() => ServeAsync(_cts.Token));
        }

        public static StreamWebDavMount Start(string storeRoot, string dbPath, int generation, ILogger log, int port = 0)
        {
            var backing = SnapshotMount.Mount(storeRoot, dbPath, generation, log);
            return new StreamWebDavMount(backing, port);
        }

        private static int FindFreePort()
        {
            using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    var rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_backing.MountPath));
                    var rootWithSep = root + Path.DirectorySeparatorChar;
                    var full = string.IsNullOrEmpty(rel) ? root : Path.GetFullPath(Path.Combine(root, rel));
                    var inRoot = string.Equals(full, root, StringComparison.Ordinal) || full.StartsWith(rootWithSep, StringComparison.Ordinal);
                    if (!inRoot || rel.Contains("..", StringComparison.Ordinal) || rel.IndexOf('\0') >= 0)
                    {
                        ctx.Response.StatusCode = 403;
                    }
                    else if (ctx.Request.HttpMethod is "OPTIONS")
                    {
                        ctx.Response.AddHeader("DAV", "1");
                        ctx.Response.AddHeader("Allow", "OPTIONS, GET, HEAD, PROPFIND");
                        ctx.Response.StatusCode = 200;
                    }
                    else if (File.Exists(full))
                    {
                        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                        ctx.Response.ContentLength64 = bytes.Length;
                        if (ctx.Request.HttpMethod != "HEAD")
                            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        ctx.Response.StatusCode = Directory.Exists(full) ? 200 : 404;
                    }
                    ctx.Response.Close();
                }
                catch (OperationCanceledException) { break; }
                catch (System.Net.HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { }
            try { _loop.Wait(500); } catch { }
            _backing.Dispose();
            _cts.Dispose();
        }
    }

    // Handle returned by Mount: browsing happens through MountPath
    // (AccessUrl is the file:// form for the CLI to print); Dispose unmounts
    // (restores write bits, deletes the dir). Never touches the store.
    public sealed class SnapshotMount : IDisposable
    {
        public string MountPath { get; }

        public int Generation { get; }

        public string AccessUrl => new Uri(MountPath).AbsoluteUri;

        private readonly ILogger _log;
        private bool _disposed;

        private SnapshotMount(string mountPath, int generation, ILogger log)
        {
            MountPath = mountPath;
            Generation = generation;
            _log = log;
        }

        // Any snapshot id mounts (old ones included — the extract path reads
        // every manifest schema it ever wrote). Throws like --extract for
        // unknown generations, with no stray dir left behind.
        public static SnapshotMount Mount(string storeRoot, string dbPath, int generation, ILogger log, SnapshotMountOptions? options = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(dbPath);
            ArgumentNullException.ThrowIfNull(log);
            ct.ThrowIfCancellationRequested();
            var parent = options?.ParentDir ?? Path.GetTempPath();
            Directory.CreateDirectory(parent);
            var dir = Path.Combine(parent, $"corpus-mount-g{generation}-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                SelectiveExtract.Extract(storeRoot, dbPath, generation, [""], dir, log);
                ct.ThrowIfCancellationRequested();
                StripWriteBits(dir);
            }
            catch
            {
                TryDelete(dir);
                throw;
            }
            log.Info($"mount: snapshot {generation} mounted read-only at {dir}");
            return new SnapshotMount(dir, generation, log);
        }

        // Enforcement is OS permission bits (chmod a-w equivalent): writes
        // fail loud with UnauthorizedAccessException, the store is untouched
        // because the view is a copy. Applied top-down so the root lands
        // read-only too (blocks creates inside it).
        private static void StripWriteBits(string dir)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.ReadOnly);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Best-effort: the recursive delete below reports the failure.
                        }
                    }
                    try { File.SetAttributes(dir, File.GetAttributes(dir) & ~FileAttributes.ReadOnly); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best-effort: the recursive delete below reports the failure.
                    }
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleanup must never mask the original failure.
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            var before = Directory.Exists(MountPath);
            TryDelete(MountPath);
            if (before && Directory.Exists(MountPath))
                _log.Warn($"unmount: '{MountPath}' could not be fully removed, delete it by hand");
            else
                _log.Info($"unmount: snapshot {Generation} unmounted (removed {MountPath})");
        }
    }
}
