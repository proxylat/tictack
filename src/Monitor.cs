using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        private const uint TokenAdjustPrivileges = 0x20;
        private const uint TokenQuery = 0x08;
        private const uint SePrivilegeEnabled = 0x02;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInfo lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileById(IntPtr hVolumeHint, ref FileIdDescriptor lpFileId,
            uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath,
            uint cchFilePath, uint dwFlags);

        // FILE_ID_DESCRIPTOR (winioctl.h): dwSize(4) + Type(4) + union.
        // The union is sized by its largest member (FILE_ID_128, 16
        // bytes), so sizeof(FILE_ID_DESCRIPTOR) = 24 and dwSize must say
        // 24 even for Type = 0 (raw 64-bit file reference). UnionTail
        // keeps the managed layout at the true 24 bytes — the struct is
        // pinned and handed to the kernel by reference, so a 16-byte
        // struct would pass a short buffer. (A wrong dwSize makes
        // OpenFileById fail with ERROR_INVALID_PARAMETER on every
        // resolve, and TranslateRecord then drops every USN event as
        // unresolvable: empty collections, no errors.)
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdDescriptor
        {
            public int Size;
            public int Type;
            public long FileId;
            public long UnionTail;
        }

        internal static readonly int FileIdDescriptorSize = Marshal.SizeOf<FileIdDescriptor>();

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

        // winioctl.h: CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 61, METHOD_BUFFERED,
        // FILE_ANY_ACCESS). Cross-checked via the CTL_CODE formula against
        // known siblings (ENUM 0x900B3, CREATE 0x900E7, READ 0x900BB).
        internal const uint FsctlQueryUsnJournal = 0x000900f4;
        // winioctl.h: CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 46, METHOD_NEITHER,
        // FILE_ANY_ACCESS). Was 0x900FB (function 62, undefined) — that would
        // have failed the first journal read right after a successful probe.
        internal const uint FsctlReadUsnJournal = 0x000900bb;
        // winioctl.h: CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 42, METHOD_BUFFERED,
        // FILE_ANY_ACCESS). Was 0x90028 (function 10 = FSCTL_UNLOCK_VOLUME),
        // so the old "mounted control" was unlocking nothing, not checking
        // the mount. In-probe control call: needs no buffers and no
        // journal, so it discriminates handle/environment failure from a
        // genuinely USN-specific rejection.
        internal const uint FsctlIsVolumeMounted = 0x000900a8;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
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
        // NTFS-side pre-filter: a record reaches us only if it carries at
        // least one of these reasons. Covers exactly what TranslateRecord
        // maps (create/delete/rename/modify); everything else (security,
        // EA, reparse, quota, close-alone...) maps to null downstream, so
        // dropping it here skips wasted resolves, never real events.
        // If a new mapping is added below, extend this mask too
        // (ReasonWatchMask_CoversAllMappedReasons fails otherwise).
        internal const uint ReasonWatchMask = ReasonFileCreate | ReasonFileDelete
            | ReasonRenameOldName | ReasonRenameNewName | ReasonModifyMask;

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
        private readonly int _pollIntervalMs;
        private readonly string _volumeDevice;
        private Thread? _worker;
        private volatile bool _stopping;
        private IntPtr _volumeHandle = IntPtr.Zero;
        private readonly ManualResetEventSlim _armed = new(false);
        // Sleep gate for the poll loop. A dedicated event (not _armed:
        // that one stays set once armed, so waiting on it would spin).
        // Single kernel wait instead of a chunked Thread.Sleep loop: 5
        // workers x 200ms chunks woke 25x/sec doing nothing (~147k
        // cycles/sec in Task Manager with usn:true).
        private readonly ManualResetEventSlim _wake = new(false);
        // Rename-old path by file reference number. A rename out of the
        // volume leaves an orphan entry; the cap bounds that leak.
        // ponytail: clear-all eviction, per-FRN LRU if renames ever dominate.
        private readonly Dictionary<ulong, string> _pendingOldNames = new();
        // FRNs with a completed rename pair. The journal can deliver the
        // new name twice (duplicate rename-new, or a create record for the
        // new link): the paired rename already reported it, so a later
        // create/orphan for the same FRN is a duplicate, not a new file.
        // Cleared on delete (FRN lifecycle end), capped like pending.
        private readonly HashSet<ulong> _renameTargets = new();
        // Silent-drop observability: every by-id resolve failure lands
        // here, so a live test with an empty collection can distinguish
        // "no records arrived" from "records arrived but paths unresolvable".
        internal long ResolveFailures;
        // ParentRef pre-filter (usn_parent_prefilter): FRNs of every
        // directory under the watched tree. The journal is volume-wide,
        // so most records belong to other apps (Spotify, Temp...); a
        // record whose parent is not a watched dir skips both resolves
        // with zero syscalls. UnderWatch stays the authority — a stale
        // entry only costs an extra resolve, never a wrong event.
        private readonly HashSet<ulong> _watchDirFrns = new();
        private readonly bool _parentPrefilter;
        internal bool _prefilterActive;
        private readonly ILogger? _log;
        internal long PrefilteredSkips;
        internal long _recordsSeen;
        private long _eventsEmitted;
        // Swappable for tests: production resolves FRNs via the OS,
        // tests inject a fake mapping (native calls need Windows).
        internal Func<string, ulong?> FrnOfPath = NativeFrnOfPath;

        public event EventHandler<FileChangedEventArgs>? Changed;
        public event EventHandler<MonitorErrorEventArgs>? Error;

        public UsnJournalMonitor(string path, int restartDelaySec = 10, int pollIntervalMs = 200,
            bool parentPrefilter = true, ILogger? log = null)
        {
            // No platform check here: translation and parsing are pure and
            // unit-tested cross-platform; ProbeVolume/Start throw on misuse.
            _path = Path.GetFullPath(path);
            _prefix = _path.EndsWith(Path.DirectorySeparatorChar) ? _path : _path + Path.DirectorySeparatorChar;
            _restartDelaySec = Math.Max(1, restartDelaySec);
            // Floor blocks a typo from re-creating the busy-spin
            // (proven by ProcMon: solid QUERY+READ, ~40% CPU idle).
            _pollIntervalMs = Math.Max(50, pollIntervalMs);
            _parentPrefilter = parentPrefilter;
            _log = log;
            var root = Path.GetPathRoot(_path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new NotSupportedException("USN journal is per-volume; UNC paths are not supported: " + path);
            _volumeDevice = @"\\.\" + root.TrimEnd('\\');
        }

        // fsutil enables SeManageVolumePrivilege before touching the journal:
        // presence in the token is not enough, Disabled stays disabled until
        // adjusted. Reports per-privilege outcome so the probe failure line
        // distinguishes "adjust failed" from "enabled yet ioctl still fails".
        // Best-effort — the probe-skip fallback treats any non-ok as before,
        // so unelevated callers behave exactly as before.
        internal static string EnableVolumePrivileges()
        {
            if (!OperatingSystem.IsWindows())
                return "unsupported";
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)
                || token == IntPtr.Zero)
                return "token:err" + Marshal.GetLastWin32Error();
            try
            {
                var parts = new List<string>();
                foreach (var name in new[] { "SeManageVolumePrivilege", "SeBackupPrivilege" })
                {
                    if (!LookupPrivilegeValue(null, name, out var luid))
                    {
                        parts.Add(name + ":lookup-err" + Marshal.GetLastWin32Error());
                        continue;
                    }
                    var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                    bool adjusted = AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
                    int w = Marshal.GetLastWin32Error();
                    parts.Add(name + (adjusted && w == 0 ? ":ok" : ":err" + w));
                }
                return string.Join("/", parts);
            }
            finally { CloseHandle(token); }
        }

        // Opens the volume and reads the journal head. Called by the factory
        // before wiring the monitor so elevation failure falls back to the
        // watcher with a warning instead of failing startup. Also called by
        // Start for the late-start path.
        internal (ulong JournalId, long NextUsn) ProbeVolume()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("UsnJournalMonitor requires Windows/NTFS.");
            // Best-effort: Disabled privileges stay disabled until adjusted,
            // and failure here keeps the existing probe-skip fallback.
            // The report travels into the failure line (priv=...).
            // Open shape is the documented sample (GENERIC_READ|WRITE,
            // flags 0): probe-v4 proved FILE_READ_ATTRIBUTES delivery fails
            // with Win32 1 on some stacks while this shape succeeds.
            var priv = EnableVolumePrivileges();
            var handle = CreateFile(_volumeDevice, GenericRead | GenericWrite, FileShareAll,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandle)
            {
                var err = Marshal.GetLastWin32Error();
                throw new IOException("Cannot open volume " + _volumeDevice +
                    " (Win32 " + err + "); USN journal needs elevation — run as admin/SYSTEM or use watcher mode.");
            }
            try
            {
                return QueryJournal(handle, _volumeDevice, priv);
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
            _wake.Reset();
            _worker = new Thread(WorkerLoop) { IsBackground = true };
            _worker.Start();
            // Same contract as FileWatcherMonitor: do not return until the
            // first read is armed, so no change slips through the gap.
            _armed.Wait(TimeSpan.FromSeconds(10));
        }

        private void WorkerLoop()
        {
            var priv = EnableVolumePrivileges();
            while (!_stopping)
            {
                try
                {
                    // Same documented sample shape as ProbeVolume (see note
                    // there): minimal-access opens fail FSCTL delivery on
                    // some stacks with Win32 1.
                    _volumeHandle = CreateFile(_volumeDevice, GenericRead | GenericWrite, FileShareAll,
                        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (_volumeHandle == InvalidHandle)
                    {
                        _armed.Set();
                        FireError(new IOException("Failed to open volume: " + _volumeDevice));
                        SleepOrStop(_restartDelaySec * 1000);
                        continue;
                    }
                    if (_stopping) break;

                    // Seed before the head is captured: dirs created in
                    // between still deliver CREATE records at/after it.
                    SeedWatchDirs();
                    ulong journalId;
                    long lastUsn;
                    try { (journalId, lastUsn) = QueryJournal(_volumeHandle, _volumeDevice, priv); }
                    catch (Exception ex)
                    {
                        _armed.Set();
                        FireError(ex);
                        SleepOrStop(_restartDelaySec * 1000);
                        continue;
                    }

                    _log?.Info("USN watch started [" + _path + "]: journal=" + journalId
                        + " head=" + lastUsn + " mask=0x" + ReasonWatchMask.ToString("X8")
                        + " poll=" + _pollIntervalMs + "ms prefilter="
                        + (_prefilterActive ? "on (" + _watchDirFrns.Count + " dirs)" : "off"));
                    _armed.Set();
                    var buf = new byte[64 * 1024];
                    var lastStats = Stopwatch.StartNew();
                    while (!_stopping)
                    {
                        List<UsnRecord>? records = null;
                        try { records = ReadJournal(_volumeHandle, journalId, ref lastUsn, buf); }
                        catch (Exception ex)
                        {
                            // Journal recreated/deleted underneath us: the gap
                            // is real, so say so, then re-baseline at the new
                            // head. InitialSync (or composite's polling leg)
                            // covers the missed range.
                            FireError(ex);
                            _log?.Info("USN journal changed underneath, re-baselining at new head"
                                + " (gap covered by InitialSync/polling leg).");
                            break;
                        }
                        if (records != null)
                        {
                            if (records.Count == 0)
                            {
                                // The read should block up to Timeout seconds,
                                // but some stacks return instantly when caught
                                // up — without this the loop busy-spins on an
                                // ioctl per iteration (proven by ProcMon: solid
                                // QUERY+READ with no sleeps, ~40% CPU idle).
                                SleepOrStop(_pollIntervalMs);
                            }
                            else
                            {
                                foreach (var rec in records)
                                {
                                    if (_stopping) break;
                                    Dispatch(rec);
                                }
                            }
                        }
                        if (lastStats.Elapsed >= TimeSpan.FromMinutes(30))
                        {
                            _log?.Info("USN stats [" + _path + "]: records=" + _recordsSeen
                                + " events=" + _eventsEmitted
                                + " prefiltered=" + PrefilteredSkips
                                + " resolveFailures=" + ResolveFailures);
                            lastStats.Restart();
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

        internal void Dispatch(UsnRecord rec)
        {
            _recordsSeen++;
            if (_log != null)
                _log.Debug("USN usn=" + rec.Usn + " reason=0x" + rec.Reason.ToString("X8")
                    + " name=" + rec.FileName + " parent=" + rec.ParentRef);
            // Volume-wide journal: most records belong to other apps.
            // A ParentRef miss skips both resolves with zero syscalls.
            if (!IsWatchedParent(rec.ParentRef)) { PrefilteredSkips++; return; }
            var evt = TranslateRecord(rec, ResolvePath);
            if (evt != null) { _eventsEmitted++; FireChanged(evt.ChangeType, evt.FullPath, evt.OldFullPath); }
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
                _pendingOldNames.Remove(rec.FileRef);
                _renameTargets.Remove(rec.FileRef);
                // FRN lifecycle end: a later reuse starts untracked.
                if (isDir) _watchDirFrns.Remove(rec.FileRef);
            }
            else if ((rec.Reason & ReasonRenameOldName) != 0)
            {
                // By the time the journal is read the file has already
                // moved, so opening it by id returns the NEW path. The
                // record's parent ref + old name are the authoritative
                // old location.
                var oldFull = UnderWatch(CombineParent(rec, resolvePath));
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
                    // Duplicate delivery of an already-paired rename (same
                    // FRN): the rename event covered it, stay silent.
                    if (_renameTargets.Contains(rec.FileRef)) return null;
                    // Rename into the watched tree (or a missed old-name):
                    // report as creation of the new name.
                    type = ChangeType.Created;
                }
                else
                {
                    if (_renameTargets.Count > 1024) _renameTargets.Clear();
                    _renameTargets.Add(rec.FileRef);
                }
                if (isDir && _parentPrefilter && _watchDirFrns.Contains(rec.ParentRef))
                {
                    // Directory moved (or created) into the tree: track it
                    // plus its subtree, closing the move-in gap. A move out
                    // leaves a stale entry — harmless, UnderWatch drops it.
                    _watchDirFrns.Add(rec.FileRef);
                    AddSubtreeFrns(CombineParent(rec, resolvePath));
                }
            }
            else if ((rec.Reason & ReasonFileCreate) != 0)
            {
                // Create record for an already-paired rename target (same
                // FRN): duplicate delivery of the new link, stay silent.
                if (_renameTargets.Contains(rec.FileRef)) return null;
                if (isDir && _parentPrefilter && _watchDirFrns.Contains(rec.ParentRef))
                    _watchDirFrns.Add(rec.FileRef);
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
                // Event-time path first: by the time the journal is read
                // the file may have moved (by-id then reports the NEW
                // name) or been deleted (by-id fails outright). The
                // record's parent ref + name are where the event happened;
                // by-id stays as the fallback for a deleted parent.
                full = UnderWatch(CombineParent(rec, resolvePath) ?? resolvePath(rec.FileRef));
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
            var desc = new FileIdDescriptor { Size = FileIdDescriptorSize, Type = 0, FileId = (long)fileRef };
            var fh = OpenFileById(h, ref desc, 0, FileShareAll, IntPtr.Zero, FileFlagBackupSemantics);
            if (fh == InvalidHandle || fh == IntPtr.Zero) { Interlocked.Increment(ref ResolveFailures); return null; }
            try
            {
                var sb = new StringBuilder(512);
                var len = GetFinalPathNameByHandle(fh, sb, (uint)sb.Capacity, 0);
                if (len == 0 || len >= (uint)sb.Capacity) { Interlocked.Increment(ref ResolveFailures); return null; }
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

        // Pack=4 is load-bearing: the native BY_HANDLE_FILE_INFORMATION is
        // 52 bytes with 4-aligned fields. Default Pack=8 inserts 4 pad
        // bytes after FileAttributes, shifting FileIndexHigh/Low onto
        // bytes the OS never wrote — every computed FRN came out garbage
        // and the ParentRef pre-filter dropped 100% of journal records
        // (proven by prefiltered=seen, events=0 on Windows). With Pack=4
        // the managed layout is byte-identical to native (SizeOf 52,
        // FileIndexHigh@44, FileIndexLow@48 — pinned by
        // ByHandleFileInfo_MatchesNativeLayout).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct ByHandleFileInfo
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private static ulong? NativeFrnOfPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return null;
            IntPtr h;
            try
            {
                h = CreateFile(path, 0, FileShareAll, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics, IntPtr.Zero);
            }
            catch { return null; }
            if (h == InvalidHandle || h == IntPtr.Zero) return null;
            try
            {
                if (!GetFileInformationByHandle(h, out var info)) return null;
                return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            }
            finally { CloseHandle(h); }
        }

        // Seeds the ParentRef pre-filter: the FRN of the watched root plus
        // every directory beneath it. Runs before the journal head is
        // captured, so dirs created in between still deliver CREATE records
        // at/after the head and join via the maintain rules below. An empty
        // result (root missing, lookup failing) disables the pre-filter for
        // the run: skipping everything would be blindness, not filtering.
        internal void SeedWatchDirs()
        {
            _watchDirFrns.Clear();
            _prefilterActive = false;
            if (!_parentPrefilter) return;
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(_path, "*", SearchOption.AllDirectories)
                    .Prepend(_path);
            }
            catch { return; }
            foreach (var d in dirs)
            {
                ulong? frn;
                try { frn = FrnOfPath(d); }
                catch { continue; }
                if (frn != null) _watchDirFrns.Add(frn.Value);
            }
            _prefilterActive = _watchDirFrns.Count > 0;
        }

        internal bool IsWatchedParent(ulong parentRef)
        {
            return !_prefilterActive || _watchDirFrns.Contains(parentRef);
        }

        private void AddSubtreeFrns(string? root)
        {
            if (root == null || !_prefilterActive) return;
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Prepend(root);
            }
            catch { return; }
            foreach (var d in dirs)
            {
                ulong? frn;
                try { frn = FrnOfPath(d); }
                catch { continue; }
                if (frn != null) _watchDirFrns.Add(frn.Value);
            }
        }

        internal static bool TryParseRecord(byte[] buf, int offset, out UsnRecord? record)
        {
            record = null;
            // True USN_RECORD_V2 layout (winioctl.h): RecordLength@0,
            // Major@4, FileRef@8, ParentRef@16, Usn@24, TimeStamp@32,
            // Reason@40, SourceInfo@44, SecurityId@48, Attributes@52,
            // FileNameLength@56, FileNameOffset@58, FileName@60. The fixed
            // prefix is 60 bytes, not 52 — the old 52-byte assumption read
            // TimeStamp as Reason and SecurityId as name length, so the
            // bounds check below rejected every real NTFS record and the
            // monitor collected zero events with zero errors.
            // V3 (ReFS, 128-bit file ids) is rejected: the by-id resolve
            // path assumes 64-bit FRNs, so a clean skip beats misparsing.
            if (buf.Length - offset < 60) return false;
            uint recLen = BitConverter.ToUInt32(buf, offset);
            if (recLen < 60 || offset + recLen > (uint)buf.Length) return false;
            ushort major = BitConverter.ToUInt16(buf, offset + 4);
            if (major != 2) return false;
            ushort nameLen = BitConverter.ToUInt16(buf, offset + 56);
            ushort nameOff = BitConverter.ToUInt16(buf, offset + 58);
            if (nameOff + nameLen > recLen) return false;
            record = new UsnRecord
            {
                FileRef = BitConverter.ToUInt64(buf, offset + 8),
                ParentRef = BitConverter.ToUInt64(buf, offset + 16),
                Usn = BitConverter.ToInt64(buf, offset + 24),
                Reason = BitConverter.ToUInt32(buf, offset + 40),
                Attributes = BitConverter.ToUInt32(buf, offset + 52),
                FileName = Encoding.Unicode.GetString(buf, offset + nameOff, nameLen),
            };
            return true;
        }

        // Baked into the failure message so a pasted line proves which
        // binary produced it. Bump when the probe changes.
        private const string ProbeMarker = "probe-v4";

        // In-probe control: the trivial ioctl on the same handle. If it
        // fails too, the handle/environment is broken — USN exonerated.
        private static (bool Ok, int Win32) TestVolumeMounted(IntPtr volume)
        {
            if (!DeviceIoControl(volume, FsctlIsVolumeMounted, IntPtr.Zero, 0,
                    Array.Empty<byte>(), 0, out _, IntPtr.Zero))
                return (false, Marshal.GetLastWin32Error());
            return (true, 0);
        }

        // probe-v4: retry the query on a fresh handle of the same
        // documented shape. A fresh-handle success means the first handle
        // was bad (transient); identical failure means volume FSCTLs are
        // not delivered to NTFS in this process and no open shape will
        // fix it. (This retry is also what convicted the old
        // FILE_READ_ATTRIBUTES open: sample-shape=ok against Win32 1.)
        private static string TrySampleShape(string device)
        {
            var handle = CreateFile(device, GenericRead | GenericWrite, FileShareAll,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandle)
                return "open-err" + Marshal.GetLastWin32Error();
            try
            {
                var outBuf = new byte[64];
                bool ok = DeviceIoControl(handle, FsctlQueryUsnJournal, IntPtr.Zero, 0,
                    outBuf, (uint)outBuf.Length, out var ret, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();
                return ok && ret >= 24 ? "ok" : "fail/Win32 " + err;
            }
            finally { CloseHandle(handle); }
        }

        private static (ulong JournalId, long NextUsn) QueryJournal(IntPtr volume, string device, string priv)
        {
            var outBuf = new byte[64];
            bool ok = DeviceIoControl(volume, FsctlQueryUsnJournal, IntPtr.Zero, 0,
                outBuf, (uint)outBuf.Length, out var ret, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            if (ok && ret >= 24)
            {
                var ptr = Marshal.AllocHGlobal(24);
                try
                {
                    Marshal.Copy(outBuf, 0, ptr, 24);
                    var data = Marshal.PtrToStructure<UsnJournalData>(ptr);
                    return (data.UsnJournalID, data.NextUsn);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            // Failure diagnostics: which branch fired, raw handle, bytes
            // returned, in-process control call, documented-shape retry.
            // Read the pasted line: sample-shape=ok means our open is the
            // bug; sample-shape failing identically means volume FSCTLs are
            // not delivered to NTFS in this process (a working fsutil proves
            // the journal itself exists) and the USN member stays skipped.
            var (mountedOk, mountedErr) = TestVolumeMounted(volume);
            var sample = TrySampleShape(device);
            throw new IOException("FSCTL_QUERY_USN_JOURNAL failed on " + device +
                " [" + ProbeMarker + "] (Win32 " + err +
                ", ok=" + ok + ", bytes=" + ret +
                ", handle=0x" + volume.ToString("X") +
                ", mounted-control=" + (mountedOk ? "ok" : "fail/Win32 " + mountedErr) +
                ", sample-shape=" + sample +
                ", priv=" + priv + "); " +
                "USN member skipped, watcher/polling fallback in effect.");
        }

        // Reads one batch; advances lastUsn past the records returned.
        // Empty (timeout) batches return an empty list, not null. The journal
        // id is captured once by the caller on purpose: no per-poll QUERY
        // (that ioctl doubled the idle syscall rate), and a recreation
        // underneath us makes this read throw on id mismatch, which the
        // worker turns into a re-baseline at the new head.
        private static List<UsnRecord> ReadJournal(IntPtr volume, ulong journalId, ref long lastUsn, byte[] buf)
        {
            var input = new ReadUsnInput
            {
                StartUsn = lastUsn,
                ReasonMask = ReasonWatchMask,
                ReturnOnlyOnClose = 0,
                Timeout = 5,
                BytesToWaitFor = 1,
                UsnJournalID = journalId,
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
                // StartUsn is exclusive in practice, but if a stack ever
                // returns the boundary record again the floor drops the
                // replay: reprocessing one batch per poll would duplicate
                // every event and strand rename pairing on orphans.
                // The output cursor doubles as the head for the jump below,
                // replacing the per-poll QUERY this method used to do.
                long headUsn = ret >= 8 ? BitConverter.ToInt64(buf, 0) : lastUsn;
                long floor = lastUsn;
                int off = 8;
                while (off + 4 <= ret)
                {
                    uint recLen = BitConverter.ToUInt32(buf, off);
                    if (recLen == 0 || off + recLen > ret) break;
                    if (TryParseRecord(buf, off, out var rec) && rec != null && rec.Usn > floor)
                    {
                        records.Add(rec);
                        if (rec.Usn > lastUsn) lastUsn = rec.Usn;
                    }
                    off += (int)recLen;
                }
                // Unparseable records (e.g. ReFS V3) never advance lastUsn
                // via the loop above; jump to the head when it runs far
                // ahead so one foreign batch can't pin the cursor forever.
                // (Head comes from the read output cursor, not a QUERY.)
                if (records.Count == 0 && headUsn > lastUsn + 8 * 1024 * 1024)
                    lastUsn = headUsn;
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
            try { _wake.Wait(ms); } catch (ObjectDisposedException) { }
        }

        public void Stop()
        {
            _stopping = true;
            _armed.Set();
            _wake.Set();
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
            _wake.Dispose();
        }
    }
}
