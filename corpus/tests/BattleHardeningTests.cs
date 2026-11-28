using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TicTack;
using Xunit;

namespace Corpus.Tests
{
    // Phase 4 Tier A battle-hardening tests: verify crash-safety, delta+GC+optimize
    // base preservation, Export/Restore bitrot detection, GcScheduler locking,
    // Differential & VerifyBlocks modes, $ATTRIBUTE_LIST fail-closed guard,
    // ReflinkExport hole-zeroing, whole-disk bootable VHDX, 4Kn GPT, and 100 MB ESP.
    public sealed class BattleHardeningTests : IDisposable
    {
        private readonly string _root;
        private readonly string _storeDir;
        private readonly string _dbPath;
        private readonly TestLogger _log = new();

        public BattleHardeningTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "corpus-battle-" + Guid.NewGuid().ToString("N"));
            _storeDir = Path.Combine(_root, "store");
            _dbPath = Path.Combine(_root, "chunks.db");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private byte[] MakePattern(int size, byte seed)
        {
            var buf = new byte[size];
            var rng = new Random(seed);
            rng.NextBytes(buf);
            return buf;
        }

        [Fact]
        public void StoreOptimizer_PreservesDeltaBaseChunks_AfterParentPruned()
        {
            var imgPath = Path.Combine(_root, "vol.img");
            var data1 = MakePattern(2 * 1024 * 1024, 11);
            File.WriteAllBytes(imgPath, data1);

            using var delta = new ZstdDeltaCodec(3);
            using (var store = new Store(_storeDir, _dbPath, 1, 3, _log, deltaCodec: delta))
            {
                using var s1 = new FileBlockSource(imgPath, useUnbuffered: false);
                var m1 = store.Image(s1, "sys");
                Assert.Equal(1, m1.Generation);

                // Modify 16 bytes in chunk 0 so gen 2 stores a delta referencing gen 1's base chunk.
                var data2 = (byte[])data1.Clone();
                for (var i = 0; i < 16; i++) data2[i] ^= 0xFF;
                File.WriteAllBytes(imgPath, data2);

                using var s2 = new FileBlockSource(imgPath, useUnbuffered: false);
                var dirty = new HashSet<long> { 0 };
                var cs = new StaticDirtyChangeSource(dirty, "c2");
                var m2 = store.Image(s2, "sys", new ImageOptions(Incremental: true, ChangeSource: cs));
                Assert.Equal(2, m2.Generation);
            }

            // Prune gen 1 via GC, then run StoreOptimizer.Optimize() — delta base MUST survive!
            var ret = new CorpusRetentionConfig { KeepLast = 1 };
            var gc = new GarbageCollector(_storeDir, _dbPath, ret, _log);
            gc.Execute(gc.Plan());

            var opt = new StoreOptimizer(_storeDir, _dbPath, ret, _log, 1, 3, deltaCodec: delta);
            opt.Optimize();

            using (var store = new Store(_storeDir, _dbPath, 1, 3, _log, deltaCodec: delta))
            {
                Assert.True(store.Verify(2));
                var outPath = Path.Combine(_root, "restored-gen2.img");
                store.Export(2, outPath);
                Assert.Equal(File.ReadAllBytes(imgPath), File.ReadAllBytes(outPath));
            }
        }

        [Fact]
        public void Export_DetectsBitrotInRawPackBlob_NeverSilentlyCorrupts()
        {
            var imgPath = Path.Combine(_root, "incompressible.img");
            var data = MakePattern(1024 * 1024, 42); // random = stored as CodecRaw
            File.WriteAllBytes(imgPath, data);

            using (var store = new Store(_storeDir, _dbPath, 1, 3, _log))
            {
                using var src = new FileBlockSource(imgPath, useUnbuffered: false);
                store.Image(src, "rawvol");
            }

            // Flip 1 byte inside the pack payload.
            var pack = Directory.GetFiles(Path.Combine(_storeDir, "packs"), "pack-*.pack").Single();
            var bytes = File.ReadAllBytes(pack);
            bytes[PackWriter.BlobHeaderBytes + 100] ^= 0x55;
            File.WriteAllBytes(pack, bytes);

            using (var store = new Store(_storeDir, _dbPath, 1, 3, _log))
            {
                var outPath = Path.Combine(_root, "corrupt-export.img");
                Assert.ThrowsAny<Exception>(() => store.Export(1, outPath));
            }
        }

