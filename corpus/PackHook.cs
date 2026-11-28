using System;
using System.IO;
using TicTack;

namespace Corpus
{
    // Per-pack encryption hook. Corpus ships zero crypto: one shell command
    // (config `encrypt:`) transforms each sealed pack. Same shape as TicTack
    // jobs — {input}/{output} substitution, /bin/sh -c (cmd.exe /c on
    // Windows) via ProcessRunner. Empty hook = stored unencrypted.
    public static class PackHook
    {
        public const string OutputSuffix = ".enc.tmp";

        // {input} = sealed pack, {output} = temp the hook must produce; on
        // success the output replaces the sealed pack. Any failure deletes
        // the output temp AND the plaintext pack (never leave unencrypted
        // bytes behind when encryption was requested) and throws loud.
        public static void Seal(string command, string sealedPackPath)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(sealedPackPath);
            var outputPath = sealedPackPath + OutputSuffix;
            var cmd = command.Replace("{input}", sealedPackPath).Replace("{output}", outputPath);
            var dir = Path.GetDirectoryName(sealedPackPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            int exit;
            string stderr;
            bool timedOut;
            try
            {
                (exit, _, stderr, timedOut) = ProcessRunner.RunAsync(cmd, dir, redirect: true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Cleanup(sealedPackPath, outputPath);
                throw new InvalidDataException($"pack encrypt hook failed to run: {ex.Message}", ex);
            }
            if (timedOut || exit != 0 || !File.Exists(outputPath))
            {
                Cleanup(sealedPackPath, outputPath);
                throw new InvalidDataException(timedOut
                    ? $"pack encrypt hook timed out ({ProcessRunner.TimeoutMs / 60000} min): {cmd}"
                    : exit != 0
                        ? $"pack encrypt hook exited {exit}: {stderr.Trim()}"
                        : $"pack encrypt hook produced no output file: {outputPath}");
            }
            File.Move(outputPath, sealedPackPath, overwrite: true);
            AtomicFile.SyncParentDir(sealedPackPath);
        }

        private static void Cleanup(string sealedPackPath, string outputPath)
        {
            // Best-effort: the throw is the signal, not the cleanup.
            try { File.Delete(outputPath); } catch { }
            try { File.Delete(sealedPackPath); } catch { }
        }
    }

    // Session-scoped decrypted pack cache for encrypted stores (`decrypt:`).
    // Decrypts each pack once into a private temp dir and wipes it on Dispose.
    public sealed class PackReadCache : IDisposable
    {
        private readonly string? _decryptCommand;
        private readonly string? _tempDir;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

        public PackReadCache(string? decryptCommand)
        {
            _decryptCommand = string.IsNullOrWhiteSpace(decryptCommand) ? null : decryptCommand;
            if (_decryptCommand != null)
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "corpus-dec-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
            }
        }

        public string Resolve(string packPath)
        {
            if (_decryptCommand == null || _tempDir == null)
                return packPath;
            var full = Path.GetFullPath(packPath);
            if (_cache.TryGetValue(full, out var cached) && File.Exists(cached))
                return cached;
            var gate = _locks.GetOrAdd(full, _ => new object());
            lock (gate)
            {
                if (_cache.TryGetValue(full, out cached) && File.Exists(cached))
                    return cached;
                var outPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".pack");
                var cmd = _decryptCommand.Replace("{input}", full).Replace("{output}", outPath);
                var dir = Path.GetDirectoryName(full) ?? _tempDir;
                var (exit, _, stderr, timedOut) = ProcessRunner.RunAsync(cmd, dir, redirect: true).GetAwaiter().GetResult();
                if (timedOut || exit != 0 || !File.Exists(outPath))
                {
                    try { File.Delete(outPath); } catch { }
                    throw new InvalidDataException($"pack decrypt hook failed (exit {exit}): {stderr.Trim()}");
                }
                _cache[full] = outPath;
                return outPath;
            }
        }

        public void Dispose()
        {
            if (_tempDir == null) return;
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
