using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TicTack
{
    public class FileWatcherMonitor : IFileMonitor
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadDirectoryChangesW(IntPtr hDirectory,
            byte[] lpBuffer, uint nBufferLength, bool bWatchSubtree,
            uint dwNotifyFilter, out uint lpBytesReturned,
            IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // Cancels a blocked synchronous ReadDirectoryChangesW issued from the worker
        // thread. Best-effort: CloseHandle can stall while a filter driver holds the
        // pending IRP, so the read must be cancelled before the handle is closed.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        private const uint FileListDirectory = 0x0001;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint NotifyFileName = 0x00000001;
        private const uint NotifyDirName = 0x00000002;
        private const uint NotifySize = 0x00000008;
        private const uint NotifyLastWrite = 0x00000010;
        private const int ActionAdded = 1;
        private const int ActionRemoved = 2;
        private const int ActionModified = 3;
        private const int ActionRenamedOld = 4;
        private const int ActionRenamedNew = 5;

        private IntPtr _dirHandle;
        private Thread? _worker;
        private volatile bool _stopping;
        private string? _pendingRename;
        private readonly ManualResetEventSlim _armed = new(false);
        private readonly string _path;
        private readonly int _bufferSize;
        private readonly int _restartDelaySec;

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public FileWatcherMonitor(string path, int bufferKb = 256, int restartDelaySec = 10)
        {
            _path = path;
            _bufferSize = Math.Max(4, bufferKb) * 1024;
            _restartDelaySec = restartDelaySec;
        }

        public void Start()
        {
            Stop();
            _stopping = false;
            _armed.Reset();
            _worker = new Thread(WorkerLoop) { IsBackground = true };
            _worker.Start();
            // Do not return until the first ReadDirectoryChangesW is armed:
            // a change written before then is not buffered and is lost. The
            // worker also signals on the open-failure and retry paths, so this
            // is bounded even when the directory cannot be opened.
            _armed.Wait(TimeSpan.FromSeconds(10));
        }

        private void WorkerLoop()
        {
            while (!_stopping)
            {
                try
                {
                    _dirHandle = CreateFile(_path, FileListDirectory,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

                    if (_dirHandle == new IntPtr(-1))
                    {
                        _armed.Set();
                        FireError(new IOException("Failed to open directory: " + _path));
                        SleepOrStop(5000);
                        continue;
                    }

                    // Stop() may already have run before the handle existed (it then
                    // closed nothing). Exit now instead of blocking in the read below
                    // with a handle nobody will close; finally releases it.
                    if (_stopping)
                        break;

                    // Signal immediately before the first read: the kernel starts
                    // buffering directory changes only once this call is issued.
                    _armed.Set();
                    var buf = new byte[_bufferSize];
                    while (!_stopping)
                    {
                        uint ret;
                        if (!ReadDirectoryChangesW(_dirHandle, buf, (uint)buf.Length, true,
                                NotifyFileName | NotifyDirName | NotifySize | NotifyLastWrite,
                                out ret, IntPtr.Zero, IntPtr.Zero))
                        {
                            var err = Marshal.GetLastWin32Error();
                            if (err == 6 || err == 995) break;
                            FireError(new IOException("ReadDirectoryChangesW error " + err));
                            break;
                        }
                        if (ret == 0)
                        {
                            FireError(new IOException("ReadDirectoryChangesW buffer overflow; rescanning"));
                            Rescan();
                            break;
                        }
                        ParseEvents(buf, ret);
                    }
                }
                catch (Exception ex) when (!_stopping)
                {
                    _armed.Set();
                    FireError(ex);
                }
                finally
                {
                    // Exactly-once close: Stop() may race us here; whoever wins
                    // the exchange owns the handle, preventing double-close or
                    // closing a recycled handle value owned by someone else.
                    var h = Interlocked.Exchange(ref _dirHandle, IntPtr.Zero);
                    if (h != IntPtr.Zero && h != new IntPtr(-1))
                        CloseHandle(h);
                    FlushRename();
                }
                SleepOrStop(_restartDelaySec * 1000);
            }
        }

        private void ParseEvents(byte[] buf, uint len)
        {
            var offset = 0;
            while (offset + 12 <= len)
            {
                var next = BitConverter.ToInt32(buf, offset);
                var action = BitConverter.ToInt32(buf, offset + 4);
                var nameLen = BitConverter.ToInt32(buf, offset + 8);
                if (nameLen < 0 || offset + 12 + nameLen > len)
                {
                    FireError(new IOException("Malformed ReadDirectoryChangesW event"));
                    break;
                }
                var name = Encoding.Unicode.GetString(buf, offset + 12, nameLen);
                var full = Path.Combine(_path, name);

                switch (action)
                {
                    case ActionAdded:   FlushRename(); FireChanged(ChangeType.Created, full); break;
                    case ActionRemoved: FlushRename(); FireChanged(ChangeType.Deleted, full); break;
                    case ActionModified: FlushRename(); FireChanged(ChangeType.Modified, full); break;
                    case ActionRenamedOld: _pendingRename = full; break;
                    case ActionRenamedNew:
                        if (_pendingRename != null)
                        {
                            FireChanged(ChangeType.Renamed, full, _pendingRename);
                            _pendingRename = null;
                        }
                        else FireChanged(ChangeType.Created, full);
                        break;
                }
                if (next == 0) break;
                if (next < 12 || offset + next > len)
                {
                    FireError(new IOException("Malformed ReadDirectoryChangesW event chain"));
                    break;
                }
                offset += next;
            }
        }

        internal void RescanNow() => Rescan();

        private void Rescan() =>
            MonitorRescan.FireModifiedFiles(_path, FireError, (t, p) => FireChanged(t, p));

        private void FlushRename()
        {
            if (_pendingRename != null)
            {
                FireChanged(ChangeType.Deleted, _pendingRename);
                _pendingRename = null;
            }
        }

        private void FireChanged(ChangeType type, string path, string? oldPath = null)
        {
            var h = Changed;
            if (h != null)
            {
                try { h(this, new FileChangedEventArgs(type, path, oldPath)); }
                catch (Exception ex) { FireError(ex); }
            }
        }

        private void FireError(Exception ex)
        {
            var h = Error;
            if (h != null)
            {
                try { h(this, new MonitorErrorEventArgs(ex)); }
                catch { }
            }
        }

        private void SleepOrStop(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!_stopping && DateTime.UtcNow < end)
                Thread.Sleep(100);
        }

        public void Stop()
        {
            _stopping = true;
            var h = Interlocked.Exchange(ref _dirHandle, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                // Cancel the worker's blocked read first: CloseHandle alone can stall
                // while a filter driver holds the pending directory-change IRP.
                try { CancelIoEx(h, IntPtr.Zero); } catch { }
                CloseHandle(h);
            }
            if (_worker != null && _worker.IsAlive)
            {
                // CancelIoEx above is the mechanism that unblocks the worker;
                // Thread.Interrupt cannot interrupt native ReadDirectoryChangesW
                // and would leave a pending interrupt behind.
                _worker.Join(3000);
                _worker = null;
            }
        }

        // Intentionally does not dispose _armed: the worker can still signal it
        // on a retry path after Stop()'s bounded join, and Set() on a disposed
        // event would fault the background thread.
        public void Dispose() => Stop();
    }

    // Shared by the two watcher implementations: report every existing file as
    // Modified so a missed event or overflow is recovered by a normal compare.
    internal static class MonitorRescan
    {
        internal static void FireModifiedFiles(string path, Action<Exception> onError, Action<ChangeType, string> fire)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    fire(ChangeType.Modified, file);
            }
            catch (Exception ex) { onError(ex); }
        }
    }

    public class PollingMonitor : IFileMonitor
    {
        private readonly string _path;
        private readonly int _intervalSec;
        private Timer? _timer;
        private Dictionary<string, FileSnapshot>? _snapshot;
        // Scratch dictionary reused across scans so periodic polling does not
        // allocate (and later discard) two full per-file dictionaries per tick.
        // Both dictionaries keep their capacity; detection logic is unchanged.
        private Dictionary<string, FileSnapshot>? _scratch;
        private readonly string _prefix;
        private int _scanning;

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public PollingMonitor(string path, int intervalSec = 300)
        {
            _path = path;
            _intervalSec = Math.Max(10, intervalSec);
            _prefix = _path.EndsWith(Path.DirectorySeparatorChar) ? _path : _path + Path.DirectorySeparatorChar;
        }

        public void Start()
        {
            Stop();
            _timer = new Timer(_ => Poll(), null, _intervalSec * 1000, _intervalSec * 1000);
        }

        internal void PollNow() => Poll();

        private void Poll()
        {
            // Timer callbacks can overlap when a scan outruns the interval;
            // skip the tick instead of mutating the shared dictionaries twice.
            if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
            try
            {
                if (_snapshot == null) { _snapshot = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal); ScanInto(_snapshot, null); return; }
                _scratch ??= new Dictionary<string, FileSnapshot>(_snapshot.Count, StringComparer.Ordinal);
                _scratch.Clear();
                var prev = _snapshot;
                ScanInto(_scratch, prev);
                var current = _scratch;

                foreach (var kv in current)
                {
                    if (!prev.TryGetValue(kv.Key, out var oldSnap))
                        FireChanged(ChangeType.Created, _prefix + kv.Key);
                    else if (!oldSnap.Equals(kv.Value))
                        FireChanged(ChangeType.Modified, _prefix + kv.Key);
                }
                foreach (var kv in prev)
                {
                    if (!current.ContainsKey(kv.Key))
                        FireChanged(ChangeType.Deleted, _prefix + kv.Key);
                }

                _snapshot = current;
                _scratch = prev;
            }
            catch (Exception ex) { FireError(ex); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        }

        private void FireChanged(ChangeType type, string path)
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(this, new FileChangedEventArgs(type, path)); }
            catch (Exception ex) { FireError(ex); }
        }

        private void FireError(Exception ex)
        {
            var handler = Error;
            if (handler == null) return;
            try { handler(this, new MonitorErrorEventArgs(ex)); }
            catch { }
        }

        private void ScanInto(Dictionary<string, FileSnapshot> result, Dictionary<string, FileSnapshot>? previous)
        {
            if (!Directory.Exists(_path)) return;
            foreach (var f in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories))
            {
                var key = f.Substring(_prefix.Length);
                try
                {
                    var info = new FileInfo(f);
                    result[key] = new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks, info.Attributes);
                }
                catch
                {
                    // A failed stat must not look like a deletion: carry the
                    // previous snapshot forward so no spurious Deleted event
                    // can reach the delete guard.
                    if (previous != null && previous.TryGetValue(key, out var prev))
                        result[key] = prev;
                }
            }
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

    }

    public class CompositeMonitor : IFileMonitor
    {
        private readonly IFileMonitor[] _monitors;
        private readonly List<(IFileMonitor Monitor, EventHandler<FileChangedEventArgs> Changed, EventHandler<MonitorErrorEventArgs> Error)> _wired = new();

        public CompositeMonitor(params IFileMonitor[] monitors)
        {
            _monitors = monitors ?? Array.Empty<IFileMonitor>();
        }

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public void Start()
        {
            // Idempotent: re-subscribing forwarding lambdas on a second Start
            // would double-deliver every child event.
            Stop();
            foreach (var m in _monitors)
            {
                EventHandler<FileChangedEventArgs> changed = (s, e) => Changed?.Invoke(s, e);
                EventHandler<MonitorErrorEventArgs> error = (s, e) => Error?.Invoke(s, e);
                m.Changed += changed;
                m.Error += error;
                _wired.Add((m, changed, error));
                m.Start();
            }
        }

        public void Stop()
        {
            foreach (var (m, changed, error) in _wired)
            {
                m.Changed -= changed;
                m.Error -= error;
            }
            _wired.Clear();
            foreach (var m in _monitors) m.Stop();
        }

        public void Dispose()
        {
            foreach (var m in _monitors) m.Dispose();
        }
    }

    // Cross-platform watcher (System.IO.FileSystemWatcher) — used on non-Windows where
    // FileWatcherMonitor's ReadDirectoryChangesW P/Invoke is unavailable.
    public class FsWatchMonitor : IFileMonitor
    {
        private readonly string _path;
        private readonly int _bufferSize;
        private readonly int _restartDelaySec;
        private FileSystemWatcher? _watcher;
        private volatile bool _stopping;
        private readonly object _gate = new();

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public FsWatchMonitor(string path, int bufferKb = 256, int restartDelaySec = 10)
        {
            _path = path;
            _bufferSize = Math.Max(32, bufferKb) * 1024;
            _restartDelaySec = Math.Max(1, restartDelaySec);
        }

        public void Start()
        {
            lock (_gate) { _stopping = false; }
            StartWatcher();
        }

        private void StartWatcher()
        {
            lock (_gate)
            {
                // Stop() may have won the race while the restart thread slept.
                if (_stopping) return;
                var watcher = new FileSystemWatcher(_path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = _bufferSize,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                watcher.Created += (s, e) => FireChanged(ChangeType.Created, e.FullPath);
                watcher.Changed += (s, e) => FireChanged(ChangeType.Modified, e.FullPath);
                watcher.Deleted += (s, e) => FireChanged(ChangeType.Deleted, e.FullPath);
                watcher.Renamed += (s, e) => FireChanged(ChangeType.Renamed, e.FullPath, e.OldFullPath);
                watcher.Error += (s, e) =>
                {
                    FireError(e.GetException());
                    if (!_stopping)
                    {
                        var t = new Thread(() =>
                        {
                            try { watcher.Dispose(); } catch { }
                            Rescan();
                            while (!_stopping)
                            {
                                Thread.Sleep(_restartDelaySec * 1000);
                                if (_stopping) return;
                                try { StartWatcher(); return; }
                                catch (Exception ex) { FireError(ex); }
                            }
                        });
                        t.IsBackground = true;
                        t.Start();
                    }
                };
                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
        }

        internal void RescanNow() => Rescan();

        private void Rescan() =>
            MonitorRescan.FireModifiedFiles(_path, FireError, (t, p) => FireChanged(t, p));

        private void FireChanged(ChangeType type, string path, string? oldPath = null)
        {
            if (_stopping) return;
            var handler = Changed;
            if (handler != null)
            {
                // Contain a throwing subscriber the same way FileWatcherMonitor
                // does, so one bad handler cannot abort dispatch.
                try { handler(this, new FileChangedEventArgs(type, path, oldPath)); }
                catch (Exception ex) { FireError(ex); }
            }
        }

        private void FireError(Exception ex)
        {
            var handler = Error;
            if (handler != null)
            {
                try { handler(this, new MonitorErrorEventArgs(ex)); }
                catch { }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopping = true;
                try { _watcher?.EnableRaisingEvents = false; } catch { }
                try { _watcher?.Dispose(); } catch { }
                _watcher = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
