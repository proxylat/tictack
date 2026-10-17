using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    public sealed record WimImageEntry(int Index, string Name, long? SizeBytes);

    // Info dossier (research item 5): index count + per-index entries from
    // DISM /Get-WimInfo or wimlib `info` stdout (both colon shapes), a
    // best-effort Languages token scan, and CompactState from a sidecar file
    // only when a path is given (no store knowledge here).
    public sealed record WimInfoReport(
        int IndexCount,
        IReadOnlyList<WimImageEntry> Images,
        IReadOnlyList<string> Languages,
        string? CompactState)
    {
        public static WimInfoReport Get(string imagePath, IWimRunner runner, ILogger log, string? compactSidecarPath = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(log);
            if (!File.Exists(imagePath))
                throw new SingleFileSourceNotFoundException(imagePath);
            var wimlib = runner.Backend == WimBackendKind.Wimlib;
            var args = wimlib
                ? $"info {ProcessRunner.QuoteArg(imagePath)}"
                : $"/Get-WimInfo /WimFile:{ProcessRunner.QuoteArg(imagePath)}";
            var r = runner.Run(args);
            if (r.ExitCode != 0)
                throw wimlib
                    ? new WimBackendException(imagePath, args, r)
                    : new SingleFileDismException(imagePath, args, r);
            var (images, languages) = Parse(r.Stdout);
            string? compact = compactSidecarPath is null ? null : ReadCompactState(compactSidecarPath);
            log.Info($"info {imagePath}: {images.Count} image(s)");
            return new WimInfoReport(images.Count, images, languages, compact);
        }

        // Single pass over both output shapes. wimlib is colon-tight
        // ("Index: 1", "Name: x", "Total Bytes: 6"); DISM is colon-spaced
        // ("Index : 1", "Name : x", "Size : ..."). Size/Languages are
        // best-effort: absent lines leave null/empty, never an error.
        internal static (List<WimImageEntry> Images, List<string> Languages) Parse(string stdout)
        {
            ArgumentNullException.ThrowIfNull(stdout);
            var images = new List<WimImageEntry>();
            var languages = new List<string>();
            foreach (var line in stdout.Split('\n'))
            {
                var t = line.Trim();
                if (IsField(t, "Index", out var indexValue))
                {
                    var n = indexValue.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    images.Add(new WimImageEntry(int.TryParse(n, out var parsed) ? parsed : images.Count + 1, "", null));
                }
                else if (images.Count > 0 && IsField(t, "Name", out var name))
                {
                    images[^1] = images[^1] with { Name = name };
                }
                else if (images.Count > 0 && (IsField(t, "Total Bytes", out var total) || IsField(t, "Size", out total)))
                {
                    var token = total.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Replace(",", "", StringComparison.Ordinal);
                    if (long.TryParse(token, out var size))
                        images[^1] = images[^1] with { SizeBytes = size };
                }
                if (IsField(t, "Languages", out var langs) || IsField(t, "Language", out langs))
                    languages.AddRange(langs.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries));
            }
            return (images, languages);
        }

        private static bool IsField(string trimmed, string field, out string value)
        {
            value = "";
            if (!trimmed.StartsWith(field, StringComparison.OrdinalIgnoreCase))
                return false;
            var rest = trimmed[field.Length..].TrimStart();
            if (!rest.StartsWith(":", StringComparison.Ordinal))
                return false;
            value = rest[1..].Trim();
            return true;
        }

        // Sidecar (CompactStateRecord JSON) read by path only: missing file
        // is normal (old snapshots) -> null; corrupt -> loud, never a
        // silent default.
        private static string? ReadCompactState(string path)
        {
            if (!File.Exists(path))
                return null; // absent is normal (old snapshots) -> null CompactState
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidDataException($"CompactOS sidecar {path} cannot be read: {ex.Message}", ex);
            }
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("CompactEnabled", out var enabled)
                    || (enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False))
                    throw new InvalidDataException($"CompactOS sidecar {path} has no CompactEnabled flag.");
                var algo = root.TryGetProperty("Algorithm", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString()
                    : null;
                return enabled.GetBoolean()
                    ? (string.IsNullOrEmpty(algo) ? "enabled" : $"enabled ({algo})")
                    : "disabled";
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"CompactOS sidecar {path} is not valid JSON.", ex);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{IndexCount} image(s)");
            foreach (var img in Images)
                sb.AppendLine($"  [{img.Index}] {(string.IsNullOrEmpty(img.Name) ? "(unnamed)" : img.Name)} ({(img.SizeBytes.HasValue ? img.SizeBytes.Value.ToString() + " bytes" : "size unknown")})");
            sb.AppendLine(Languages.Count > 0 ? "Languages: " + string.Join(" ", Languages) : "Languages: none listed");
            sb.Append("Compact: " + (CompactState ?? "n/a"));
            return sb.ToString();
        }
    }
}
