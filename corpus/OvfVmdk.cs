using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using TicTack;

namespace Corpus
{
    public sealed record VmdkExportResult(long CapacityBytes, long PopulatedBytes, long FileBytes, string Sha256Hex = "");

    public sealed record OvfExportResult(
        string OvfPath,
        string VmdkPath,
        string ManifestPath,
        long CapacityBytes,
        long PopulatedBytes,
        long VmdkBytes);

    // Pure-C# streamOptimized VMDK (VMware Virtual Disk Format 5.0) + DMTF OVF 1.1
    // (.ovf / .mf / .ova) edge exporter. Zero extra NuGet dependencies: uses built-in
    // ZLibStream (RFC 1950 DEFLATE) and System.Formats.Tar (POSIX ustar).
    public static class OvfExporter
    {
        internal const int SectorSize = 512;
        internal const int GrainSectors = 128; // 64 KiB grains
        internal const int GrainBytes = GrainSectors * SectorSize;
        internal const int GtesPerGt = 512; // 32 MiB per grain table (4 sectors)
        internal const int DescriptorSectors = 20;
        internal const ulong OverHeadSectors = 128;
        internal const uint VmdkMagic = 0x564D444Bu; // "KDMV"
        internal const uint VmdkVersion = 3u;
        internal const uint StreamOptimizedFlags = 0x00030001u;
        internal const ulong GdAtEnd = 0xFFFFFFFFFFFFFFFFul;
        internal const uint MarkerEos = 0u;
        internal const uint MarkerGt = 1u;
        internal const uint MarkerGd = 2u;
        internal const uint MarkerFooter = 3u;

        public static long AlignedCapacity(long volumeLength)
        {
            if (volumeLength <= 0)
                throw new InvalidDataException("cannot export an empty snapshot to VMDK/OVF/OVA");
            return ((volumeLength + GrainBytes - 1) / GrainBytes) * GrainBytes;
        }

        private const int IoBufferBytes = 4 * 1024 * 1024;

        public static VmdkExportResult ExportVmdk(string vmdkPath, long volumeLength, Action<Stream> writeContent)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(vmdkPath);
            ArgumentNullException.ThrowIfNull(writeContent);
            var capacity = AlignedCapacity(volumeLength);
            var dir = Path.GetDirectoryName(Path.GetFullPath(vmdkPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using var file = new FileStream(vmdkPath, FileMode.Create, FileAccess.Write, FileShare.Read, IoBufferBytes, FileOptions.SequentialScan);
            using var hashStream = new HashingWriteStream(file);
            using var vmdk = new StreamOptimizedVmdkStream(hashStream, capacity, Path.GetFileName(vmdkPath));
            writeContent(vmdk);
            vmdk.Finish();
            file.Flush(true);
            return new VmdkExportResult(capacity, vmdk.PopulatedBytes, file.Length, hashStream.FinishHex());
        }

        public static OvfExportResult ExportOvf(string outPath, string vmName, long volumeLength, Action<Stream> writeContent, bool uefi = false, string storageController = "ahci")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outPath);
            ArgumentNullException.ThrowIfNull(writeContent);
            var full = Path.GetFullPath(outPath);
            string dir;
            string stem;
            if (full.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase))
            {
                dir = Path.GetDirectoryName(full) ?? ".";
                stem = SanitizeStem(Path.GetFileNameWithoutExtension(full));
            }
            else
            {
                dir = full;
                stem = SanitizeStem(vmName);
            }
            Directory.CreateDirectory(dir);
            var vmdkPath = Path.Combine(dir, stem + ".vmdk");
            var ovfPath = Path.Combine(dir, stem + ".ovf");
            var mfPath = Path.Combine(dir, stem + ".mf");