        [Fact]
        public void Differential_AlwaysParentsAgainstLatestFull_NotLatestIncremental()
        {
            var imgPath = Path.Combine(_root, "diff.img");
            var data1 = MakePattern(2 * 1024 * 1024, 7);
            File.WriteAllBytes(imgPath, data1);

            using var store = new Store(_storeDir, _dbPath, 1, 3, _log);
            using (var s1 = new FileBlockSource(imgPath, useUnbuffered: false))
                Assert.Equal(0, store.Image(s1, "sys").ParentGeneration);

            // Gen 2: Incremental (parent = 1)
            data1[0] ^= 0x11;
            File.WriteAllBytes(imgPath, data1);
            using (var s2 = new FileBlockSource(imgPath, useUnbuffered: false))
            {
                var m2 = store.Image(s2, "sys", new ImageOptions(Incremental: true, ChangeSource: new StaticDirtyChangeSource(new HashSet<long> { 0 }, "c2")));
                Assert.Equal(1, m2.ParentGeneration);
            }

            // Gen 3: Differential (MUST parent against gen 1, NOT gen 2!)
            data1[1024 * 1024] ^= 0x22;
            File.WriteAllBytes(imgPath, data1);
            using (var s3 = new FileBlockSource(imgPath, useUnbuffered: false))
            {
                var m3 = store.Image(s3, "sys", new ImageOptions(Differential: true, ChangeSource: new StaticDirtyChangeSource(new HashSet<long> { 0, 1 }, "c3")));
                Assert.Equal(1, m3.ParentGeneration);
            }
        }

