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
        }

        private static void Cleanup(string sealedPackPath, string outputPath)
        {
            // Best-effort: the throw is the signal, not the cleanup.
            try { File.Delete(outputPath); } catch { }
            try { File.Delete(sealedPackPath); } catch { }
        }
    }
}
