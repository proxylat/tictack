using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Corpus
{
    // Crash-safe metadata writes: write to a sibling .tmp, fsync via
    // FileStream.Flush(true), atomic File.Move, and best-effort parent dir
    // fsync on Linux so the directory entry survives power loss.
    internal static class AtomicFile
    {
        private const int O_RDONLY = 0;
        private const int O_DIRECTORY = 0x10000;

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int fsync(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, bool overwrite = true, bool ownerOnlyOnLinux = false)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
                {
                    if (ownerOnlyOnLinux && OperatingSystem.IsLinux())
                        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    fs.Write(data);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite);
                SyncParentDir(path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        public static void WriteAllText(string path, string content, bool overwrite = true) =>
            WriteAllBytes(path, Encoding.UTF8.GetBytes(content), overwrite);

        public static void SyncParentDir(string path)
        {
            if (!OperatingSystem.IsLinux()) return;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            int fd = open(dir, O_RDONLY | O_DIRECTORY);
            if (fd < 0) return;
            try { _ = fsync(fd); }
            finally { _ = close(fd); }
        }
    }
}
