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

    // USN-journal monitor (Windows/NTFS only): reads the volume's update
    // sequence number journal instead of watching directories. The journal is
    // a persistent on-disk cursor — no watcher-buffer overruns, and nothing
    // is missed between Start and Stop. Crash/downtime gaps are NOT covered
    // by the cursor (restart re-baselines at the current NextUsn); they are
    // covered by InitialSync, which reconciles every source->dest difference
    // on startup. Pair with composite mode when the gap matters live.
    public class UsnJournalMonitor : IFileMonitor
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            [Out] byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileById(IntPtr hVolumeHint, ref FileIdDescriptor lpFileId,
            uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath,
            uint cchFilePath, uint dwFlags);

        // FILE_ID_DESCRIPTOR with Type = 0 (raw 64-bit file reference number,
        // zero-extended). Explicit padding keeps the native layout exact.
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdDescriptor
        {
            public int Size;
            private int _pad1;
            public int Type;
            private int _pad2;
            public long FileId;
        }

        // USN_JOURNAL_DATA_V0/V1 prefix: only the journal id and the head
        // cursor are needed, so the longer V1 tail is never read.
        [StructLayout(LayoutKind.Sequential)]
        private struct UsnJournalData
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
        }

        // READ_USN_JOURNAL_DATA_V0 input: StartUsn is exclusive, Timeout
        // bounds the block so Stop() never waits out a full interval.
        [StructLayout(LayoutKind.Sequential)]
        private struct ReadUsnInput
        {
            public long StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        private const uint FsctlQueryUsnJournal = 0x000900f4;
        private const uint FsctlReadUsnJournal = 0x000900fb;
        private const uint FileReadAttributes = 0x0080;
        private const uint FileShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileAttributeDirectory = 0x00000010;

        private const uint ReasonFileCreate = 0x00000100;
        private const uint ReasonFileDelete = 0x00000200;
        private const uint ReasonRenameOldName = 0x00001000;
        private const uint ReasonRenameNewName = 0x00002000;
        private const uint ReasonClose = 0x80000000;
        // Any content or metadata change worth re-syncing. CLOSE alone is
        // excluded: every record carries it, and alone it means nothing
        // changed. The pipeline re-validates via the comparer anyway, so an
        // over-broad Modified is a wasted stat, never a wrong copy.
        private const uint ReasonModifyMask = 0x00000001 | 0x00000002 | 0x00000004
            | 0x00000010 | 0x00000020 | 0x00000040 | 0x00000400 | 0x00000800
            | 0x00004000 | 0x00008000 | 0x00010000 | 0x00020000 | 0x00040000
            | 0x00080000 | 0x00100000 | 0x00200000 | 0x00400000 | 0x00800000;

        // Parsed USN_RECORD_V2/V3 common prefix (both versions share these
        // field offsets; the V3-only tail past FileNameOffset is ignored).
        internal sealed class UsnRecord
        {
            public ulong FileRef;
            public ulong ParentRef;
            public long Usn;
            public uint Reason;
            public uint Attributes;
            public string FileName = string.Empty;
        }

        private static readonly IntPtr InvalidHandle = new(-1);

        private readonly string _path;
        private readonly string _prefix;
        private readonly int _restartDelaySec;
        private readonly string _volumeDevice;
        private Thread? _worker;
        private volatile bool _stopping;
        private IntPtr _volumeHandle = IntPtr.Zero;
        private readonly ManualResetEventSlim _armed = new(false);
        // Rename-old path by file reference number. A rename out of the
        // volume leaves an orphan entry; the cap bounds that leak.
        // ponytail: clear-all eviction, per-FRN LRU if renames ever dominate.
        private readonly Dictionary<ulong, string> _pendingOldNames = new();

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public UsnJournalMonitor(string path, int restartDelaySec = 10)
        {
            // No platform check here: translation and parsing are pure and
            // unit-tested cross-platform; ProbeVolume/Start throw on misuse.
            _path = Path.GetFullPath(path);
            _prefix = _path.EndsWith(Path.DirectorySeparatorChar) ? _path : _path + Path.DirectorySeparatorChar;
            _restartDelaySec = Math.Max(1, restartDelaySec);
            var root = Path.GetPathRoot(_path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new NotSupportedException("USN journal is per-volume; UNC paths are not supported: " + path);
            _volumeDevice = @"\\.\" + root.TrimEnd('\\');
        }

        // Opens the volume and reads the journal head. Called by the factory
        // before wiring the monitor so elevation failure falls back to the
        // watcher with a warning instead of failing startup. Also called by
        // Start for the late-start path.
        internal (ulong JournalId, long NextUsn) ProbeVolume()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("UsnJournalMonitor requires Windows/NTFS.");
            var handle = CreateFile(_volumeDevice, FileReadAttributes, FileShareAll,
                IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle == InvalidHandle)
            {
                var err = Marshal.GetLastWin32Error();
                throw new IOException("Cannot open volume " + _volumeDevice +
                    " (Win32 " + err + "); USN journal needs elevation — run as admin/SYSTEM or use watcher mode.");
            }
            try
            {
                return QueryJournal(handle);
            }
            finally { CloseHandle(handle); }
        }

        public void Start()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("UsnJournalMonitor requires Windows/NTFS.");
            Stop();
            _stopping = false;
            _armed.Reset();
            _worker = new Thread(WorkerLoop) { IsBackground = true };
            _worker.Start();
            // Same contract as FileWatcherMonitor: do not return until the
            // first read is armed, so no change slips through the gap.
            _armed.Wait(TimeSpan.FromSeconds(10));
        }

        private void WorkerLoop()
        {
            while (!_stopping)
            {
                try
                {
                    _volumeHandle = CreateFile(_volumeDevice, FileReadAttributes, FileShareAll,
                        IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                    if (_volumeHandle == InvalidHandle)
                    {
                        _armed.Set();
                        FireError(new IOException("Failed to open volume: " + _volumeDevice));
                        SleepOrStop(_restartDelaySec * 1000);
                        continue;
                    }
                    if (_stopping) break;

                    long lastUsn;
                    try { lastUsn = QueryJournal(_volumeHandle).NextUsn; }
                    catch (Exception ex)
                    {
                        _armed.Set();
                        FireError(ex);
                        SleepOrStop(_restartDelaySec * 1000);
                        continue;
                    }

                    _armed.Set();
                    var buf = new byte[64 * 1024];
                    while (!_stopping)
                    {
                        List<UsnRecord>? records = null;
                        try { records = ReadJournal(_volumeHandle, ref lastUsn, buf); }
                        catch (Exception ex)
                        {
                            // Journal recreated/deleted underneath us: the gap
                            // is real, so say so, then re-baseline at the new
                            // head. InitialSync (or composite's polling leg)
                            // covers the missed range.
                            FireError(ex);
                            break;
                        }
                        if (records != null)
                        {
                            foreach (var rec in records)
                            {
                                if (_stopping) break;
                                Dispatch(rec);
                            }
                        }
                    }
                }
                finally
                {
                    var h = _volumeHandle;
                    _volumeHandle = IntPtr.Zero;
                    if (h != IntPtr.Zero && h != InvalidHandle)
                        CloseHandle(h);
                }
                if (!_stopping) SleepOrStop(_restartDelaySec * 1000);
            }
        }

        private void Dispatch(UsnRecord rec)
        {
            var evt = TranslateRecord(rec, ResolvePath);
            if (evt != null) FireChanged(evt.ChangeType, evt.FullPath, evt.OldFullPath);
        }

        // Pure translation step, separated for testing: parse and rename
        // pairing without touching the volume.
        internal FileChangedEventArgs? TranslateRecord(UsnRecord rec, Func<ulong, string?> resolvePath)
        {
            bool isDir = (rec.Attributes & FileAttributeDirectory) != 0;
            ChangeType type;
            string? oldPath = null;
            if ((rec.Reason & ReasonFileDelete) != 0)
            {
                type = ChangeType.Deleted;
            }
            else if ((rec.Reason & ReasonRenameOldName) != 0)
            {
                var oldFull = UnderWatch(resolvePath(rec.FileRef) ?? CombineParent(rec, resolvePath));
                if (oldFull != null)
                {
                    if (_pendingOldNames.Count > 1024) _pendingOldNames.Clear();
                    _pendingOldNames[rec.FileRef] = oldFull;
                }
                return null;
            }
            else if ((rec.Reason & ReasonRenameNewName) != 0)
            {
                type = ChangeType.Renamed;
                if (!_pendingOldNames.Remove(rec.FileRef, out oldPath))
                {
                    // Rename into the watched tree (or a missed old-name):
                    // report as creation of the new name.
                    type = ChangeType.Created;
                }
            }
            else if ((rec.Reason & ReasonFileCreate) != 0)
            {
                type = ChangeType.Created;
            }
            else if ((rec.Reason & ReasonModifyMask) != 0)
            {
                // Directory mtime churns on every child change; the watcher
                // has the same noise, but USN parity only needs dir
                // create/delete, so skip dir-Modified here.
                if (isDir) return null;
                type = ChangeType.Modified;
            }
            else
            {
                return null;
            }

            string? full;
            if (type == ChangeType.Deleted)
            {
                // Deleted files cannot be opened by id; resolve through the
                // (usually surviving) parent directory instead.
                full = UnderWatch(CombineParent(rec, resolvePath));
            }
            else
            {
                full = UnderWatch(resolvePath(rec.FileRef));
            }
            if (full == null) return null;
            // A rename whose old path resolved outside the watched tree is a
            // creation as far as the destination is concerned.
            if (type == ChangeType.Renamed && (oldPath == null || UnderWatch(oldPath) == null))
                return new FileChangedEventArgs(ChangeType.Created, full);
            return new FileChangedEventArgs(type, full, oldPath);
        }

        private string? CombineParent(UsnRecord rec, Func<ulong, string?> resolvePath)
        {
            var parent = resolvePath(rec.ParentRef);
            if (parent == null) return null;
            return Path.Combine(parent, rec.FileName);
        }

        // Null unless the path is the watched root or inside it
        // (boundary-aware: /a/src must not match /a/src2/f).
        private string? UnderWatch(string? fullPath)
        {
            if (fullPath == null) return null;
            if (fullPath.Equals(_path, StringComparison.OrdinalIgnoreCase)) return fullPath;
            if (fullPath.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) return fullPath;
            return null;
        }

        internal string? ResolvePath(ulong fileRef)
        {
            var h = _volumeHandle;
            if (h == IntPtr.Zero || h == InvalidHandle) return null;
            var desc = new FileIdDescriptor { Size = 24, Type = 0, FileId = (long)fileRef };
            var fh = OpenFileById(h, ref desc, 0, FileShareAll, IntPtr.Zero, OpenExisting);
            if (fh == InvalidHandle || fh == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(512);
                var len = GetFinalPathNameByHandle(fh, sb, (uint)sb.Capacity, 0);
                if (len == 0 || len >= (uint)sb.Capacity) return null;
                var p = sb.ToString();
                // GetFinalPathNameByHandle returns \\?\C:\...; strip to the
                // plain DOS path so downstream long-path handling matches the
                // watcher shape.
                const string prefix = @"\\?\";
                if (p.StartsWith(prefix, StringComparison.Ordinal))
                    p = p.Substring(prefix.Length);
                return p;
            }
            finally { CloseHandle(fh); }
        }

        internal static bool TryParseRecord(byte[] buf, int offset, out UsnRecord? record)
        {
            record = null;
            // Fixed 52-byte prefix; FileName follows at FileNameOffset.
            if (buf.Length - offset < 52) return false;
            uint recLen = BitConverter.ToUInt32(buf, offset);
            if (recLen < 52 || offset + recLen > (uint)buf.Length) return false;
            ushort major = BitConverter.ToUInt16(buf, offset + 4);
            if (major != 2 && major != 3) return false;
            ushort nameLen = BitConverter.ToUInt16(buf, offset + 48);
            ushort nameOff = BitConverter.ToUInt16(buf, offset + 50);
            if (nameOff + nameLen > recLen) return false;
            record = new UsnRecord
            {
                FileRef = BitConverter.ToUInt64(buf, offset + 8),
                ParentRef = BitConverter.ToUInt64(buf, offset + 16),
                Usn = BitConverter.ToInt64(buf, offset + 24),
                Reason = BitConverter.ToUInt32(buf, offset + 32),
                Attributes = BitConverter.ToUInt32(buf, offset + 44),
                FileName = Encoding.Unicode.GetString(buf, offset + nameOff, nameLen),
            };
            return true;
        }

        private static (ulong JournalId, long NextUsn) QueryJournal(IntPtr volume)
        {
            var outBuf = new byte[64];
            if (!DeviceIoControl(volume, FsctlQueryUsnJournal, IntPtr.Zero, 0,
                    outBuf, (uint)outBuf.Length, out var ret, IntPtr.Zero) || ret < 24)
                throw new IOException("FSCTL_QUERY_USN_JOURNAL failed (Win32 " +
                    Marshal.GetLastWin32Error() + "); the volume may have no active journal.");
            var ptr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(outBuf, 0, ptr, 24);
                var data = Marshal.PtrToStructure<UsnJournalData>(ptr);
                return (data.UsnJournalID, data.NextUsn);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        // Reads one batch; advances lastUsn past the records returned.
        // Empty (timeout) batches return an empty list, not null.
        private static List<UsnRecord> ReadJournal(IntPtr volume, ref long lastUsn, byte[] buf)
        {
            var journal = QueryJournal(volume);
            var input = new ReadUsnInput
            {
                StartUsn = lastUsn,
                ReasonMask = 0xFFFFFFFF,
                ReturnOnlyOnClose = 0,
                Timeout = 5,
                BytesToWaitFor = 1,
                UsnJournalID = journal.JournalId,
            };
            var size = Marshal.SizeOf<ReadUsnInput>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(input, ptr, false);
                if (!DeviceIoControl(volume, FsctlReadUsnJournal, ptr, (uint)size,
                        buf, (uint)buf.Length, out var ret, IntPtr.Zero))
                    throw new IOException("FSCTL_READ_USN_JOURNAL_DATA failed (Win32 " +
                        Marshal.GetLastWin32Error() + ").");
                var records = new List<UsnRecord>();
                // First 8 bytes are the next-USN cursor, records follow.
                int off = 8;
                while (off + 4 <= ret)
                {
                    uint recLen = BitConverter.ToUInt32(buf, off);
                    if (recLen == 0 || off + recLen > ret) break;
                    if (TryParseRecord(buf, off, out var rec) && rec != null)
                    {
                        records.Add(rec);
                        if (rec.Usn > lastUsn) lastUsn = rec.Usn;
                    }
                    off += (int)recLen;
                }
                // If the journal was recreated under us the id changed and the
                // read above threw; reaching here with records means the head
                // only advanced — but re-query anyway when nothing came back
                // so a recreation is noticed within one timeout, not one event.
                if (records.Count == 0 && journal.NextUsn > lastUsn + 8 * 1024 * 1024)
                    lastUsn = journal.NextUsn;
                return records;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private void FireChanged(ChangeType type, string path, string? oldPath = null)
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(this, new FileChangedEventArgs(type, path, oldPath)); }
            catch (Exception ex) { FireError(ex); }
        }

        private void FireError(Exception ex)
        {
            var handler = Error;
            if (handler == null) return;
            try { handler(this, new MonitorErrorEventArgs(ex)); }
            catch { }
        }

        private void SleepOrStop(int ms)
        {
            var waited = 0;
            while (!_stopping && waited < ms)
            {
                Thread.Sleep(Math.Min(200, ms - waited));
                waited += 200;
            }
        }

        public void Stop()
        {
            _stopping = true;
            _armed.Set();
            var h = _volumeHandle;
            // Cancel the blocked DeviceIoControl before closing: closing a
            // handle with a pending synchronous read can stall in a filter
            // driver (same reason FileWatcherMonitor cancels first).
            if (h != IntPtr.Zero && h != InvalidHandle)
            {
                try { CancelIoEx(h, IntPtr.Zero); } catch { }
                try { CloseHandle(h); } catch { }
            }
            _volumeHandle = IntPtr.Zero;
            var worker = _worker;
            _worker = null;
            try { worker?.Join(TimeSpan.FromSeconds(10)); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _armed.Dispose();
        }
    }
}
