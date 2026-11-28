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
        private const string RecoveryType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

        public static string Generate(DiskLayout layout, int targetDisk, bool formatPartitions = true)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (targetDisk < 0)
                throw new ArgumentOutOfRangeException(nameof(targetDisk), "target disk must be non-negative (got: " + targetDisk + ")");
            var sb = new StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var isMbr = string.Equals(layout.Style, "mbr", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine(inv, $"rem Corpus bare-disk prep for {(isMbr ? "MBR" : "GPT")} disk {layout.DiskGuid} ({layout.DiskSizeBytes} bytes) — REVIEW BEFORE RUNNING.");
            sb.AppendLine("rem Verify the disk number with Corpus.exe --list-disks first, then run: diskpart /s <thisfile>");
            sb.AppendLine(inv, $"rem 'clean' ERASES disk {targetDisk}. Bare-disk prep stays a reviewed manual step unless --repair-boot applies it.");
            sb.AppendLine(inv, $"select disk {targetDisk}");
            sb.AppendLine("clean");
            if (isMbr)
            {
                sb.AppendLine("convert mbr");
            }
            else
            {
                sb.AppendLine("convert gpt");
                sb.AppendLine("rem Delete diskpart's auto-created front MSR (ours is placed by offset below).");
                sb.AppendLine("select partition 1");
                sb.AppendLine("delete partition override");
            }
            foreach (var p in layout.Partitions)
                AppendPartitionCommands(sb, inv, p, formatPartitions, isMbr);
            return sb.ToString();
        }

        // Diskpart only accepts MiB sizes / KiB offsets; non-aligned sources fail loud
        // rather than silently shifting later partitions. Never emit `assign`: a mounted
        // volume locks the disk against raw-sector writes (letters come post-verify).
        private static void AppendPartitionCommands(StringBuilder sb, System.Globalization.CultureInfo inv, PartitionRecord p, bool formatPartitions, bool isMbr)
        {
            if (p.LengthBytes % (1024L * 1024L) != 0 && !IsRecovery(p))
                throw new InvalidDataException($"partition {p.PartitionNumber}: length {p.LengthBytes} is not MiB-exact, cannot recreate via diskpart");
            if (p.OffsetBytes % 1024L != 0)
                throw new InvalidDataException($"partition {p.PartitionNumber}: offset {p.OffsetBytes} is not KiB-exact, cannot recreate via diskpart");
            var sizeMb = Math.Max(1L, p.LengthBytes / (1024L * 1024L));
            var offsetKb = p.OffsetBytes / 1024L;
            // No `assign` anywhere: a mounted volume locks its disk against raw-sector writes.
            sb.AppendLine(inv, $"rem partition {p.PartitionNumber} {p.Name} ({p.LengthBytes} bytes at offset {p.OffsetBytes})");
            if (!isMbr && IsEsp(p))
            {
                sb.AppendLine(inv, $"create partition efi size={sizeMb} offset={offsetKb}");
                if (formatPartitions)
                    sb.AppendLine("format fs=fat32 quick");
            }
            else if (!isMbr && IsMsr(p))
            {
                sb.AppendLine(inv, $"create partition msr size={sizeMb} offset={offsetKb}");
            }
            else
            {
                sb.AppendLine(inv, $"create partition primary size={sizeMb} offset={offsetKb}");
                if (isMbr && (p.AttributeFlags & 0x80) != 0)
                    sb.AppendLine("active");
                if (!isMbr && IsRecovery(p))
                {
                    sb.AppendLine(inv, $"set id=\"{RecoveryType}\"");
                    sb.AppendLine("gpt attributes=0x8000000000000001");
                }
                if (formatPartitions)
                {
                    if (string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("format fs=fat32 quick");
                    else if (!string.Equals(p.Filesystem, "none", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("format fs=ntfs quick");
                }
            }
        }

        private static bool IsEsp(PartitionRecord p) =>
            string.Equals(p.TypeGuid, EspType, StringComparison.OrdinalIgnoreCase)
            || (p.PartitionNumber == 1 && string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase));

        private static bool IsMsr(PartitionRecord p) =>
            string.Equals(p.TypeGuid, MsrType, StringComparison.OrdinalIgnoreCase);

        private static bool IsRecovery(PartitionRecord p) =>
            string.Equals(p.TypeGuid, RecoveryType, StringComparison.OrdinalIgnoreCase);
    }
}
