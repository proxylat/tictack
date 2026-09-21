using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TicTack
{
    public enum ChangeType { Created, Modified, Deleted, Renamed }

    public class FileChangedEventArgs : EventArgs
    {
        public ChangeType ChangeType { get; }
        public string FullPath { get; }
        public string? OldFullPath { get; }

        public FileChangedEventArgs(ChangeType type, string path, string? oldPath = null)
        {
            ChangeType = type; FullPath = path; OldFullPath = oldPath;
        }
    }

    public class MonitorErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }

        public MonitorErrorEventArgs(Exception ex) { Exception = ex; }
    }

    public class FileActionArgs
    {
        public FileChangedEventArgs ChangeEvent { get; }
        public string SourceBase { get; }
        public string DestBase { get; }
        public string DestPath { get; }
        public string? OldDestPath { get; }
        public FileSnapshot? SourceSnapshot { get; }

        public FileActionArgs(FileChangedEventArgs changeEvent, string sourceBase, string destBase, FileSnapshot? sourceSnapshot = null)
        {
            ChangeEvent = changeEvent;
            SourceBase = sourceBase;
            DestBase = destBase;
            DestPath = MapPath(sourceBase, destBase, changeEvent.FullPath);
            SourceSnapshot = sourceSnapshot;
            if (changeEvent.ChangeType == ChangeType.Renamed && changeEvent.OldFullPath != null)
                OldDestPath = MapPath(sourceBase, destBase, changeEvent.OldFullPath);
        }

        private static string MapPath(string fromBase, string toBase, string path)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Boundary-aware compare without depending on the current OS separator:
            // base /a/src must not match /a/src2/f, while Windows-style literals
            // still match on Linux (and vice versa). A base that already ends in
            // a separator needs no further boundary check.
            var baseMatches = path.StartsWith(fromBase, comparison);
            var baseEndsWithSeparator = fromBase.Length > 0
                && (fromBase[fromBase.Length - 1] == '\\' || fromBase[fromBase.Length - 1] == '/');
            var boundaryOk = baseEndsWithSeparator
                || (path.Length > fromBase.Length && (path[fromBase.Length] == '\\' || path[fromBase.Length] == '/'));
            if (path.Equals(fromBase, comparison) || (baseMatches && boundaryOk))
            {
                var rel = path.Substring(fromBase.Length).TrimStart('\\', '/');
                return string.IsNullOrEmpty(rel) ? toBase : Path.Combine(toBase, rel);
            }
            return path;
        }
    }

    // Compared once per file per poll scan. IEquatable gives the JIT a
    // direct field compare instead of routing through ValueType.Equals.
    // Sequential is the CLR default for structs; explicit to satisfy
    // MA0008 without changing the marshalling contract.
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FileSnapshot : IEquatable<FileSnapshot>
    {
        public long Length { get; }
        public long LastWriteTimeUtcTicks { get; }
        public DateTime LastWriteTimeUtc => new DateTime(LastWriteTimeUtcTicks, DateTimeKind.Utc);
        // Informational only: populated from the same stat as Length/mtime,
        // deliberately excluded from Equals so change detection stays
        // content-based (attribute-only changes are not data changes).
        public FileAttributes Attributes { get; }

        public FileSnapshot(long length, long lastWriteTimeUtcTicks, FileAttributes attributes = default)
        {
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            Attributes = attributes;
        }

        public bool Equals(FileSnapshot other) =>
            Length == other.Length && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;

        public override bool Equals(object? obj) => obj is FileSnapshot other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Length, LastWriteTimeUtcTicks);

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

                snapshot = new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks, info.Attributes);
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
        // True for failures that a retry may clear (sharing violations,
        // transient IO). Pipeline unwraps these into the retry wrapper.
        public bool Retryable { get; set; }
        // Lowercase hex SHA-256 of the source bytes as streamed during copy,
        // set only when CopyAction.ComputeSourceHash is on. Null means no
        // copy-time hash is available; validators must do full reads.
        public string? SourceHash { get; set; }
        public static ActionResult Ok() { return new ActionResult { Success = true }; }
        public static ActionResult Fail(string msg, bool retryable = false) =>
            new ActionResult { Success = false, ErrorMessage = msg, Retryable = retryable };
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
        // True when AreEqual may read full file contents (hash/full).
        // Lets callers keep a cheap existence gate so warm runs never
        // pay content reads; metadata comparers stat dst themselves,
        // so no extra gate is needed for them.
        bool RequiresContentRead { get; }
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
        Task<ActionResult> ArchivePreviousVersionAsync(string destPath, CancellationToken ct);
    }

    public interface IDeletionStrategy
    {
        Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct);
    }

    public interface ILogger
    {
        void Debug(string msg);
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg, Exception? ex = null);
    }

}
