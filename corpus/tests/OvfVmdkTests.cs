using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Corpus.Tests;

public sealed class OvfVmdkTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public OvfVmdkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "corpus-ovf-test-" + Guid.NewGuid().ToString("N"));
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Store NewStore() => new(_storeRoot, _dbPath, 1, 1, new TestLogger());

    private static byte[] MakeTestVolume()
    {
        // 3 MiB + 12345 bytes: chunk 0 = random, chunk 1 = all zeros (sparse), chunk 2 = patterned, tail = random.
        var data = new byte[3 * 1024 * 1024 + 12345];
        var rng = new Random(2026);
        rng.NextBytes(data.AsSpan(0, 1024 * 1024));
        // chunk 1 (1..2 MiB) left as zeros to prove sparse grain skipping
        for (var i = 2 * 1024 * 1024; i < 3 * 1024 * 1024; i++)
            data[i] = (byte)(i % 251);
        rng.NextBytes(data.AsSpan(3 * 1024 * 1024));
        return data;
    }

    [Fact]
    public void ExportVmdk_RoundTripsDataAndSkipsZeroGrains()
    {
        var original = MakeTestVolume();
        var volPath = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(volPath, original);

        using var store = NewStore();
        using (var src = new FileBlockSource(volPath))
            store.Image(src, "vm-win11");

        var vmdkPath = Path.Combine(_dir, "out.vmdk");
        store.ExportVmdk(1, vmdkPath);

        Assert.True(File.Exists(vmdkPath));
        // Sparse zero-skip + zlib must be noticeably smaller than the raw 3 MiB volume.
        Assert.True(new FileInfo(vmdkPath).Length < original.Length);

        var header = new byte[OvfExporter.SectorSize];
        using (var fs = File.OpenRead(vmdkPath))
        {
            fs.ReadExactly(header);
            var desc = new byte[OvfExporter.DescriptorSectors * OvfExporter.SectorSize];
            fs.ReadExactly(desc);
            var descText = Encoding.ASCII.GetString(desc).TrimEnd('\0');
            Assert.Contains("createType=\"streamOptimized\"", descText, StringComparison.Ordinal);
            Assert.Contains("SPARSE \"out.vmdk\"", descText, StringComparison.Ordinal);
        }
        Assert.Equal(OvfExporter.VmdkMagic, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)));
        Assert.Equal(OvfExporter.VmdkVersion, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)));
        Assert.Equal(OvfExporter.StreamOptimizedFlags, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)));
        Assert.Equal(OvfExporter.GdAtEnd, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56, 8)));

        var decoded = VmdkVerifier.ReadAllBytes(vmdkPath, original.Length);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ExportOvf_WritesValidEnvelopeVmdkAndSha256Manifest()
    {
        var original = MakeTestVolume();
        var volPath = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(volPath, original);

        using var store = NewStore();
        using (var src = new FileBlockSource(volPath))
            store.Image(src, "prod-box");

        var ovfOut = Path.Combine(_dir, "ovf-dir", "prod-box.ovf");
        var res = store.ExportOvf(1, ovfOut);

        Assert.True(File.Exists(res.OvfPath));
        Assert.True(File.Exists(res.VmdkPath));
        Assert.True(File.Exists(res.ManifestPath));

        var doc = XDocument.Load(res.OvfPath);
        XNamespace ovf = "http://schemas.dmtf.org/ovf/envelope/1";
        var fileEl = doc.Root!.Element(ovf + "References")!.Element(ovf + "File")!;
        Assert.Equal("prod-box.vmdk", (string)fileEl.Attribute(ovf + "href")!);
        Assert.Equal(new FileInfo(res.VmdkPath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)fileEl.Attribute(ovf + "size")!);

        var diskEl = doc.Root.Element(ovf + "DiskSection")!.Element(ovf + "Disk")!;
        Assert.Equal("http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized", (string)diskEl.Attribute(ovf + "format")!);
        Assert.Equal(res.CapacityBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)diskEl.Attribute(ovf + "capacity")!);

        var mfLines = File.ReadAllLines(res.ManifestPath);
        Assert.Equal(2, mfLines.Length);
        Assert.Equal($"SHA256(prod-box.ovf)= {Sha256Hex(res.OvfPath)}", mfLines[0]);
        Assert.Equal($"SHA256(prod-box.vmdk)= {Sha256Hex(res.VmdkPath)}", mfLines[1]);

        Assert.Equal(original, VmdkVerifier.ReadAllBytes(res.VmdkPath, original.Length));
    }

    [Fact]
    public void ExportOva_PacksUstarInSpecOrderAndRoundTrips()
    {
        var original = MakeTestVolume();
        var volPath = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(volPath, original);

        using var store = NewStore();
        using (var src = new FileBlockSource(volPath))
            store.Image(src, "appliance");

        var ovaPath = Path.Combine(_dir, "my-vm.ova");
        store.ExportOva(1, ovaPath);
        Assert.True(File.Exists(ovaPath));

        var extractDir = Path.Combine(_dir, "unpacked");
        Directory.CreateDirectory(extractDir);
        var names = new List<string>();
        using (var fs = File.OpenRead(ovaPath))
        using (var tar = new TarReader(fs))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                names.Add(entry.Name);
                Assert.Equal(TarEntryFormat.Ustar, entry.Format);
                entry.ExtractToFile(Path.Combine(extractDir, entry.Name), overwrite: true);
            }
        }

        // OVF 1.1 §5.1 strict order: .ovf first, .mf second, .vmdk third.
        Assert.Equal(new[] { "my-vm.ovf", "my-vm.mf", "my-vm.vmdk" }, names);

        var mfLines = File.ReadAllLines(Path.Combine(extractDir, "my-vm.mf"));
        Assert.Equal($"SHA256(my-vm.ovf)= {Sha256Hex(Path.Combine(extractDir, "my-vm.ovf"))}", mfLines[0]);
        Assert.Equal($"SHA256(my-vm.vmdk)= {Sha256Hex(Path.Combine(extractDir, "my-vm.vmdk"))}", mfLines[1]);

        var decoded = VmdkVerifier.ReadAllBytes(Path.Combine(extractDir, "my-vm.vmdk"), original.Length);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ExportDiskOva_WritesBootableGptAndUefiOvf()
    {
        var espBytes = new byte[1024 * 1024];
        var osBytes = new byte[2 * 1024 * 1024];
        var rng = new Random(77);
        rng.NextBytes(espBytes);
        rng.NextBytes(osBytes);
        var espPath = Path.Combine(_dir, "esp.img");
        var osPath = Path.Combine(_dir, "os.img");
        File.WriteAllBytes(espPath, espBytes);
        File.WriteAllBytes(osPath, osBytes);

        const long diskBytes = 5L * 1024 * 1024; // 5 MiB total
        var diskGuid = Guid.NewGuid().ToString("D");
        var espGuid = Guid.NewGuid().ToString("D");
        var osGuid = Guid.NewGuid().ToString("D");

        using var store = NewStore();
        using var espSrc = new FileBlockSource(espPath);
        using var osSrc = new FileBlockSource(osPath);
        var plan = new DiskCapturePlan(diskGuid, diskBytes, 512, new[]
        {
            new PartitionImageInput(1, "EFI", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", espGuid,
                2048, 4095, 1024L * 1024, 1024L * 1024, "fat32", false, null, espSrc, null),
            new PartitionImageInput(2, "OS", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", osGuid,
                4096, 8191, 2L * 1024 * 1024, 2L * 1024 * 1024, "ntfs", false, null, osSrc, null),
        });
        var layout = store.ImageDisk(0, "win11-disk", new DiskImageOptions(), plan);

        var ovaPath = Path.Combine(_dir, "bootable-w11.ova");
        store.ExportDiskOva(layout.Generation, ovaPath);

        var unpack = Path.Combine(_dir, "bootable-unpacked");
        Directory.CreateDirectory(unpack);
        using (var fs = File.OpenRead(ovaPath))
        using (var tar = new TarReader(fs))
        {
            while (tar.GetNextEntry() is { } entry)
                entry.ExtractToFile(Path.Combine(unpack, entry.Name), overwrite: true);
        }

        // Verify UEFI firmware tag in OVF
        var ovfText = File.ReadAllText(Path.Combine(unpack, "bootable-w11.ovf"));
        Assert.Contains("vmw:key=\"firmware\" vmw:value=\"efi\"", ovfText, StringComparison.Ordinal);

        // Decode VMDK back to raw disk image and parse GPT with PartitionTable.ReadFromStream
        var rawDisk = VmdkVerifier.ReadAllBytes(Path.Combine(unpack, "bootable-w11.vmdk"), diskBytes);
        using var ms = new MemoryStream(rawDisk, writable: false);
        var parsed = PartitionTable.ReadFromStream(ms, 0, diskBytes, new TestLogger());
        Assert.Equal(diskGuid, parsed.DiskGuid, ignoreCase: true);
        Assert.Equal(2, parsed.Partitions.Count);
        Assert.Equal(espBytes, rawDisk.AsSpan(1024 * 1024, 1024 * 1024).ToArray());
        Assert.Equal(osBytes, rawDisk.AsSpan(2 * 1024 * 1024, 2 * 1024 * 1024).ToArray());
    }

    [Fact]
    public void ExportDiskPlanDirect_SkipRecovery_OmitsRecoveryAndWritesBootableUefiOva()
    {
        var espBytes = new byte[1024 * 1024];
        var osBytes = new byte[2 * 1024 * 1024];
        var recBytes = new byte[1024 * 1024];
        var rng = new Random(99);
        rng.NextBytes(espBytes);
        rng.NextBytes(osBytes);
        rng.NextBytes(recBytes);
        var espPath = Path.Combine(_dir, "d-esp.img");
        var osPath = Path.Combine(_dir, "d-os.img");
        var recPath = Path.Combine(_dir, "d-rec.img");
        File.WriteAllBytes(espPath, espBytes);
        File.WriteAllBytes(osPath, osBytes);
        File.WriteAllBytes(recPath, recBytes);

        const long diskBytes = 7L * 1024 * 1024;
        var diskGuid = Guid.NewGuid().ToString("D");
        var layout = new DiskLayout
        {
            DiskNumber = 0,
            DiskGuid = diskGuid,
            DiskSizeBytes = diskBytes,
            SectorSizeBytes = 512,
            Partitions = new List<PartitionRecord>
            {
                new() { PartitionNumber = 1, Name = "EFI", TypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", UniqueGuid = Guid.NewGuid().ToString("D"), FirstLba = 2048, LastLba = 4095, OffsetBytes = 1024L * 1024, LengthBytes = 1024L * 1024, Filesystem = "fat32" },
                new() { PartitionNumber = 2, Name = "MSR", TypeGuid = "e3c9e316-0b5c-4db8-817d-f92df00215ae", UniqueGuid = Guid.NewGuid().ToString("D"), FirstLba = 4096, LastLba = 4127, OffsetBytes = 2L * 1024 * 1024, LengthBytes = 16384, Filesystem = "none", Skipped = true, SkipReason = "msr" },
                new() { PartitionNumber = 3, Name = "OS", TypeGuid = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", UniqueGuid = Guid.NewGuid().ToString("D"), FirstLba = 6144, LastLba = 10239, OffsetBytes = 3L * 1024 * 1024, LengthBytes = 2L * 1024 * 1024, Filesystem = "ntfs" },
                new() { PartitionNumber = 4, Name = "Recovery", TypeGuid = "de94bba4-06d1-4d40-a16a-bfd50179d6ac", UniqueGuid = Guid.NewGuid().ToString("D"), FirstLba = 10240, LastLba = 12287, OffsetBytes = 5L * 1024 * 1024, LengthBytes = 1024L * 1024, Filesystem = "ntfs" },
            },
        };
        DiskCapture.ApplyCaptureFilters(layout, skipRecovery: true, null, new TestLogger());
        Assert.True(layout.Partitions[3].Skipped);
        Assert.Equal("recovery-skipped", layout.Partitions[3].SkipReason);

        var plan = new DiskCapturePlan(diskGuid, diskBytes, 512, new[]
        {
            new PartitionImageInput(1, "EFI", layout.Partitions[0].TypeGuid, layout.Partitions[0].UniqueGuid, 2048, 4095, 1024L * 1024, 1024L * 1024, "fat32", false, null, new FileBlockSource(espPath), null),
            new PartitionImageInput(2, "MSR", layout.Partitions[1].TypeGuid, layout.Partitions[1].UniqueGuid, 4096, 4127, 2L * 1024 * 1024, 16384, "none", true, "msr", null, null),
            new PartitionImageInput(3, "OS", layout.Partitions[2].TypeGuid, layout.Partitions[2].UniqueGuid, 6144, 10239, 3L * 1024 * 1024, 2L * 1024 * 1024, "ntfs", false, null, new FileBlockSource(osPath), null),
            new PartitionImageInput(4, "Recovery", layout.Partitions[3].TypeGuid, layout.Partitions[3].UniqueGuid, 10240, 12287, 5L * 1024 * 1024, 1024L * 1024, "ntfs", true, "recovery-skipped", null, null),
        });

        var ovaPath = Path.Combine(_dir, "direct-w11.ova");
        var res = OvfExporter.ExportDiskPlanDirect("ova", ovaPath, "direct-w11", plan, Array.Empty<string>(), new TestLogger());
        // Capacity shrinks past the omitted Recovery partition (5 MiB + backup GPT < 8 MiB).
        Assert.True(res.CapacityBytes < diskBytes);

        var unpack = Path.Combine(_dir, "direct-unpacked");
        Directory.CreateDirectory(unpack);
        using (var fs = File.OpenRead(ovaPath))
        using (var tar = new TarReader(fs))
        {
            while (tar.GetNextEntry() is { } entry)
                entry.ExtractToFile(Path.Combine(unpack, entry.Name), overwrite: true);
        }

        var rawDisk = VmdkVerifier.ReadAllBytes(Path.Combine(unpack, "direct-w11.vmdk"), res.CapacityBytes);
        using var ms = new MemoryStream(rawDisk, writable: false);
        var parsed = PartitionTable.ReadFromStream(ms, 0, res.CapacityBytes, new TestLogger());
        // Recovery omitted from GPT, EFI + MSR + OS preserved.
        Assert.Equal(3, parsed.Partitions.Count);
        Assert.Equal(espBytes, rawDisk.AsSpan(1024 * 1024, 1024 * 1024).ToArray());
        Assert.Equal(osBytes, rawDisk.AsSpan(3 * 1024 * 1024, 2 * 1024 * 1024).ToArray());
    }

    [Fact]
    public void NormalizeDrive_HandlesFormats()
    {
        Assert.Equal("C:", DiskCapture.NormalizeDrive("c"));
        Assert.Equal("C:", DiskCapture.NormalizeDrive("c:"));
        Assert.Equal("C:", DiskCapture.NormalizeDrive(@"C:\"));
        Assert.Equal("D:", DiskCapture.NormalizeDrive(" d:/ "));
    }

    [Fact]
    public void AlignedCapacity_ZeroOrNegative_Throws()
    {
        Assert.Throws<InvalidDataException>(() => OvfExporter.AlignedCapacity(0));
        Assert.Throws<InvalidDataException>(() => OvfExporter.AlignedCapacity(-1));
    }

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
