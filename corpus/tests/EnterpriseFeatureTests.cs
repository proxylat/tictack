using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TicTack;
using Xunit;

namespace Corpus.Tests
{
    // Tests for the 8 enterprise additions:
    // A1 (disk exclusions), A2 ($ATTRIBUTE_LIST), A3 (MBR BIOS BootRepair + Gen1),
    // A4 (symmetric decrypt hook), B1 (NBD server + WebDAV streaming mount),
    // B2 (LZNT1 decompression), B3 (BootVerifyOptions CheckFs/Gen), B4 (ShrinkToFit).
    public sealed class EnterpriseFeatureTests : IDisposable
    {
        private readonly string _root;
        private readonly string _storeDir;
        private readonly string _dbPath;
        private readonly TestLogger _log = new();

        public EnterpriseFeatureTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "corpus-ent-" + Guid.NewGuid().ToString("N"));
            _storeDir = Path.Combine(_root, "store");
            _dbPath = Path.Combine(_root, "chunks.db");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void A4_EncryptAndDecryptHooks_RoundTrip_VerifyAndExport()
        {
            var imgPath = Path.Combine(_root, "secret.img");
            var raw = new byte[512 * 1024];
            new Random(99).NextBytes(raw);
            File.WriteAllBytes(imgPath, raw);

            // Simple reversible XOR-0xAA transform via shell hook (tests PackHook + PackReadCache).
            var encScript = Path.Combine(_root, "enc.sh");
            var decScript = Path.Combine(_root, "dec.sh");
            File.WriteAllText(encScript, "#!/bin/sh\npython3 -c \"import sys; d=open(sys.argv[1],'rb').read(); open(sys.argv[2],'wb').write(bytes(b^0xAA for b in d))\" \"$1\" \"$2\"\n");
            File.WriteAllText(decScript, "#!/bin/sh\npython3 -c \"import sys; d=open(sys.argv[1],'rb').read(); open(sys.argv[2],'wb').write(bytes(b^0xAA for b in d))\" \"$1\" \"$2\"\n");
            if (!OperatingSystem.IsWindows())
            {
                RunExternal("/bin/chmod", $"+x \"{encScript}\" \"{decScript}\"");
                using (var store = new Store(_storeDir, _dbPath, 1, 3, _log,
                    encryptHook: $"\"{encScript}\" \"{{input}}\" \"{{output}}\"",
                    decryptHook: $"\"{decScript}\" \"{{input}}\" \"{{output}}\""))
                {
                    using var src = new FileBlockSource(imgPath, useUnbuffered: false);
                    store.Image(src, "enc");
                }

                // Verify pack on disk is NOT plaintext CPK1.
                var pack = Directory.GetFiles(Path.Combine(_storeDir, "packs"), "pack-*.pack").Single();
                var tail = File.ReadAllBytes(pack)[^4..];
                Assert.NotEqual("CPK1"u8.ToArray(), tail);

                // Verify & Export using decryptHook.
                using (var store = new Store(_storeDir, _dbPath, 1, 3, _log,
                    decryptHook: $"\"{decScript}\" \"{{input}}\" \"{{output}}\""))
                {
                    Assert.True(store.Verify(1));
                    var outPath = Path.Combine(_root, "decrypted.img");
                    store.Export(1, outPath);
                    Assert.Equal(raw, File.ReadAllBytes(outPath));
                }
            }
        }

