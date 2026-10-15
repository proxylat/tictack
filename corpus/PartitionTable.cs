using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using TicTack;

namespace Corpus
{
    // Whole-disk capture enumeration (disk-spec §1.1): protective-MBR check
    // → GPT header check → entry parse. Windows-only I/O (ReadDisk opens
    // \\.\PhysicalDriveN elevated, same posture as VssSnapshot.Create);
    // the parsers are pure and Linux-testable. v1 assumes 512-byte logical
    // sectors and GPT-only tables — anything else fails loud, never silent.
    // (DiskLayout/PartitionRecord live in LayoutStore.cs — the store lane's
    // home for the §2 JSON schema; this file only fills them.)
    public static class PartitionTable
    {
        public const int SectorSize = 512;
        private const int EntrySize = 128;

        private static readonly Guid EspType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        // Live-disk proven (VM disk0 partition 2): third group is 0B5C —
        // 0C5C was a typo that unskipped every real MSR (repair always
        // refused, image-disk captured the MSR as data).
        private static readonly Guid MsrType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
        // UEFI spec: fourth group is 85D2 — 85D3 silently unrefused
        // dynamic disks (block copy without LDM reassembly restores garbage).
        private static readonly Guid LdmType = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");

        public static DiskLayout ReadDisk(int diskNumber, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(log);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("whole-disk capture needs Windows (disk " + diskNumber + ")");
            if (!IsElevated())
                throw new InvalidOperationException("whole-disk capture needs elevation — run Corpus.exe elevated.");
            var device = $@"\\.\PhysicalDrive{diskNumber}";
            byte[] header;
            using (var handle = NativeDisk.OpenHandle(device, read: true))
            using (var fs = new FileStream(handle, FileAccess.Read))
            {
                var mbr = new byte[SectorSize];
                fs.Seek(0, SeekOrigin.Begin);
                fs.ReadExactly(mbr, 0, SectorSize);
                CheckProtectiveMbr(mbr);
                header = new byte[SectorSize];
                fs.Seek(SectorSize, SeekOrigin.Begin);
                fs.ReadExactly(header, 0, SectorSize);
            }
            CheckHeader(header);
            var entriesLba = BitConverter.ToInt64(header, 72);
            var entryCount = BitConverter.ToInt32(header, 80);
            var entrySize = BitConverter.ToInt32(header, 84);
            if (entrySize != EntrySize)
                throw new InvalidDataException($"GPT entry size {entrySize}, want {EntrySize} (disk {diskNumber})");
            if (entryCount <= 0 || entriesLba <= 0)
                throw new InvalidDataException($"GPT header names {entryCount} entries at LBA {entriesLba} (disk {diskNumber})");
            var entriesBytes = new byte[(long)entryCount * EntrySize];
            using (var handle = NativeDisk.OpenHandle(device, read: true))
            using (var fs = new FileStream(handle, FileAccess.Read))
            {
                fs.Seek(entriesLba * SectorSize, SeekOrigin.Begin);
                fs.ReadExactly(entriesBytes, 0, entriesBytes.Length);
            }
            var storedEntriesCrc = BitConverter.ToUInt32(header, 88);
            if (Crc32(entriesBytes) != storedEntriesCrc)
                throw new InvalidDataException("GPT entries CRC mismatch — 4Kn disks (4096-byte sectors) misparse as 512B and fail here; 4Kn is unsupported in v1");
            var parts = ParseEntries(entriesBytes, entryCount);
            foreach (var p in parts)
                p.PartitionKey = $"disk{diskNumber}p{p.PartitionNumber}:{p.UniqueGuid}";
            SniffFilesystems(device, parts, log);
            var diskGuid = new Guid(header.AsSpan(56, 16)).ToString("d");
            log.Info($"disk {diskNumber} {diskGuid}: {parts.Count} partitions");
            return new DiskLayout
            {
                Generation = 0,
                DiskNumber = diskNumber,
                DiskGuid = diskGuid,
                DiskSizeBytes = NativeDisk.GetLengthBytes(device),
                SectorSizeBytes = SectorSize,
                Partitions = parts,
            };
        }

        // Protective MBR: boot signature + one 0xEE entry. Anything else is
        // an MBR disk — refused loud, GPT-only in v1.
        internal static void CheckProtectiveMbr(byte[] sector)
        {
            ArgumentNullException.ThrowIfNull(sector);
            if (sector.Length < SectorSize)
                throw new InvalidDataException($"MBR sector is {sector.Length} bytes, want {SectorSize}");
            if (sector[510] != 0x55 || sector[511] != 0xAA)
                throw new InvalidDataException("missing MBR boot signature 0x55AA — not a partitioned disk");
            for (var i = 0; i < 4; i++)
            {
                if (sector[446 + i * 16 + 4] == 0xEE)
                    return;
            }
            throw new InvalidDataException("mbr disk without a protective 0xEE entry — mbr layout is refused, gpt-only in v1");
        }