            var vmdk = ExportVmdk(vmdkPath, volumeLength, writeContent);
            var ovfXml = BuildOvfXml(stem, Path.GetFileName(vmdkPath), vmdk.CapacityBytes, vmdk.PopulatedBytes, vmdk.FileBytes, uefi, storageController);
            var ovfBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ovfXml);
            File.WriteAllBytes(ovfPath, ovfBytes);

            var ovfHash = Convert.ToHexString(SHA256.HashData(ovfBytes)).ToLowerInvariant();
            var mfText = BuildManifest(
                (Path.GetFileName(ovfPath), ovfHash),
                (Path.GetFileName(vmdkPath), vmdk.Sha256Hex));
            File.WriteAllText(mfPath, mfText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new OvfExportResult(ovfPath, vmdkPath, mfPath, vmdk.CapacityBytes, vmdk.PopulatedBytes, vmdk.FileBytes);
        }

        public static OvfExportResult ExportOva(string ovaPath, string vmName, long volumeLength, Action<Stream> writeContent, bool uefi = false, string storageController = "ahci")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ovaPath);
            ArgumentNullException.ThrowIfNull(writeContent);
            var fullOva = Path.GetFullPath(ovaPath);
            var dir = Path.GetDirectoryName(fullOva) ?? ".";
            Directory.CreateDirectory(dir);
            var stem = SanitizeStem(Path.GetFileNameWithoutExtension(fullOva) is { Length: > 0 } s ? s : vmName);
            var stage = Path.Combine(dir, ".corpus-ova-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                var ovf = ExportOvf(Path.Combine(stage, stem + ".ovf"), stem, volumeLength, writeContent, uefi, storageController);
                PackOvaArchive(fullOva, ovf.OvfPath, ovf.ManifestPath, ovf.VmdkPath);
                return ovf;
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // Streams a live or file IBlockSource directly into a forward-only VMDK stream:
        // skips unallocated NTFS clusters via ISparseBlockSource and zeros excluded file extents.
        // Uses a single reusable 4 MiB buffer (zero per-chunk LOH allocations or copies).
        public static void StreamBlockSource(IBlockSource source, Stream dest, long destOffset, IExclusionResolver? excl)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(dest);
            const int chunkBytes = 4 * 1024 * 1024;
            var exclBpc = excl?.BytesPerCluster ?? 0;
            var exclRuns = exclBpc > 0 ? excl!.ExcludedRuns() : null;
            bool[]? holeMap = source is ISparseBlockSource sparse
                ? Chunker.BuildHoleMap(sparse.GetAllocatedRuns(), sparse.BytesPerCluster, source.Length, chunkBytes)
                : null;
            using var input = source.OpenRead();
            dest.Seek(destOffset, SeekOrigin.Begin);
            var buf = ArrayPool<byte>.Shared.Rent(chunkBytes);
            try
            {
                long index = 0;
                long offset = 0;
                while (offset < source.Length)
                {
                    var want = (int)Math.Min(chunkBytes, source.Length - offset);
                    if (holeMap != null && index < holeMap.Length && holeMap[index])
                    {
                        input.Seek(want, SeekOrigin.Current);
                        dest.Seek(want, SeekOrigin.Current);
                        offset += want;
                        index++;
                        continue;
                    }
                    var filled = ReadFull(input, buf, want);
                    if (filled == 0) break;
                    if (exclRuns != null && exclRuns.Count > 0)
                        CapturePolicy.ZeroExcludedRanges(buf.AsSpan(0, filled), offset, exclRuns, exclBpc);
                    dest.Write(buf, 0, filled);
                    offset += filled;
                    if (filled < want) break;
                    index++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private static int ReadFull(Stream input, byte[] buf, int want)
        {
            var filled = 0;
            int n;
            while (filled < want && (n = input.Read(buf, filled, want - filled)) > 0)
                filled += n;
            return filled;
        }

        // Direct storeless whole-disk export (bypasses chunk store completely):
        // synthesizes Protective MBR + Primary/Backup GPT and streams each non-skipped partition.
        public static OvfExportResult ExportDiskPlanDirect(
            string format, string outPath, string vmName, DiskCapturePlan plan,
            IReadOnlyList<string> excludeNames, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(log);
            var layout = BuildLayoutFromPlan(plan);
            var capacity = ComputeDiskCapacity(layout);
            var partByNum = new Dictionary<int, PartitionImageInput>();
            foreach (var p in plan.Partitions)
                partByNum[p.PartitionNumber] = p;
            Action<Stream> writer = s => GptWriter.WriteWholeDisk(s, layout, capacity, (partNum, dst) =>
            {
                var p = partByNum[partNum];
                var excl = ResolvePartitionExclusions(p, excludeNames, log);
                StreamBlockSource(p.Source!, dst, p.OffsetBytes, excl);
            });
            try
            {
                return format switch
                {
                    "vmdk" => WrapVmdkResult(outPath, ExportVmdk(outPath, capacity, writer)),
                    "ovf" => ExportOvf(outPath, vmName, capacity, writer, uefi: true),
                    "ova" => ExportOva(outPath, vmName, capacity, writer, uefi: true),
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };
            }
            finally
            {
                foreach (var p in plan.Partitions)
                    p.Source?.Dispose();
            }
        }

        internal static bool IsOmittedPartition(PartitionRecord p) =>
            p.Skipped && p.SkipReason is "recovery-skipped" or "excluded-partition" or "excluded-volume";

        internal static long ComputeDiskCapacity(DiskLayout layout)
        {
            var full = AlignedCapacity(layout.DiskSizeBytes);
            var anyOmitted = false;
            long maxEnd = 0;
            foreach (var p in layout.Partitions)
            {
                if (IsOmittedPartition(p)) { anyOmitted = true; continue; }
                var end = p.OffsetBytes + p.LengthBytes;
                if (end > maxEnd) maxEnd = end;
            }
            if (!anyOmitted || maxEnd <= 0) return full;
            // Backup GPT needs 33 sectors (entries) + 1 sector (header) after the last kept partition.
            var sector = layout.SectorSizeBytes > 0 ? layout.SectorSizeBytes : 512;
            var shrunk = AlignedCapacity(maxEnd + 34L * sector);
            return shrunk < full ? shrunk : full;
        }

        private static OvfExportResult WrapVmdkResult(string vmdkPath, VmdkExportResult r) =>
            new("", vmdkPath, "", r.CapacityBytes, r.PopulatedBytes, r.FileBytes);

        private static DiskLayout BuildLayoutFromPlan(DiskCapturePlan plan)
        {
            var layout = new DiskLayout
            {
                SchemaVersion = 1,
                DiskGuid = plan.DiskGuid,
                DiskSizeBytes = plan.DiskSizeBytes,
                SectorSizeBytes = plan.SectorSizeBytes,
                Style = "gpt",
            };
            foreach (var p in plan.Partitions)
            {
                layout.Partitions.Add(new PartitionRecord
                {
                    PartitionNumber = p.PartitionNumber,
                    Name = p.Name,
                    TypeGuid = p.TypeGuid,
                    UniqueGuid = p.UniqueGuid,
                    FirstLba = p.FirstLba,
                    LastLba = p.LastLba,
                    OffsetBytes = p.OffsetBytes,
                    LengthBytes = p.LengthBytes,
                    Filesystem = p.Filesystem,
                    Skipped = p.Skipped,
                    SkipReason = p.SkipReason,
                    SnapshotGeneration = p.Skipped || p.Source == null ? 0 : p.PartitionNumber,
                });
            }
            return layout;
        }

        private static IExclusionResolver? ResolvePartitionExclusions(PartitionImageInput part, IReadOnlyList<string> excludeNames, ILogger log)
        {
            if (excludeNames.Count == 0 || !string.Equals(part.Filesystem, "ntfs", StringComparison.OrdinalIgnoreCase))
                return null;
            var device = (part.Source as IVolumeHandleProvider)?.SnapshotDevicePath;
            if (string.IsNullOrEmpty(device))
                return null;
            return MftExclusionResolver.TryCreate(device, excludeNames, log);
        }

        // OVF 1.1 §5.1 uses POSIX ustar TAR (<= 8 GiB per file) in strict order:
        // .ovf, .mf, .vmdk; use GNU base-256 size encoding for > 8 GiB VMDKs
        // (VMware ovftool rejects PAX extended headers on large OVA members).
        private static void PackOvaArchive(string ovaPath, string ovfPath, string mfPath, string vmdkPath)
        {
            var format = new FileInfo(vmdkPath).Length > 0x1_FFFF_FFFFL ? TarEntryFormat.Gnu : TarEntryFormat.Ustar;
            using var fs = new FileStream(ovaPath, FileMode.Create, FileAccess.Write, FileShare.Read, IoBufferBytes, FileOptions.SequentialScan);
            using var tar = new TarWriter(fs, format, leaveOpen: true);
            tar.WriteEntry(ovfPath, Path.GetFileName(ovfPath));
            tar.WriteEntry(mfPath, Path.GetFileName(mfPath));
            tar.WriteEntry(vmdkPath, Path.GetFileName(vmdkPath));
            fs.Flush(true);
        }

        private static string BuildManifest(params (string FileName, string Sha256Hex)[] entries)
        {
            var sb = new StringBuilder();
            foreach (var (name, hex) in entries)
                sb.Append(CultureInfo.InvariantCulture, $"SHA256({name})= {hex}\n");
            return sb.ToString();
        }

        private static string SanitizeStem(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "corpus-disk";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
            var result = sb.ToString().Trim('-');
            return result.Length > 0 ? result : "corpus-disk";
        }

        private static string BuildOvfXml(string vmName, string vmdkFileName, long capacityBytes, long populatedBytes, long vmdkFileBytes, bool uefi, string storageController = "ahci")
        {
            var inv = CultureInfo.InvariantCulture;
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var settings = new XmlWriterSettings { Indent = true, Encoding = utf8, OmitXmlDeclaration = false };
            using var ms = new MemoryStream();
            using (var w = XmlWriter.Create(ms, settings))
            {
                const string ovfNs = "http://schemas.dmtf.org/ovf/envelope/1";
                const string rasdNs = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData";
                const string vssdNs = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_VirtualSystemSettingData";
                const string vmwNs = "http://www.vmware.com/schema/ovf";

                w.WriteStartDocument();
                w.WriteStartElement("Envelope", ovfNs);
                w.WriteAttributeString("xmlns", "ovf", null, ovfNs);
                w.WriteAttributeString("xmlns", "rasd", null, rasdNs);
                w.WriteAttributeString("xmlns", "vssd", null, vssdNs);
                w.WriteAttributeString("xmlns", "vmw", null, vmwNs);

                w.WriteStartElement("References", ovfNs);
                w.WriteStartElement("File", ovfNs);
                w.WriteAttributeString("href", ovfNs, vmdkFileName);
                w.WriteAttributeString("id", ovfNs, "file1");
                w.WriteAttributeString("size", ovfNs, vmdkFileBytes.ToString(inv));
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("DiskSection", ovfNs);
                w.WriteElementString("Info", ovfNs, "Virtual disk information");
                w.WriteStartElement("Disk", ovfNs);
                w.WriteAttributeString("capacity", ovfNs, capacityBytes.ToString(inv));
                w.WriteAttributeString("capacityAllocationUnits", ovfNs, "byte");
                w.WriteAttributeString("diskId", ovfNs, "vmdisk1");
                w.WriteAttributeString("fileRef", ovfNs, "file1");
                w.WriteAttributeString("format", ovfNs, "http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized");
                w.WriteAttributeString("populatedSize", ovfNs, populatedBytes.ToString(inv));
                w.WriteEndElement();
                w.WriteEndElement();

                WriteVirtualSystem(w, ovfNs, rasdNs, vssdNs, vmwNs, vmName, uefi, storageController);
                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return utf8.GetString(ms.ToArray()) + "\n";
        }

        private static void WriteVirtualSystem(XmlWriter w, string ovfNs, string rasdNs, string vssdNs, string vmwNs, string vmName, bool uefi, string storageController = "ahci")
        {
            w.WriteStartElement("VirtualSystem", ovfNs);
            w.WriteAttributeString("id", ovfNs, vmName);
            w.WriteElementString("Info", ovfNs, "Corpus exported virtual machine");
            w.WriteElementString("Name", ovfNs, vmName);

            w.WriteStartElement("OperatingSystemSection", ovfNs);
            w.WriteAttributeString("id", ovfNs, "1");
            w.WriteElementString("Info", ovfNs, "Guest operating system");
            w.WriteEndElement();

            w.WriteStartElement("VirtualHardwareSection", ovfNs);
            w.WriteElementString("Info", ovfNs, "Virtual hardware requirements");

            w.WriteStartElement("System", ovfNs);
            w.WriteElementString("ElementName", vssdNs, "Virtual Hardware Family");
            w.WriteElementString("InstanceID", vssdNs, "0");
            w.WriteElementString("VirtualSystemIdentifier", vssdNs, vmName);
            w.WriteElementString("VirtualSystemType", vssdNs, "vmx-14");
            w.WriteEndElement();

            WriteHardwareItem(w, ovfNs, rasdNs, "2 virtual CPU(s)", "1", "3", virtualQuantity: "2");
            WriteHardwareItem(w, ovfNs, rasdNs, "4096MB of memory", "2", "4", allocationUnits: "byte * 2^20", virtualQuantity: "4096");
            WriteStorageController(w, ovfNs, rasdNs, storageController);
            WriteHardwareItem(w, ovfNs, rasdNs, "Hard Disk 1", "4", "17", hostResource: "ovf:/disk/vmdisk1", parent: "3", addressOnParent: "0");
            if (uefi)
            {
                w.WriteStartElement("Config", vmwNs);
                w.WriteAttributeString("required", ovfNs, "false");
                w.WriteAttributeString("key", vmwNs, "firmware");
                w.WriteAttributeString("value", vmwNs, "efi");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteStorageController(XmlWriter w, string ovfNs, string rasdNs, string controller)
        {
            switch (controller.ToLowerInvariant())
            {
                case "lsisas":
                    WriteHardwareItem(w, ovfNs, rasdNs, "SCSI Controller", "3", "6", resourceSubType: "lsilogicsas", address: "0");
                    break;
                case "nvme":
                    WriteHardwareItem(w, ovfNs, rasdNs, "NVMe Controller", "3", "20", resourceSubType: "vmware.nvme.controller", address: "0");
                    break;
                case "scsi":
                    WriteHardwareItem(w, ovfNs, rasdNs, "SCSI Controller", "3", "6", resourceSubType: "lsilogic", address: "0");
                    break;
                default: // "ahci" — works in-box on Windows 10/11 across VMware, VirtualBox, and QEMU
                    WriteHardwareItem(w, ovfNs, rasdNs, "SATA Controller", "3", "20", resourceSubType: "AHCI", address: "0");
                    break;
            }
        }

        private static void WriteHardwareItem(
            XmlWriter w, string ovfNs, string rasdNs, string name, string id, string resourceType,
            string? allocationUnits = null, string? virtualQuantity = null, string? resourceSubType = null,
            string? hostResource = null, string? parent = null, string? addressOnParent = null, string? address = null)
        {
            w.WriteStartElement("Item", ovfNs);
            if (address != null) w.WriteElementString("Address", rasdNs, address);
            if (addressOnParent != null) w.WriteElementString("AddressOnParent", rasdNs, addressOnParent);
            if (allocationUnits != null) w.WriteElementString("AllocationUnits", rasdNs, allocationUnits);
            w.WriteElementString("ElementName", rasdNs, name);
            if (hostResource != null) w.WriteElementString("HostResource", rasdNs, hostResource);
            w.WriteElementString("InstanceID", rasdNs, id);
            if (parent != null) w.WriteElementString("Parent", rasdNs, parent);
            if (resourceSubType != null) w.WriteElementString("ResourceSubType", rasdNs, resourceSubType);
            w.WriteElementString("ResourceType", rasdNs, resourceType);
            if (virtualQuantity != null) w.WriteElementString("VirtualQuantity", rasdNs, virtualQuantity);
            w.WriteEndElement();
        }
    }

    // Forward-only streaming writer for VMware streamOptimized VMDK (version 3).
    // Supports Seek(Current/Begin) forward so Store.ExportToStream skips holes/zeros without allocation.
    internal sealed class StreamOptimizedVmdkStream : Stream
    {
        private readonly Stream _dest;
        private readonly long _capacityBytes;
        private readonly ulong _capacitySectors;
        private readonly long _totalGrains;
        private readonly uint[] _gd;
        private readonly uint[] _currentGt = new uint[OvfExporter.GtesPerGt];
        private readonly byte[] _grainBuf = new byte[OvfExporter.GrainBytes];
        private readonly byte[] _sectorPad = new byte[OvfExporter.SectorSize];
        private readonly MemoryStream _compressBuf = new();
        private long _virtualPos;
        private long _bufferedGrainIndex = -1;
        private int _bufferedBytes;
        private bool _bufferedNonZero;
        private int _openGtIndex = -1;
        private bool _openGtDirty;
        private ulong _currentSector;
        private bool _finished;

        public long PopulatedBytes { get; private set; }

        public StreamOptimizedVmdkStream(Stream dest, long capacityBytes, string diskFileName)
        {
            _dest = dest;
            _capacityBytes = capacityBytes;
            _capacitySectors = (ulong)(capacityBytes / OvfExporter.SectorSize);
            _totalGrains = capacityBytes / OvfExporter.GrainBytes;
            var numGts = (int)((_totalGrains + OvfExporter.GtesPerGt - 1) / OvfExporter.GtesPerGt);
            _gd = new uint[numGts];
            WritePreamble(diskFileName);
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _capacityBytes;
        public override long Position
        {
            get => _virtualPos;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _virtualPos + offset,
                SeekOrigin.End => _capacityBytes + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < _virtualPos)
                throw new NotSupportedException("streamOptimized VMDK writer only supports forward seeks");
            if (next > _capacityBytes)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (next > _virtualPos && _bufferedGrainIndex >= 0)
            {
                var nextGrain = next / OvfExporter.GrainBytes;
                if (nextGrain != _bufferedGrainIndex)
                    FlushBufferedGrain();
                else
                    _bufferedBytes = (int)(next % OvfExporter.GrainBytes);
            }
            _virtualPos = next;
            return _virtualPos;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var end = offset + count;
            if (_bufferedGrainIndex < 0 && (_virtualPos % OvfExporter.GrainBytes) == 0 && count >= OvfExporter.GrainBytes * 2)
            {
                var fullGrains = count / OvfExporter.GrainBytes;
                WriteFullGrainsParallel(buffer, offset, fullGrains);
                var consumed = fullGrains * OvfExporter.GrainBytes;
                offset += consumed;
                _virtualPos += consumed;
                if (offset == end) return;
            }
            while (offset < end)
            {
                var grainIndex = _virtualPos / OvfExporter.GrainBytes;
                var inGrain = (int)(_virtualPos % OvfExporter.GrainBytes);
                var take = Math.Min(end - offset, OvfExporter.GrainBytes - inGrain);
                if (_bufferedGrainIndex != grainIndex)
                {
                    FlushBufferedGrain();
                    _bufferedGrainIndex = grainIndex;
                    _bufferedBytes = 0;
                    _bufferedNonZero = false;
                    if (inGrain != 0 || take < OvfExporter.GrainBytes)
                        Array.Clear(_grainBuf, 0, OvfExporter.GrainBytes);
                }
                Buffer.BlockCopy(buffer, offset, _grainBuf, inGrain, take);
                if (!_bufferedNonZero && HasNonZero(buffer.AsSpan(offset, take)))
                    _bufferedNonZero = true;
                _bufferedBytes = Math.Max(_bufferedBytes, inGrain + take);
                _virtualPos += take;
                offset += take;
                if (inGrain + take == OvfExporter.GrainBytes)
                    FlushBufferedGrain();
            }
        }

        private void WriteFullGrainsParallel(byte[] buffer, int baseOffset, int grainCount)
        {
            var startGrain = _virtualPos / OvfExporter.GrainBytes;
            var compressed = new byte[]?[grainCount];
            var compLens = new int[grainCount];
            Parallel.For(0, grainCount, i =>
            {
                var off = baseOffset + i * OvfExporter.GrainBytes;
                if (!HasNonZero(buffer.AsSpan(off, OvfExporter.GrainBytes)))
                    return;
                var ms = new MemoryStream(OvfExporter.GrainBytes + 64);
                using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    zlib.Write(buffer, off, OvfExporter.GrainBytes);
                compressed[i] = ms.GetBuffer();
                compLens[i] = (int)ms.Length;
            });
            for (var i = 0; i < grainCount; i++)
            {
                var comp = compressed[i];
                if (comp == null) continue;
                EmitCompressedGrain(startGrain + i, comp, compLens[i]);
            }
        }

        private void EmitCompressedGrain(long grainIndex, byte[] compBuf, int compLen)
        {
            var gtIndex = (int)(grainIndex / OvfExporter.GtesPerGt);
            var gteIndex = (int)(grainIndex % OvfExporter.GtesPerGt);
            if (_openGtIndex != gtIndex)
            {
                FlushCurrentGt();
                _openGtIndex = gtIndex;
                Array.Clear(_currentGt, 0, _currentGt.Length);
            }
            var markerSector = _currentSector;
            Span<byte> hdr = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(0, 8), (ulong)grainIndex * OvfExporter.GrainSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), (uint)compLen);
            _dest.Write(hdr);
            _dest.Write(compBuf, 0, compLen);
            var totalBytes = 12 + compLen;
            var rem = totalBytes % OvfExporter.SectorSize;
            if (rem != 0)
            {
                var pad = OvfExporter.SectorSize - rem;
                _dest.Write(_sectorPad, 0, pad);
                totalBytes += pad;
            }
            _currentSector += (ulong)(totalBytes / OvfExporter.SectorSize);
            _currentGt[gteIndex] = checked((uint)markerSector);
            _openGtDirty = true;
            PopulatedBytes += OvfExporter.GrainBytes;
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            FlushBufferedGrain();
            FlushCurrentGt();
            var gdBytes = ((_gd.Length * 4) + OvfExporter.SectorSize - 1) / OvfExporter.SectorSize * OvfExporter.SectorSize;
            var gdSectors = (ulong)Math.Max(1, gdBytes / OvfExporter.SectorSize);
            WriteMetadataMarker(gdSectors, OvfExporter.MarkerGd);
            var gdOffset = _currentSector;
            var gdRaw = new byte[(int)gdSectors * OvfExporter.SectorSize];
            for (var i = 0; i < _gd.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(gdRaw.AsSpan(i * 4, 4), _gd[i]);
            WriteSectors(gdRaw);
            WriteMetadataMarker(1, OvfExporter.MarkerFooter);
            WriteSectors(BuildHeader(gdOffset));
            WriteMetadataMarker(0, OvfExporter.MarkerEos);
        }

        public override void Flush() => _dest.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void WritePreamble(string diskFileName)
        {
            WriteSectors(BuildHeader(OvfExporter.GdAtEnd));
            var desc = BuildDescriptor(diskFileName);
            WriteSectors(desc);
            var padSectors = (int)(OvfExporter.OverHeadSectors - _currentSector);
            for (var i = 0; i < padSectors; i++)
                WriteSectors(_sectorPad);
        }

        private byte[] BuildHeader(ulong gdOffset)
        {
            var hdr = new byte[OvfExporter.SectorSize];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), OvfExporter.VmdkMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), OvfExporter.VmdkVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), OvfExporter.StreamOptimizedFlags);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(12, 8), _capacitySectors);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(20, 8), OvfExporter.GrainSectors);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(28, 8), 1ul);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(36, 8), OvfExporter.DescriptorSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(44, 4), OvfExporter.GtesPerGt);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(48, 8), 0ul);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(56, 8), gdOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(64, 8), OvfExporter.OverHeadSectors);
            hdr[72] = 0; // uncleanShutdown
            hdr[73] = (byte)'\n';
            hdr[74] = (byte)' ';
            hdr[75] = (byte)'\r';
            hdr[76] = (byte)'\n';
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(77, 2), 1); // DEFLATE
            return hdr;
        }

        private byte[] BuildDescriptor(string diskFileName)
        {
            var cylinders = Math.Max(1ul, _capacitySectors / (255ul * 63ul));
            var text = string.Create(CultureInfo.InvariantCulture,
                $"# Disk DescriptorFile\n" +
                $"version=1\n" +
                $"CID=fffffffe\n" +
                $"parentCID=ffffffff\n" +
                $"createType=\"streamOptimized\"\n\n" +
                $"# Extent description\n" +
                $"RW {_capacitySectors} SPARSE \"{diskFileName}\"\n\n" +
                $"# The Disk Data Base\n#DDB\n\n" +
                $"ddb.virtualHWVersion = \"14\"\n" +
                $"ddb.adapterType = \"lsilogic\"\n" +
                $"ddb.geometry.cylinders = \"{cylinders}\"\n" +
                $"ddb.geometry.heads = \"255\"\n" +
                $"ddb.geometry.sectors = \"63\"\n");
            var raw = new byte[OvfExporter.DescriptorSectors * OvfExporter.SectorSize];
            var bytes = Encoding.ASCII.GetBytes(text);
            if (bytes.Length > raw.Length)
                throw new InvalidDataException("VMDK descriptor exceeds 20-sector embedded limit");
            Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
            return raw;
        }

        private void FlushBufferedGrain()
        {
            if (_bufferedGrainIndex < 0) return;
            var grainIndex = _bufferedGrainIndex;
            _bufferedGrainIndex = -1;
            if (!_bufferedNonZero) return;

            _compressBuf.SetLength(0);
            using (var zlib = new ZLibStream(_compressBuf, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(_grainBuf, 0, OvfExporter.GrainBytes);
            EmitCompressedGrain(grainIndex, _compressBuf.GetBuffer(), (int)_compressBuf.Length);
        }

        private void FlushCurrentGt()
        {
            if (_openGtIndex < 0 || !_openGtDirty) return;
            var gtSectors = (ulong)((OvfExporter.GtesPerGt * 4) / OvfExporter.SectorSize);
            WriteMetadataMarker(gtSectors, OvfExporter.MarkerGt);
            _gd[_openGtIndex] = checked((uint)_currentSector);
            var gtRaw = new byte[OvfExporter.GtesPerGt * 4];
            for (var i = 0; i < OvfExporter.GtesPerGt; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(gtRaw.AsSpan(i * 4, 4), _currentGt[i]);
            WriteSectors(gtRaw);
            _openGtDirty = false;
        }

        private void WriteMetadataMarker(ulong val, uint type)
        {
            var marker = new byte[OvfExporter.SectorSize];
            BinaryPrimitives.WriteUInt64LittleEndian(marker.AsSpan(0, 8), val);
            BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(8, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(12, 4), type);
            WriteSectors(marker);
        }

        private void WriteSectors(byte[] data)
        {
            _dest.Write(data, 0, data.Length);
            _currentSector += (ulong)(data.Length / OvfExporter.SectorSize);
        }

        private static bool HasNonZero(ReadOnlySpan<byte> span) => span.IndexOfAnyExcept((byte)0) >= 0;
    }

    // Pass-through write stream that computes SHA-256 inline while writing to disk,
    // eliminating the multi-gigabyte re-read previously needed to generate OVF .mf manifests.
    internal sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public HashingWriteStream(Stream inner) => _inner = inner;

        public string FinishHex() => Convert.ToHexString(_sha.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _sha.AppendData(buffer, offset, count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _sha.AppendData(buffer);
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _sha.Dispose();
            base.Dispose(disposing);
        }
    }

    // Pure-C# streamOptimized VMDK reader for round-trip verification in tests and CLI checks.
    internal static class VmdkVerifier
    {
        public static byte[] ReadAllBytes(string vmdkPath, long logicalLength)
        {
            using var fs = new FileStream(vmdkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < OvfExporter.SectorSize * 3)
                throw new InvalidDataException("VMDK file too short");

            var footerBuf = new byte[OvfExporter.SectorSize];
            fs.Seek(-2L * OvfExporter.SectorSize, SeekOrigin.End);
            fs.ReadExactly(footerBuf);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footerBuf.AsSpan(0, 4)) != OvfExporter.VmdkMagic)
                throw new InvalidDataException("VMDK footer magic mismatch");
            var capacitySectors = BinaryPrimitives.ReadUInt64LittleEndian(footerBuf.AsSpan(12, 8));
            var gdOffset = BinaryPrimitives.ReadUInt64LittleEndian(footerBuf.AsSpan(56, 8));
            if (gdOffset == OvfExporter.GdAtEnd)
                throw new InvalidDataException("VMDK footer has unset gdOffset");

            var totalGrains = (long)(capacitySectors / OvfExporter.GrainSectors);
            var numGts = (int)((totalGrains + OvfExporter.GtesPerGt - 1) / OvfExporter.GtesPerGt);
            var gdRaw = new byte[numGts * 4];
            fs.Seek((long)(gdOffset * OvfExporter.SectorSize), SeekOrigin.Begin);
            fs.ReadExactly(gdRaw);

            var result = new byte[logicalLength];
            var gtRaw = new byte[OvfExporter.GtesPerGt * 4];
            Span<byte> grainHdr = stackalloc byte[12];
            for (var gi = 0; gi < numGts; gi++)
            {
                var gtSec = BinaryPrimitives.ReadUInt32LittleEndian(gdRaw.AsSpan(gi * 4, 4));
                if (gtSec == 0) continue;
                fs.Seek((long)gtSec * OvfExporter.SectorSize, SeekOrigin.Begin);
                fs.ReadExactly(gtRaw);
                ReadGrainTable(fs, gtRaw, gi, grainHdr, result);
            }
            return result;
        }

        private static void ReadGrainTable(FileStream fs, byte[] gtRaw, int gi, Span<byte> grainHdr, byte[] result)
        {
            for (var e = 0; e < OvfExporter.GtesPerGt; e++)
            {
                var grainSec = BinaryPrimitives.ReadUInt32LittleEndian(gtRaw.AsSpan(e * 4, 4));
                if (grainSec == 0) continue;
                fs.Seek((long)grainSec * OvfExporter.SectorSize, SeekOrigin.Begin);
                fs.ReadExactly(grainHdr);
                var lba = BinaryPrimitives.ReadUInt64LittleEndian(grainHdr.Slice(0, 8));
                var compSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(grainHdr.Slice(8, 4));
                var expectedLba = (ulong)((long)gi * OvfExporter.GtesPerGt + e) * OvfExporter.GrainSectors;
                if (lba != expectedLba)
                    throw new InvalidDataException($"VMDK grain LBA mismatch: got {lba}, expected {expectedLba}");
                var comp = new byte[compSize];
                fs.ReadExactly(comp);
                using var ms = new MemoryStream(comp, writable: false);
                using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                var plain = new byte[OvfExporter.GrainBytes];
                zlib.ReadExactly(plain);
                var dstOff = (long)lba * OvfExporter.SectorSize;
                if (dstOff < result.Length)
                    Buffer.BlockCopy(plain, 0, result, (int)dstOff, (int)Math.Min(plain.Length, result.Length - dstOff));
            }
        }
    }

    // Synthesizes UEFI-compliant Protective MBR + Primary GPT + Backup GPT into a forward-only stream
    // so whole-disk VMDK/OVF/OVA exports boot directly in VMware/VirtualBox/QEMU.
    internal static class GptWriter
    {
        private const int Sector = 512;
        private const int EntrySize = 128;
        private const int EntryCount = 128;
        private const int ArraySectors = (EntryCount * EntrySize) / Sector; // 32 sectors (16 KiB)
        private static readonly uint[] CrcTable = BuildCrc32Table();

        public static void WriteWholeDisk(Stream dest, DiskLayout layout, long alignedCapacityBytes, Action<int, Stream> writeSnapshot)
        {
            ArgumentNullException.ThrowIfNull(dest);
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(writeSnapshot);
            var sectorSize = layout.SectorSizeBytes == 4096 ? 4096 : Sector;
            if (string.Equals(layout.Style, "mbr", StringComparison.OrdinalIgnoreCase))
            {
                WriteWholeMbrDisk(dest, layout, sectorSize, writeSnapshot);
                return;
            }
            var arraySectors = (EntryCount * EntrySize) / sectorSize;
            var totalSectors = (ulong)(alignedCapacityBytes / sectorSize);
            if (totalSectors < (ulong)(arraySectors * 2 + 4))
                throw new InvalidDataException($"disk capacity {alignedCapacityBytes} bytes is too small for GPT");

            var entries = BuildPartitionArray(layout.Partitions);
            var entriesCrc = Crc32(entries);
            var diskGuid = Guid.TryParse(layout.DiskGuid, out var g) ? g : Guid.NewGuid();

            dest.Seek(0, SeekOrigin.Begin);
            dest.Write(BuildProtectiveMbr(totalSectors, sectorSize));
            dest.Write(BuildGptHeader(myLba: 1, altLba: totalSectors - 1, entriesLba: 2, totalSectors, diskGuid, entriesCrc, sectorSize, (ulong)arraySectors));
            dest.Write(entries);

            var sorted = new List<PartitionRecord>(layout.Partitions);
            sorted.Sort((a, b) => a.OffsetBytes.CompareTo(b.OffsetBytes));
            foreach (var part in sorted)
            {
                if (part.Skipped || part.SnapshotGeneration <= 0)
                    continue;
                dest.Seek(part.OffsetBytes, SeekOrigin.Begin);
                writeSnapshot(part.SnapshotGeneration, dest);
            }

            var backupEntriesLba = totalSectors - 1 - (ulong)arraySectors;
            dest.Seek((long)(backupEntriesLba * (ulong)sectorSize), SeekOrigin.Begin);
            dest.Write(entries);
            dest.Write(BuildGptHeader(myLba: totalSectors - 1, altLba: 1, entriesLba: backupEntriesLba, totalSectors, diskGuid, entriesCrc, sectorSize, (ulong)arraySectors));
        }

        private static void WriteWholeMbrDisk(Stream dest, DiskLayout layout, int sectorSize, Action<int, Stream> writeSnapshot)
        {
            var mbr = new byte[sectorSize];
            if (uint.TryParse(layout.DiskGuid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sig))
                BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(440, 4), sig);
            var slot = 0;
            foreach (var p in layout.Partitions)
            {
                if (slot >= 4) break;
                var off = 446 + slot * 16;
                mbr[off] = (byte)(p.AttributeFlags & 0xFF);
                var ptype = (byte)0x07;
                if (p.TypeGuid.StartsWith("mbr-0x", StringComparison.OrdinalIgnoreCase)
                    && byte.TryParse(p.TypeGuid.AsSpan(6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedType))
                    ptype = parsedType;
                else if (string.Equals(p.Filesystem, "fat32", StringComparison.OrdinalIgnoreCase))
                    ptype = 0x0C;
                mbr[off + 4] = ptype;
                BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(off + 8, 4), (uint)p.FirstLba);
                BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(off + 12, 4), (uint)(p.LastLba - p.FirstLba + 1));
                slot++;
            }
            mbr[510] = 0x55;
            mbr[511] = 0xAA;
            dest.Seek(0, SeekOrigin.Begin);
            dest.Write(mbr);
            var sorted = new List<PartitionRecord>(layout.Partitions);
            sorted.Sort((a, b) => a.OffsetBytes.CompareTo(b.OffsetBytes));
            foreach (var part in sorted)
            {
                if (part.Skipped || part.SnapshotGeneration <= 0) continue;
                dest.Seek(part.OffsetBytes, SeekOrigin.Begin);
                writeSnapshot(part.SnapshotGeneration, dest);
            }
        }

        private static byte[] BuildProtectiveMbr(ulong totalSectors, int sectorSize)
        {
            var mbr = new byte[sectorSize];
            mbr[446 + 4] = 0xEE; // GPT Protective MBR partition type
            mbr[446 + 1] = 0x00; mbr[446 + 2] = 0x02; mbr[446 + 3] = 0x00;
            mbr[446 + 5] = 0xFF; mbr[446 + 6] = 0xFF; mbr[446 + 7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8, 4), 1u);
            var sizeSectors = totalSectors - 1 > uint.MaxValue ? uint.MaxValue : (uint)(totalSectors - 1);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12, 4), sizeSectors);
            mbr[510] = 0x55;
            mbr[511] = 0xAA;
            return mbr;
        }

        private static byte[] BuildPartitionArray(IList<PartitionRecord> parts)
        {
            var buf = new byte[EntryCount * EntrySize];
            var idx = 0;
            foreach (var p in parts)
            {
                if (idx >= EntryCount) break;
                if (OvfExporter.IsOmittedPartition(p))
                    continue;
                var span = buf.AsSpan(idx * EntrySize, EntrySize);
                if (Guid.TryParse(p.TypeGuid, out var typeGuid))
                    typeGuid.TryWriteBytes(span.Slice(0, 16));
                var uniqueGuid = Guid.TryParse(p.UniqueGuid, out var ug) ? ug : Guid.NewGuid();
                uniqueGuid.TryWriteBytes(span.Slice(16, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32, 8), (ulong)p.FirstLba);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(40, 8), (ulong)p.LastLba);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(48, 8), p.AttributeFlags);
                if (!string.IsNullOrEmpty(p.Name))
                {
                    var nameBytes = Encoding.Unicode.GetBytes(p.Name);
                    nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 72)).CopyTo(span.Slice(56, 72));
                }
                idx++;
            }
            return buf;
        }

        private static byte[] BuildGptHeader(ulong myLba, ulong altLba, ulong entriesLba, ulong totalSectors, Guid diskGuid, uint entriesCrc, int sectorSize, ulong arraySectors)
        {
            var hdr = new byte[sectorSize];
            Encoding.ASCII.GetBytes("EFI PART", hdr.AsSpan(0, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), 92u);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(24, 8), myLba);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(32, 8), altLba);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(40, 8), 2ul + arraySectors);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(48, 8), totalSectors - 2ul - arraySectors);
            diskGuid.TryWriteBytes(hdr.AsSpan(56, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(72, 8), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(80, 4), EntryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(84, 4), EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(88, 4), entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), Crc32(hdr.AsSpan(0, 92)));
            return hdr;
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var b = 0; b < 8; b++)
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < data.Length; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
