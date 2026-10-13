using System;
using System.IO;
using DiscUtils.Streams;
using DiscUtils.Vhdx;

namespace Corpus
{
    // VHDX edge exporter (system-image-future.md §5 rung 3): dynamic VHDX via
    // LTRData.DiscUtils (MIT, pure C# — zero ship cost, no GPL shell-out).
    // The chunk store stays canonical; VHDX is rendered once at the edge
    // from decoded chunk plaintext, like any other export. Only DiscUtils
    // coupling lives here — Store passes a content writer, never names Disk.
    public static class VhdxExporter
    {
        private const long Align = 1024 * 1024;

        // VHDX virtual size is 1 MiB-aligned; the tail past VolumeLength is
        // never written and reads back as zero. Empty volumes fail loudly —
        // DiscUtils would throw its own less obvious error.
        public static long AlignedCapacity(long volumeLength)
        {
            if (volumeLength <= 0)
                throw new InvalidDataException("cannot export an empty snapshot to VHDX");
            return ((volumeLength + Align - 1) / Align) * Align;
        }

        public static void Export(string vhdxPath, long volumeLength, Action<Stream> writeContent)
        {
            ArgumentNullException.ThrowIfNull(vhdxPath);
            ArgumentNullException.ThrowIfNull(writeContent);
            using var file = new FileStream(vhdxPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using var disk = Disk.InitializeDynamic(file, Ownership.None, AlignedCapacity(volumeLength));
            writeContent(disk.Content);
        }
    }
}
