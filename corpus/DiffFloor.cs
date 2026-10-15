using System;

namespace Corpus
{
    // VSS diff-area preflight floor: how much free space a volume must have
    // before we attempt a snapshot. Pure function so the boundaries are
    // unit-testable without Windows, elevation, or a real volume.
    // shortcut: true shadow-storage accounting (Win32_ShadowStorage
    // free/max) replaces this when multi-hour captures exist; short
    // loops never grow past this.
    internal static class DiffFloor
    {
        // 10% of the volume: Windows caps default shadow-storage growth at a
        // tenth of the source volume, so the diff area may legitimately grow
        // that far while a capture holds the snapshot open.
        private const long PercentDivisor = 10;

        // Tiered minimum from the documented MinDiffAreaFileSize defaults
        // (VSS preallocates at least this much diff area, so less free space
        // means the snapshot cannot even start allocating):
        // 32 MiB below 500 MB, 320 MiB at/above (MSDN "Registry Keys and
        // Values for Backup and Restore"). The small tier keeps toy volumes
        // imageable; a single 320 MiB floor would ban every sub-320 MB volume.
        internal const long SmallVolumeBytes = 500L * 1024 * 1024;
        internal const long SmallMinBytes = 32L * 1024 * 1024;
        internal const long LargeMinBytes = 320L * 1024 * 1024;

        // 1 GiB cap: the previous fixed floor, kept as the ceiling — short
        // captures never grow the diff past this, and large volumes keep the
        // exact behavior they already had.
        internal const long MaxBytes = 1L * 1024 * 1024 * 1024;

        internal static long RequiredHeadroomBytes(long totalBytes)
        {
            if (totalBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalBytes), "volume size must be positive (got: " + totalBytes + ")");
            var floor = totalBytes < SmallVolumeBytes ? SmallMinBytes : LargeMinBytes;
            return Math.Min(Math.Max(totalBytes / PercentDivisor, floor), MaxBytes);
        }

        internal static bool MeetsFloor(long totalBytes, long freeBytes) =>
            freeBytes >= RequiredHeadroomBytes(totalBytes);
    }
}
