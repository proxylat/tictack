using System;
using System.IO;
using System.Text;

namespace Corpus
{
    // Bare-disk prep (disk-spec §6): Generate prints the script for
    // --dry-run and <target>.diskpart.txt; the operator reviews and
    // applies it by hand, or --repair-boot applies it orchestrated via
    // ApplyPhase. The target disk is ALWAYS explicit — the layout's
    // source disk number is never a default (a restore to disk 2 must
    // never clean disk 0).
    public static class DiskpartScript
    {
        private const string EspType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
        private const string MsrType = "e3c9e316-0b5c-4db8-817d-f92df00215ae";

        public static string Generate(DiskLayout layout, int targetDisk, bool formatPartitions = true)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (targetDisk < 0)
                throw new ArgumentOutOfRangeException(nameof(targetDisk), "target disk must be non-negative (got: " + targetDisk + ")");
            var sb = new StringBuilder();
            sb.AppendLine($"rem Corpus bare-disk prep for GPT disk {layout.DiskGuid} ({layout.DiskSizeBytes} bytes) — REVIEW BEFORE RUNNING.");
            sb.AppendLine("rem Verify the disk number with Corpus.exe --list-disks first, then run: diskpart /s <thisfile>");
            sb.AppendLine($"rem 'clean' ERASES disk {targetDisk}. Bare-disk prep stays a reviewed manual step unless --repair-boot applies it.");
            sb.AppendLine($"select disk {targetDisk}");
            sb.AppendLine("clean");
            sb.AppendLine("convert gpt");
            // diskpart auto-creates a 15MB front MSR on convert (proven
            // 26100; our ESP at 1MB would collide with "not enough usable
            // space"). Delete it: select/delete accept no `noerr`, so a
            // hypothetical no-auto-MSR build aborts LOUD here, before any
            // create runs and long before any byte streams — safe, not silent.
            sb.AppendLine("rem Delete diskpart's auto-created front MSR (ours is placed by offset below).");
            sb.AppendLine("select partition 1");
            sb.AppendLine("delete partition override");
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
                // No `assign` anywhere in this script, either variant: a
                // mounted volume locks its disk against raw-sector writes
                // (UnauthorizedAccessException on the first streamed byte —
                // proven on-box). Letters come post-verify from
                // LocateVolumes, which assigns only the ESP + OS it needs
                // for bcdboot.
                sb.AppendLine($"rem partition {p.PartitionNumber} {p.Name} ({p.LengthBytes} bytes at offset {p.OffsetBytes})");
                if (IsEsp(p))
                {
                    sb.AppendLine($"create partition efi size={sizeMb} offset={offsetKb}");
                    if (formatPartitions)
                        sb.AppendLine("format fs=fat32 quick");
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
