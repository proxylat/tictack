using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // Whole-disk rung, store side (spec §2/§3/§5): per-partition manifests in
    // the SAME store plus one sealed DiskLayout sidecar. The loop runs over an
    // explicit DiskCapturePlan so Linux tests can drive it with
    // FileBlockSource + FakeChangeSource; the real GPT+VSS enumeration is the
    // capture lane's and arrives via the CapturePlan hook (until wired,
    // ImageDisk fails loud — Linux disk verbs exit 1 posture).
    public sealed record DiskImageOptions(bool Incremental = false, bool ForceFull = false, int ParentLayoutGen = 0);

    // One partition's capture input: layout-derived identity plus a readable
    // source. Source is null exactly when Skipped (MSR: byte range kept in the
    // layout, never snapshotted).
    public sealed record PartitionImageInput(
        int PartitionNumber,
        string Name,
        string TypeGuid,
        string UniqueGuid,
        long FirstLba,
        long LastLba,
        long OffsetBytes,
        long LengthBytes,
        string Filesystem,
        bool Skipped,
        string? SkipReason,
        IBlockSource? Source,
        IChangeSource? ChangeSource);

    public sealed record DiskCapturePlan(
        string DiskGuid,
        long DiskSizeBytes,
        int SectorSizeBytes,
        IReadOnlyList<PartitionImageInput> Partitions);

    public static class DiskImager
    {
        // Identity is the GPT unique GUID, never the partition number
        // (numbers recycle on delete/recreate — misattribution restores bytes
        // to the wrong OS).
        public static string PartitionKey(int diskNumber, int partitionNumber, string uniqueGuid) =>
            $"disk{diskNumber}p{partitionNumber}:{uniqueGuid}";

        // Capture-lane hook: GPT parse + VSS set + ESP live read build one
        // plan per disk. Unset = no backend (tests pass plans directly).
        public static Func<int, ILogger, DiskCapturePlan>? CapturePlan { get; set; }

        // Capture-side BitLocker refuse (spec §1.1/§1.2): a locked partition
        // images ciphertext with no escrow — fail loud naming the partition,
        // never capture-opaque. Best-effort: unresolvable letters (Linux,
        // unmounted) skip the probe; manage-bde absent/broken skips too.
        // VM loop (e) gates real behavior; Linux CI only pins the skip.
        internal static void RefuseIfLocked(int diskNumber, IEnumerable<PartitionImageInput> partitions, ILogger log)
        {
            if (!OperatingSystem.IsWindows())
                return;
            foreach (var part in partitions)
            {
                if (part.Skipped)
                    continue;
                var letter = EspLiveSource.TryFindDriveLetter(diskNumber, part.OffsetBytes);
                if (letter == null)
                    continue;
                string stdout;
                try
                {
                    var r = ProcessRunner.RunAsync($"manage-bde -status {letter}", AppDomain.CurrentDomain.BaseDirectory, redirect: true, timeoutMs: 15000).GetAwaiter().GetResult();
                    if (r.TimedOut || r.ExitCode != 0)
                        continue;
                    stdout = r.Stdout;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    continue;
                }
                if (stdout.Contains("Locked", StringComparison.OrdinalIgnoreCase)
                    && !stdout.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"refusing disk image: partition {part.PartitionNumber} ({part.Name}, {letter}) is BitLocker-locked (unlock with manage-bde -unlock {letter} first)");
            }
        }
    }
}