        // GPT header at LBA1: signature + CRC32 over the header-size bytes
        // (CRC field zeroed). A 4Kn disk read as 512B sectors lands here
        // with a bad CRC — the error names 4Kn so it reads as a refusal.
        public static void CheckHeader(byte[] sector)
        {
            ArgumentNullException.ThrowIfNull(sector);
            if (sector.Length < SectorSize)
                throw new InvalidDataException($"GPT header sector is {sector.Length} bytes, want {SectorSize}");
            if (Encoding.ASCII.GetString(sector, 0, 8) != "EFI PART")
                throw new InvalidDataException("missing GPT signature 'EFI PART' at LBA1");
            var headerSize = BitConverter.ToUInt32(sector, 12);
            if (headerSize < 92 || headerSize > sector.Length)
                throw new InvalidDataException($"GPT header size {headerSize} is implausible");
            var stored = BitConverter.ToUInt32(sector, 16);
            var copy = new byte[headerSize];
            Buffer.BlockCopy(sector, 0, copy, 0, (int)headerSize);
            copy[16] = copy[17] = copy[18] = copy[19] = 0;
            if (Crc32(copy) != stored)
                throw new InvalidDataException("GPT header CRC mismatch — 4Kn disks (4096-byte sectors) misparse as 512B and fail here; 4Kn is unsupported in v1");
        }

        // Pure entry parse: N 128-byte slots in on-disk order. Zero-type
        // slots are free space (omitted, not even skipped); MSR slots stay
        // in the layout as skipped-with-range; LDM refuses the whole disk
        // (block copy without LDM reassembly restores garbage).
        public static List<PartitionRecord> ParseEntries(byte[] entriesBytes, int count)
        {
            ArgumentNullException.ThrowIfNull(entriesBytes);
            if (count < 0 || entriesBytes.Length < count * EntrySize)
                throw new ArgumentException($"need {count * EntrySize} entry bytes, got {entriesBytes.Length}", nameof(entriesBytes));
            var parts = new List<PartitionRecord>();
            for (var i = 0; i < count; i++)
            {
                var off = i * EntrySize;
                var type = new Guid(entriesBytes.AsSpan(off, 16));
                if (type == Guid.Empty)
                    continue;
                if (type == LdmType)
                    throw new InvalidDataException("dynamic disk (LDM member partition present) — block copy without LDM reassembly restores garbage; dynamic disks are refused in v1");
                var unique = new Guid(entriesBytes.AsSpan(off + 16, 16));
                var first = BitConverter.ToInt64(entriesBytes, off + 32);
                var last = BitConverter.ToInt64(entriesBytes, off + 40);
                if (last < first)
                    throw new InvalidDataException($"partition slot {i + 1}: last LBA {last} precedes first LBA {first}");
                var name = Encoding.Unicode.GetString(entriesBytes, off + 56, 72).TrimEnd('\0');
                var skipped = type == MsrType;
                parts.Add(new PartitionRecord
                {
                    PartitionNumber = i + 1,
                    Name = name,
                    TypeGuid = type.ToString("d"),
                    UniqueGuid = unique.ToString("d"),
                    FirstLba = first,
                    LastLba = last,
                    OffsetBytes = first * SectorSize,
                    LengthBytes = (last - first + 1) * SectorSize,
                    Filesystem = type == EspType ? "fat32" : type == MsrType ? "none" : "unknown",
                    Skipped = skipped,
                    SkipReason = skipped ? "msr-reserved" : null,
                    SnapshotGeneration = 0,
                    PartitionKey = "",
                });
            }
            return parts;
        }

        // Boot-sector sniff: GPT type GUIDs don't name filesystems
        // (basic-data spans NTFS/exFAT/BitLocker/raw), and CheckLayout + the
        // diskpart plan need the real answer. One 512B read per unknown
        // partition; unreadable stays "unknown", never throws.
        private static void SniffFilesystems(string device, List<PartitionRecord> parts, ILogger log)
        {
            try
            {
                using var handle = NativeDisk.OpenHandle(device, read: true);
                using var fs = new FileStream(handle, FileAccess.Read);
                foreach (var p in parts)
                {
                    if (p.Skipped || !string.Equals(p.Filesystem, "unknown", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var sector = new byte[SectorSize];
                        fs.Seek(p.OffsetBytes, SeekOrigin.Begin);
                        fs.ReadExactly(sector, 0, SectorSize);
                        var sniffed = SniffFilesystem(sector);
                        if (sniffed != "unknown")
                        {
                            p.Filesystem = sniffed;
                            log.Debug($"partition {p.PartitionNumber}: filesystem {sniffed} (boot-sector sniff)");
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log.Debug($"partition {p.PartitionNumber}: filesystem sniff skipped ({ex.Message})");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Debug("filesystem sniff skipped: " + ex.Message);
            }
        }

        // Pure for tests. Only names filesystems we act on; BitLocker FVE
        // ("-FVE-FS-"), short buffers, and anything else stay "unknown"
        // (honest, never a guess CheckLayout would trust for the OS pick).
        internal static string SniffFilesystem(byte[] sector)
        {
            ArgumentNullException.ThrowIfNull(sector);
            if (MatchAscii(sector, 3, "NTFS    "))
                return "ntfs";
            if (MatchAscii(sector, 3, "EXFAT   "))
                return "exfat";
            if (MatchAscii(sector, 82, "FAT32   "))
                return "fat32";
            if (MatchAscii(sector, 54, "FAT12   "))
                return "fat12";
            if (MatchAscii(sector, 54, "FAT16   "))
                return "fat16";
            return "unknown";
        }

        private static bool MatchAscii(byte[] sector, int offset, string magic)
        {
            if (sector.Length < offset + magic.Length) return false;
            for (var i = 0; i < magic.Length; i++)
                if (sector[offset + i] != (byte)magic[i]) return false;
            return true;
        }

        private static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // CRC-32/ISO-HDLC (the UEFI GPT checksum). Hand-rolled: adding a
        // System.IO.Hashing package for one call is dependency bloat.
        internal static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
