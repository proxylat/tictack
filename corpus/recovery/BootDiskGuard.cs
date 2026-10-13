using System;
using System.IO;

namespace Corpus
{
    // Restore safety gate: never touch the disk this machine booted from
    // without --allow-boot-disk, and never anything without typing the
    // target token. Linux has no boot-disk concept here: always false.
    public static class BootDiskGuard
    {
        // Device path of the boot disk (\\.\PhysicalDriveN), "unknown-linux"
        // off Windows, or "" when unresolvable (spanned volume, no rights).
        public static string SystemDiskDevice()
        {
            if (!OperatingSystem.IsWindows())
                return "unknown-linux";
            try
            {
                var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                if (string.IsNullOrEmpty(systemRoot))
                    return "";
                var volume = @"\\.\" + systemRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (NativeDisk.TryVolumeDiskNumber(volume, out var disk))
                    return $@"\\.\PhysicalDrive{disk}";
                return "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // File targets are never the boot disk (a file overwrite is not a
        // boot kill); raw targets compare against the system volume's disk.
        public static bool IsBootDisk(IBlockTarget target, out string bootDisk)
        {
            if (!OperatingSystem.IsWindows())
            {
                bootDisk = "unknown-linux";
                return false;
            }
            ArgumentNullException.ThrowIfNull(target);
            bootDisk = SystemDiskDevice();
            if (target is not RawDiskTarget || string.IsNullOrEmpty(bootDisk) || bootDisk == "unknown-linux")
                return false;
            return string.Equals(target.DisplayName, bootDisk, StringComparison.OrdinalIgnoreCase);
        }

        // Type-the-name token: DISK2-931GB for block devices (floor GB, no
        // spaces so it is typable), FILE-<name>-<bytes> for files. Stable:
        // same input always yields the same token.
        public static string TargetToken(string display, long capacity)
        {
            ArgumentNullException.ThrowIfNull(display);
            if (TryParsePhysicalDrive(display, out var number))
                return $"DISK{number}-{capacity / (1024L * 1024 * 1024)}GB";
            var name = Path.GetFileName(display);
            if (string.IsNullOrEmpty(name))
                name = "target";
            return $"FILE-{Sanitize(name)}-{capacity}";
        }

        public static bool Confirm(string? supplied, string expected) =>
            string.Equals(supplied, expected, StringComparison.Ordinal);

        internal static bool TryParsePhysicalDrive(string display, out int number)
        {
            number = 0;
            const string marker = "PhysicalDrive";
            var i = display.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                return false;
            return int.TryParse(display.AsSpan(i + marker.Length).Trim(), out number);
        }

        private static string Sanitize(string name)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