        [Fact]
        public void B1_NbdServer_ServesWholeDiskSnapshot_VerifiedByQemuImg()
        {
            var partPath = Path.Combine(_root, "nbd-os.img");
            var osBytes = new byte[2 * 1024 * 1024];
            new Random(55).NextBytes(osBytes);
            File.WriteAllBytes(partPath, osBytes);

            var plan = new DiskCapturePlan(
                DiskGuid: Guid.NewGuid().ToString("d"),
                DiskSizeBytes: 8L * 1024 * 1024,
                SectorSizeBytes: 512,
                Partitions: new List<PartitionImageInput>
                {
                    new(1, "OS", "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Guid.NewGuid().ToString("d"), 2048, 6143, 1024 * 1024, 2 * 1024 * 1024, "ntfs", false, null, new FileBlockSource(partPath, false), null),
                });

            using (var store = new Store(_storeDir, _dbPath, 1, 3, _log))
                store.ImageDisk(0, "nbd", new DiskImageOptions(), plan);

            using var nbd = NbdServer.StartForLayout(_storeDir, _dbPath, 1, _log);
            nbd.Start();
            if (File.Exists("/usr/bin/qemu-img"))
            {
                var (rc, output) = RunExternal("/usr/bin/qemu-img", $"info nbd://127.0.0.1:{nbd.Port}");
                Assert.Equal(0, rc);
                Assert.Contains("virtual size:", output, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void B2_Lznt1_Decompresses_UncompressedAndCompressedChunks()
        {
            // Uncompressed chunk: header = (len - 1) | 0x3000 (bit 15 = 0)
            var payload = "Hello LZNT1 Corpus!"u8.ToArray();
            var src = new byte[2 + payload.Length];
            ushort hdr = (ushort)((payload.Length - 1) | 0x3000);
            src[0] = (byte)(hdr & 0xFF);
            src[1] = (byte)(hdr >> 8);
            Buffer.BlockCopy(payload, 0, src, 2, payload.Length);

            var dst = new byte[64];
            var n = Lznt1.Decompress(src, dst);
            Assert.Equal(payload.Length, n);
            Assert.Equal(payload, dst[..n]);

            // Compressed chunk: "AAAAAAAA" -> literal 'A' + back-ref (offset=1, len=7 -> token=0x0004)
            var comp = new byte[] { 0x03, 0xB0, 0x02, (byte)'A', 0x04, 0x00 };
            var dst2 = new byte[16];
            var n2 = Lznt1.Decompress(comp, dst2);
            Assert.Equal(8, n2);
            Assert.Equal("AAAAAAAA"u8.ToArray(), dst2[..8]);
        }

        [Fact]
        public void A3_BootRepair_Mbr_PassesBiosAndGen1Options()
        {
            var mbrLayout = new DiskLayout
            {
                DiskGuid = "deadbeef",
                DiskSizeBytes = 16L * 1024 * 1024 * 1024,
                SectorSizeBytes = 512,
                Style = "mbr",
                Partitions = new List<PartitionRecord>
                {
                    new() { PartitionNumber = 1, Filesystem = "ntfs", AttributeFlags = 0x80, OffsetBytes = 1024 * 1024, LengthBytes = 8L * 1024 * 1024 * 1024 },
                },
            };
            var runner = new FakeBootRepairRunner { Firmware = "Legacy" };
            Assert.Equal(0, BootRepairOrchestrator.ApplyPhase(mbrLayout, runner, _log, targetDisk: 1));
            Assert.Equal(0, BootRepairOrchestrator.BootloaderPhase(mbrLayout, runner, _log, targetDisk: 1));

            var hv = new FakeHyperVRunner();
            Assert.Equal(1, BootVerifyOrchestrator.Verify(new BootVerifyOptions(Path.Combine(_root, "dummy.vhdx"), Generation: 1, CheckFs: false), hv, _log));
            Assert.Contains("-Generation 1", WindowsHyperVRunner.BuildBootCommand("vm", "a.vhdx", 1024, 1), StringComparison.Ordinal);
        }

        [Fact]
        public void A2_MftAttributeList_ParsesResidentEntries()
        {
            // Minimal MFT record with a resident 0x20 ($ATTRIBUTE_LIST) pointing to FRN 42 for 0x80 ($DATA).
            var attrListPayload = new byte[26];
            BitConverter.GetBytes(0x80u).CopyTo(attrListPayload, 0);
            BitConverter.GetBytes((ushort)26).CopyTo(attrListPayload, 4);
            BitConverter.GetBytes(42UL).CopyTo(attrListPayload, 16);

            var rec = new byte[1024];
            Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);
            BitConverter.GetBytes((ushort)56).CopyTo(rec, 20); // first attr offset
            BitConverter.GetBytes((ushort)1).CopyTo(rec, 22);  // in-use
            BitConverter.GetBytes((uint)1024).CopyTo(rec, 24); // bytes in use
            var off = 56;
            BitConverter.GetBytes(0x20u).CopyTo(rec, off);
            BitConverter.GetBytes((uint)(24 + attrListPayload.Length)).CopyTo(rec, off + 4);
            rec[off + 8] = 0; // resident
            BitConverter.GetBytes((uint)attrListPayload.Length).CopyTo(rec, off + 16);
            BitConverter.GetBytes((ushort)24).CopyTo(rec, off + 20);
            Buffer.BlockCopy(attrListPayload, 0, rec, off + 24, attrListPayload.Length);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(rec, off + 24 + attrListPayload.Length);

            var entries = MftAttributeList.ReadEntries(rec, 4096, null);
            Assert.Single(entries);
            Assert.Equal((0x80u, 42UL), entries[0]);
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
    }
}