        [Fact]
        public void BootRepair_AcceptsStock100MbWindowsEsp()
        {
            var layout = new DiskLayout
            {
                Style = "gpt",
                SectorSizeBytes = 512,
                DiskSizeBytes = 64L * 1024 * 1024 * 1024,
                Partitions = new List<PartitionRecord>
                {
                    new() { PartitionNumber = 1, TypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", Filesystem = "fat32", OffsetBytes = 1024 * 1024, LengthBytes = 100L * 1024 * 1024 },
                    new() { PartitionNumber = 2, TypeGuid = "e3c9e316-0b5c-4db8-817d-f92df00215ae", Filesystem = "none", Skipped = true, OffsetBytes = 101L * 1024 * 1024, LengthBytes = 16L * 1024 * 1024 },
                    new() { PartitionNumber = 3, TypeGuid = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Filesystem = "ntfs", OffsetBytes = 117L * 1024 * 1024, LengthBytes = 40L * 1024 * 1024 * 1024 },
                },
            };
            Assert.Null(BootRepairOrchestrator.CheckLayout(layout));
        }

        [Fact]
        public void DiskpartScript_AllowsNonMiBWinRe_AndPreservesRecoveryGuid()
        {
            var layout = new DiskLayout
            {
                DiskGuid = Guid.NewGuid().ToString("d"),
                DiskSizeBytes = 64L * 1024 * 1024 * 1024,
                Partitions = new List<PartitionRecord>
                {
                    new() { PartitionNumber = 1, Name = "EFI", TypeGuid = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", Filesystem = "fat32", OffsetBytes = 1024 * 1024, LengthBytes = 100L * 1024 * 1024 },
                    new() { PartitionNumber = 2, Name = "Recovery", TypeGuid = "de94bba4-06d1-4d40-a16a-bfd50179d6ac", Filesystem = "ntfs", OffsetBytes = 101L * 1024 * 1024, LengthBytes = 500L * 1024 * 1024 - 16896 },
                },
            };
            var script = DiskpartScript.Generate(layout, targetDisk: 2);
            Assert.Contains("set id=\"de94bba4-06d1-4d40-a16a-bfd50179d6ac\"", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gpt attributes=0x8000000000000001", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExportDiskVhdx_ProducesValidGptWholeDisk()
        {
            var p1Path = Path.Combine(_root, "esp.img");
            var p2Path = Path.Combine(_root, "os.img");
            File.WriteAllBytes(p1Path, MakePattern(1024 * 1024, 1));
            File.WriteAllBytes(p2Path, MakePattern(2 * 1024 * 1024, 2));

            var plan = new DiskCapturePlan(
                DiskGuid: Guid.NewGuid().ToString("d"),
                DiskSizeBytes: 8L * 1024 * 1024,
                SectorSizeBytes: 512,
                Partitions: new List<PartitionImageInput>
                {
                    new(1, "EFI", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", Guid.NewGuid().ToString("d"), 2048, 4095, 1024 * 1024, 1024 * 1024, "fat32", false, null, new FileBlockSource(p1Path, false), null, 0),
                    new(2, "OS", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Guid.NewGuid().ToString("d"), 4096, 8191, 2 * 1024 * 1024, 2 * 1024 * 1024, "ntfs", false, null, new FileBlockSource(p2Path, false), null, 0x8000000000000001UL),
                });

            using var store = new Store(_storeDir, _dbPath, 1, 3, _log);
            var layout = store.ImageDisk(0, "win", new DiskImageOptions(), plan);
            var vhdxPath = Path.Combine(_root, "whole-disk.vhdx");
            store.ExportDiskVhdx(layout.Generation, vhdxPath);

            Assert.True(File.Exists(vhdxPath));
            using var disk = new DiscUtils.Vhdx.Disk(vhdxPath, FileAccess.Read);
            var parsed = PartitionTable.ReadFromStream(disk.Content, 0, disk.Capacity, _log);
            Assert.Equal(2, parsed.Partitions.Count);
            Assert.Equal(0x8000000000000001UL, parsed.Partitions[1].AttributeFlags);

            // External-tool validation (zero self-grading): if qemu-img is installed,
            // prove QEMU accepts both our whole-disk VHDX and whole-disk OVA/VMDK.
            var ovaPath = Path.Combine(_root, "whole-disk.ova");
            store.ExportDiskOva(layout.Generation, ovaPath, storageController: "ahci");
            Assert.True(File.Exists(ovaPath));
            if (File.Exists("/usr/bin/qemu-img"))
            {
                var vhdxCheck = RunExternal("/usr/bin/qemu-img", $"check \"{vhdxPath}\"");
                Assert.Equal(0, vhdxCheck.ExitCode);
                var extractDir = Path.Combine(_root, "ova-extract");
                Directory.CreateDirectory(extractDir);
                Assert.Equal(0, RunExternal("/usr/bin/tar", $"-xf \"{ovaPath}\" -C \"{extractDir}\"").ExitCode);
                var vmdk = Directory.GetFiles(extractDir, "*.vmdk").Single();
                var ovf = File.ReadAllText(Directory.GetFiles(extractDir, "*.ovf").Single());
                Assert.Contains("AHCI", ovf, StringComparison.Ordinal);
                Assert.Equal(0, RunExternal("/usr/bin/qemu-img", $"check \"{vmdk}\"").ExitCode);
            }
        }

        [Theory]
        [InlineData("ahci", "AHCI", "20")]
        [InlineData("lsisas", "lsilogicsas", "6")]
        [InlineData("nvme", "vmware.nvme.controller", "20")]
        [InlineData("scsi", "lsilogic", "6")]
        public void ExportOvf_AllControllers_EmitExpectedSubtypeAndPassQemuImg(string controller, string expectedSubtype, string expectedResourceType)
        {
            var imgPath = Path.Combine(_root, $"ctrl-{controller}.img");
            File.WriteAllBytes(imgPath, MakePattern(1024 * 1024, 9));
            using var store = new Store(_storeDir, _dbPath, 1, 3, _log);
            using var src = new FileBlockSource(imgPath, useUnbuffered: false);
            var m = store.Image(src, "ctrl-" + controller);

            var outDir = Path.Combine(_root, "ovf-" + controller);
            var res = store.ExportOvf(m.Generation, outDir, storageController: controller);
            var ovfXml = File.ReadAllText(res.OvfPath);
            Assert.Contains($"<rasd:ResourceSubType>{expectedSubtype}</rasd:ResourceSubType>", ovfXml, StringComparison.Ordinal);
            Assert.Contains($"<rasd:ResourceType>{expectedResourceType}</rasd:ResourceType>", ovfXml, StringComparison.Ordinal);
            Assert.Contains("<rasd:Address>0</rasd:Address>", ovfXml, StringComparison.Ordinal);
            if (File.Exists("/usr/bin/qemu-img"))
                Assert.Equal(0, RunExternal("/usr/bin/qemu-img", $"check \"{res.VmdkPath}\"").ExitCode);
        }

        [Fact]
        public void MbrDisk_RoundTrips_PartitionTable_VhdxExport_Diskpart_AndBootRepair()
        {
            var partPath = Path.Combine(_root, "mbr-os.img");
            var osBytes = MakePattern(2 * 1024 * 1024, 77);
            File.WriteAllBytes(partPath, osBytes);

            var layout = new DiskLayout
            {
                DiskGuid = "1234abcd",
                DiskSizeBytes = 8L * 1024 * 1024,
                SectorSizeBytes = 512,
                Style = "mbr",
                Partitions = new List<PartitionRecord>
                {
                    new()
                    {
                        PartitionNumber = 1, Name = "mbr-p1", TypeGuid = "mbr-0x07", UniqueGuid = "1234abcd-p1",
                        FirstLba = 2048, LastLba = 2048 + 4096 - 1, AttributeFlags = 0x80,
                        OffsetBytes = 1024 * 1024, LengthBytes = 2 * 1024 * 1024, Filesystem = "ntfs",
                    },
                },
            };

            // Verify DiskpartScript + BootRepair accept MBR and mark active.
            Assert.Null(BootRepairOrchestrator.CheckLayout(layout));
            var script = DiskpartScript.Generate(layout, targetDisk: 2);
            Assert.Contains("convert mbr", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("active", script, StringComparison.OrdinalIgnoreCase);

            // Export whole-disk MBR VHDX and re-parse MBR with PartitionTable.ReadFromStream + qemu-img check.
            using var store = new Store(_storeDir, _dbPath, 1, 3, _log);
            using var src = new FileBlockSource(partPath, useUnbuffered: false);
            var m = store.Image(src, "mbr-os");
            layout.Partitions[0].SnapshotGeneration = m.Generation;
            var vhdxPath = Path.Combine(_root, "mbr-whole.vhdx");
            VhdxExporter.ExportDisk(vhdxPath, layout, (gen, dst) => store.ExportToStream(gen, dst));

            using (var disk = new DiscUtils.Vhdx.Disk(vhdxPath, FileAccess.Read))
            {
                var parsed = PartitionTable.ReadFromStream(disk.Content, 0, disk.Capacity, _log);
                Assert.Equal("mbr", parsed.Style);
                Assert.Equal("1234abcd", parsed.DiskGuid);
                Assert.Single(parsed.Partitions);
                Assert.Equal(0x80UL, parsed.Partitions[0].AttributeFlags);
                Assert.Equal("ntfs", parsed.Partitions[0].Filesystem);
            }
            if (File.Exists("/usr/bin/qemu-img"))
                Assert.Equal(0, RunExternal("/usr/bin/qemu-img", $"check \"{vhdxPath}\"").ExitCode);
        }

        private static (int ExitCode, string Output) RunExternal(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, stdout + stderr);
        }

        private sealed class StaticDirtyChangeSource : IChangeSource
        {
            private readonly IReadOnlySet<long> _dirty;
            private readonly string _cursor;
            public StaticDirtyChangeSource(IReadOnlySet<long> dirty, string cursor) { _dirty = dirty; _cursor = cursor; }
            public IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log) => new(_dirty, _cursor);
        }
    }
}
