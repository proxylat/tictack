using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public enum ChangeType { Created, Modified, Deleted, Renamed }

    public class FileChangedEventArgs : EventArgs
    {
        private readonly ChangeType _changeType;
        private readonly string _fullPath;
        private readonly string? _oldFullPath;

        public ChangeType ChangeType { get { return _changeType; } }
        public string FullPath { get { return _fullPath; } }
        public string? OldFullPath { get { return _oldFullPath; } }

        public FileChangedEventArgs(ChangeType type, string path, string? oldPath = null)
        {
            _changeType = type; _fullPath = path; _oldFullPath = oldPath;
        }
    }

    public class MonitorErrorEventArgs : EventArgs
    {
        private readonly Exception _exception;

        public Exception Exception { get { return _exception; } }

        public MonitorErrorEventArgs(Exception ex) { _exception = ex; }
    }

    public class FileActionArgs
    {
        private readonly FileChangedEventArgs _changeEvent;
        private readonly string _sourceBase;
        private readonly string _destBase;
        private readonly string _destPath;
        private readonly string? _oldDestPath;
        private readonly FileSnapshot? _sourceSnapshot;

        public FileChangedEventArgs ChangeEvent { get { return _changeEvent; } }
        public string SourceBase { get { return _sourceBase; } }
        public string DestBase { get { return _destBase; } }
        public string DestPath { get { return _destPath; } }
        public string? OldDestPath { get { return _oldDestPath; } }
        public FileSnapshot? SourceSnapshot { get { return _sourceSnapshot; } }

        public FileActionArgs(FileChangedEventArgs changeEvent, string sourceBase, string destBase, FileSnapshot? sourceSnapshot = null)
        {
            _changeEvent = changeEvent;
            _sourceBase = sourceBase;
            _destBase = destBase;
            _destPath = MapPath(sourceBase, destBase, changeEvent.FullPath);
            _sourceSnapshot = sourceSnapshot;
            if (changeEvent.ChangeType == ChangeType.Renamed && changeEvent.OldFullPath != null)
                _oldDestPath = MapPath(sourceBase, destBase, changeEvent.OldFullPath);
        }

        private static string MapPath(string fromBase, string toBase, string path)
        {
            if (path.StartsWith(fromBase, StringComparison.OrdinalIgnoreCase))
            {
                var rel = path.Substring(fromBase.Length).TrimStart('\\', '/');
                return string.IsNullOrEmpty(rel) ? toBase : Path.Combine(toBase, rel);
            }
            return path;
        }
    }

    public readonly struct FileSnapshot
    {
        public long Length { get; }
        public long LastWriteTimeUtcTicks { get; }
        public DateTime LastWriteTimeUtc => new DateTime(LastWriteTimeUtcTicks, DateTimeKind.Utc);

        public FileSnapshot(long length, long lastWriteTimeUtcTicks)
        {
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }

        public static bool TryRead(string path, out FileSnapshot snapshot)
        {
            try
            {
                var info = new FileInfo(PathUtil.EnsureExtended(path));
                if (!info.Exists)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks);
                return true;
            }
            catch
            {
                snapshot = default;
                return false;
            }
        }
    }

    public class ActionResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public static ActionResult Ok() { return new ActionResult { Success = true }; }
        public static ActionResult Fail(string msg) { return new ActionResult { Success = false, ErrorMessage = msg }; }
    }

    public interface IFileMonitor : IDisposable
    {
        event EventHandler<FileChangedEventArgs>? Changed;
        event EventHandler<MonitorErrorEventArgs>? Error;
        void Start();
        void Stop();
    }

    public enum VerificationLevel { Size, DateAndSize, Hash, Full }

    public interface IFileComparer
    {
        bool AreEqual(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null);
    }

    public interface IFileFilter
    {
        bool ShouldProcess(string fullPath);
    }

    public interface IFileAction
    {
        Task<ActionResult> ExecuteAsync(FileActionArgs args, CancellationToken ct);
    }

    public interface IRetryPolicy
    {
        Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct);
    }

    public interface IFileAccessor
    {
        Stream OpenRead(string path);
    }

    public interface IValidator
    {
        Task<bool> ValidateAsync(string sourcePath, string destPath, FileSnapshot? sourceSnapshot = null);
    }

    public interface IVersioningStrategy
    {
        Task ArchivePreviousVersionAsync(string destPath, CancellationToken ct);
    }

    public interface IDeletionStrategy
    {
        Task HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct);
    }

    public interface ILogger
    {
        void Debug(string msg);
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg, Exception? ex = null);
    }

    public interface ISyncPipeline : IDisposable
    {
        void Start();
        void Stop();
    }
}
