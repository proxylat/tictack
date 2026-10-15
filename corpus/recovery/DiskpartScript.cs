using System;
using System.IO;
using System.Text;

namespace Corpus
{
    // Bare-disk prep (disk-spec §6): v1 never partitions automatically —
    // Generate prints the script for --dry-run and <target>.diskpart.txt;
    // the operator reviews and applies it by hand.
    public static class DiskpartScript
    {
        private const string EspType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
        private const string MsrType = "e3c9e316-0b5c-4db8-817d-f92df00215ae";

        public static string Generate(DiskLayout layout, bool formatPartitions = true)
        {
            ArgumentNullException.ThrowIfNull(layout);
            var sb = new StringBuilder();
            sb.AppendLine($"rem Corpus bare-disk prep for GPT disk {layout.DiskGuid} ({layout.DiskSizeBytes} bytes) — REVIEW BEFORE RUNNING.");
            sb.AppendLine("rem Verify the disk number with Corpus.exe --list-disks first, then run: diskpart /s <thisfile>");
            sb.AppendLine("rem 'clean' ERASES the selected disk. Bare-disk prep stays a reviewed manual step in v1.");
            sb.AppendLine($"select disk {layout.DiskNumber}");
            sb.AppendLine("clean");
            sb.AppendLine("convert gpt");
            foreach (var p in layout.Partitions)
            {
                // Exact recreation: diskpart only speaks MiB sizes / KiB
                // offsets. Non-aligned sources refuse loud rather than
                // silently shifting every later partition (Windows always
                // lays out aligned, so this fires only on foreign tools).
                if (p.LengthBytes % (1024L * 1024L) != 0)
                    throw new InvalidDataException($"partition {p.PartitionNumber}: length {p.LengthBytes} is not MiB-exact, cannot recreate via diskpart");
                if (p.OffsetBytes % 1024L != 0)
                    throw new InvalidDataException($"partition {p.PartitionNumber}: offset {p.OffsetBytes} is not KiB-exact, cannot recreate via diskpart");
                var sizeMb = Math.Max(1L, p.LengthBytes / (1024L * 1024L));
                var offsetKb = p.OffsetBytes / 1024L;
                sb.AppendLine($"rem partition {p.PartitionNumber} {p.Name} ({p.LengthBytes} bytes at offset {p.OffsetBytes})");
                if (IsEsp(p))
                {
                    sb.AppendLine($"create partition efi size={sizeMb} offset={offsetKb}");
                    if (formatPartitions)
                        sb.AppendLine("format fs=fat32 quick");
                    sb.AppendLine("assign");
                }
                else if (IsMsr(p))
                {
                    sb.AppendLine($"create partition msr size={sizeMb} offset={offsetKb}");
                }
                else
                {
                    sb.AppendLine($"create partition primary size={sizeMb} offset={offsetKb}");
                    if (formatPartitions)
                    {
                        if (string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine("format fs=fat32 quick");
                        else if (!string.Equals(p.Filesystem, "none", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine("format fs=ntfs quick");
                    }
                    // Filesystem "none" (raw, never MSR-skipped): created but unformatted.
                    if (!string.Equals(p.Filesystem, "none", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("assign");
                }
            }
            return sb.ToString();
        }

        private static bool IsEsp(PartitionRecord p) =>
            string.Equals(p.TypeGuid, EspType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase);

        private static bool IsMsr(PartitionRecord p) =>
            p.Skipped || string.Equals(p.TypeGuid, MsrType, StringComparison.OrdinalIgnoreCase);
    }
}
