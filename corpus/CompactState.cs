using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    // CompactOS state, record-only (§12.14 third round 2026-10-17): our restore
    // writes blocks and never DISM-applies, so there is no apply step for a
    // Compact flag to attach to — capture the query output as a sidecar and
    // park the apply. NTFS per-file compression of the Windows tree is the
    // documented trap: same binary, unsupported path — recorded here as a
    // don't, never as an API (no method below takes or applies such a flag).
    public sealed record CompactOsState(bool Enabled, string? Algorithm);

    // Test seam: arguments string in, compact.exe stdout out. Tests inject
    // canned real-world outputs; production passes ProcessCompactRunner.
    public interface ICompactRunner
    {
        string Run(string arguments);
    }

    public static class CompactQuery
    {
        public static CompactOsState Parse(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidDataException("compact.exe output unrecognized (empty). Seen: (empty)");
            // Shapes from the MS Learn compact.exe command reference sample
            // outputs (verified live Oct 2025 per §12.14 research) — NOT yet
            // re-verified on a live Windows box from this repo; the
            // Windows-gated live test below owns that confirmation. The
            // negated shape is checked first: it contains the "in the
            // Compact state" substring of the positive shape.
            var lower = stdout.ToLowerInvariant();
            var off = lower.Contains("not in the compact state", StringComparison.Ordinal);
            var on = !off && lower.Contains("in the compact state", StringComparison.Ordinal);
            if (!on && !off)
                throw new InvalidDataException($"compact.exe output unrecognized (no Compact-state line found). Seen: {Truncate(stdout)}");
            return new CompactOsState(on, FindAlgorithm(lower));
        }

        public static CompactOsState Query(ICompactRunner runner, string? offlineWindowsDir = null)
        {
            ArgumentNullException.ThrowIfNull(runner);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("compact.exe CompactOS query requires Windows.");
            return Parse(runner.Run(BuildArgs(offlineWindowsDir)));
        }

        internal static string BuildArgs(string? offlineWindowsDir)
        {
            var args = "/CompactOS:query";
            if (!string.IsNullOrWhiteSpace(offlineWindowsDir))
                args += $" /windir \"{offlineWindowsDir}\"";
            return args;
        }

        // Documented /EXE set-values, from the compact.exe command reference
        // (§12.14): XPRESS4K, XPRESS8K, XPRESS16K, LZX. Token scan only —
        // the query output may not report one, in which case this is null.
        private static string? FindAlgorithm(string lower)
        {
            foreach (var algo in new[] { "XPRESS4K", "XPRESS8K", "XPRESS16K", "LZX" })
            {
                if (lower.Contains(algo.ToLowerInvariant(), StringComparison.Ordinal))
                    return algo;
            }
            return null;
        }

        private static string Truncate(string s) =>
            s.Length > 200 ? s[..200] + "..." : s;

        public sealed class ProcessCompactRunner : ICompactRunner
        {
            public string Run(string arguments)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("compact.exe CompactOS query requires Windows.");
                ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
                var psi = new ProcessStartInfo
                {
                    FileName = "compact.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException($"compact.exe failed to start ({arguments}).");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"compact.exe timed out ({arguments}).");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"compact.exe exited {process.ExitCode} ({arguments}): {stderr.Trim()}");
                return stdout;
            }
        }
    }

    // Sidecar DTO: <storeRoot>/manifests/<gen:D6>.compact.json (D6 matches
    // ManifestStore's <gen:D6>-<name>.json; the 7th char is '.', not '-',
    // so ManifestStore.List / NextGeneration / ReadStoreGens skip it —
    // verified, no manifest-schema change). Temp+rename like landing
    // chunks; absent at read is normal (old snapshots) → null, corrupt →
    // loud InvalidDataException, never a silent default.
    public sealed class CompactStateRecord
    {
        public int Generation { get; set; }
        public bool CompactEnabled { get; set; }
        public string? Algorithm { get; set; }
        public string QueriedUtc { get; set; } = "";

        public static string Write(string storeRoot, int generation, CompactOsState state, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(log);
            var manifestsDir = Path.Combine(storeRoot, "manifests");
            Directory.CreateDirectory(manifestsDir);
            var record = new CompactStateRecord
            {
                Generation = generation,
                CompactEnabled = state.Enabled,
                Algorithm = state.Algorithm,
                QueriedUtc = DateTime.UtcNow.ToString("o"),
            };
            var path = Path.Combine(manifestsDir, $"{generation:D6}.compact.json");
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record));
            File.Move(tmp, path, true);
            log.Info($"recorded CompactOS state for gen {generation}: {(state.Enabled ? "on" : "off")}");
            return path;
        }

        public static CompactStateRecord? Read(string storeRoot, int generation)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            var path = Path.Combine(storeRoot, "manifests", $"{generation:D6}.compact.json");
            if (!File.Exists(path))
                return null;
            CompactStateRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<CompactStateRecord>(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"CompactOS sidecar {path} is not valid JSON.", ex);
            }
            if (record is null)
                throw new InvalidDataException($"CompactOS sidecar {path} is empty.");
            if (record.Generation != generation)
                throw new InvalidDataException($"CompactOS sidecar {path} is for gen {record.Generation}, not gen {generation}.");
            return record;
        }
    }
}
