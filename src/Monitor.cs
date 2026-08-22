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
        private Thread _worker;
        private volatile bool _stopping;
        private string _pendingRename;
        private readonly string _path;
        private readonly int _bufferSize;
        private readonly int _restartDelaySec;

        public event EventHandler<FileChangedEventArgs> Changed;
        public event EventHandler<MonitorErrorEventArgs> Error;

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
            _worker = new Thread(WorkerLoop) { IsBackground = true };
            _worker.Start();
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
                        FireError(new IOException("Failed to open directory: " + _path));
                        SleepOrStop(5000);
                        continue;
                    }

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
                        ParseEvents(buf, ret);
                    }
                }
                catch (Exception ex) when (!_stopping)
                {
                    FireError(ex);
                }
                finally
                {
                    if (_dirHandle != IntPtr.Zero && _dirHandle != new IntPtr(-1))
                    {
                        CloseHandle(_dirHandle);
                        _dirHandle = IntPtr.Zero;
                    }
                    FlushRename();
                }
                SleepOrStop(_restartDelaySec * 1000);
            }
        }

        private void ParseEvents(byte[] buf, uint len)
        {
            var offset = 0;
            while (offset < len)
            {
                var next = BitConverter.ToInt32(buf, offset);
                var action = BitConverter.ToInt32(buf, offset + 4);
                var nameLen = BitConverter.ToInt32(buf, offset + 8);
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
                offset += next;
            }
        }

        private void FlushRename()
        {
            if (_pendingRename != null)
            {
                FireChanged(ChangeType.Deleted, _pendingRename);
                _pendingRename = null;
            }
        }

        private void FireChanged(ChangeType type, string path, string oldPath = null)
        {
            var h = Changed;
            if (h != null)
            {
                try { h(this, new FileChangedEventArgs(type, path, oldPath)); }
                catch { }
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
            var h = _dirHandle;
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                CloseHandle(h);
                _dirHandle = IntPtr.Zero;
            }
            if (_worker != null && _worker.IsAlive)
            {
                if (!_worker.Join(3000))
                    _worker.Interrupt();
                _worker = null;
            }
        }

        public void Dispose() => Stop();
    }

    public class PollingMonitor : IFileMonitor
    {
        private readonly string _path;
        private readonly int _intervalSec;
        private Timer _timer;
        private Dictionary<string, FileSnapshot> _snapshot;
        private string _prefix;

        public event EventHandler<FileChangedEventArgs> Changed;
        public event EventHandler<MonitorErrorEventArgs> Error;

        public PollingMonitor(string path, int intervalSec = 300)
        {
            _path = path;
            _intervalSec = Math.Max(10, intervalSec);
            _prefix = _path.EndsWith("\\") ? _path : _path + "\\";
        }

        public void Start()
        {
            _timer = new Timer(_ => Poll(), null, _intervalSec * 1000, _intervalSec * 1000);
        }

        private void Poll()
        {
            try
            {
                if (_snapshot == null) { _snapshot = Scan(); return; }
                var current = Scan();
                var prev = _snapshot;

                foreach (var kv in current)
                {
                    if (!prev.ContainsKey(kv.Key))
                    {
                        var handler = Changed;
                        if (handler != null)
                            handler(this, new FileChangedEventArgs(ChangeType.Created, _prefix + kv.Key));
                    }
                    else if (!prev[kv.Key].Equals(kv.Value))
                    {
                        var handler = Changed;
                        if (handler != null)
                            handler(this, new FileChangedEventArgs(ChangeType.Modified, _prefix + kv.Key));
                    }
                }
                foreach (var kv in prev)
                {
                    if (!current.ContainsKey(kv.Key))
                    {
                        var handler = Changed;
                        if (handler != null)
                            handler(this, new FileChangedEventArgs(ChangeType.Deleted, _prefix + kv.Key));
                    }
                }

                _snapshot = current;
            }
            catch (Exception ex)
            {
                var handler = Error;
                if (handler != null)
                    handler(this, new MonitorErrorEventArgs(ex));
            }
        }

        private Dictionary<string, FileSnapshot> Scan()
        {
            var result = new Dictionary<string, FileSnapshot>();
            if (!Directory.Exists(_path)) return result;
            var prefix = _path.EndsWith("\\") ? _path : _path + "\\";
            foreach (var f in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(f);
                    result[f.Substring(prefix.Length)] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
                }
                catch { }
            }
            return result;
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

        private struct FileSnapshot
        {
            public readonly long Size;
            public readonly DateTime LastWrite;
            public FileSnapshot(long size, DateTime lastWrite) { Size = size; LastWrite = lastWrite; }
            public bool Equals(FileSnapshot other) { return Size == other.Size && LastWrite == other.LastWrite; }
        }
    }

    public class CompositeMonitor : IFileMonitor
    {
        private readonly IFileMonitor[] _monitors;

        public CompositeMonitor(params IFileMonitor[] monitors)
        {
            _monitors = monitors ?? Array.Empty<IFileMonitor>();
        }

        public event EventHandler<FileChangedEventArgs> Changed;
        public event EventHandler<MonitorErrorEventArgs> Error;

        public void Start()
        {
            foreach (var m in _monitors)
            {
                m.Changed += (s, e) =>
                {
                    var handler = Changed;
                    if (handler != null)
                        handler(s, e);
                };
                m.Error += (s, e) =>
                {
                    var handler = Error;
                    if (handler != null)
                        handler(s, e);
                };
                m.Start();
            }
        }

        public void Stop()
        {
            foreach (var m in _monitors) m.Stop();
        }

        public void Dispose()
        {
            foreach (var m in _monitors) m.Dispose();
        }
    }
}
