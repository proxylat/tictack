using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    public sealed record SnapshotMountOptions(string? ParentDir = null);

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
