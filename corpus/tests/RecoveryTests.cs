using System.Security.Cryptography;
using System.Text.Json;

namespace Corpus;

// Recovery-media contract tests (recovery-spec §8). Pins the §4 member
// shapes the code lane implements in parallel; every cross-type call below
// is an exact-name assertion (CS0246/CS0117/CS1061 on build = contract drift).
// Expected to FAIL (not compile) until the code lane lands — by design.
public sealed class RecoveryTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private readonly string _dir;
    private readonly string _storeRoot;
    private readonly string _dbPath;

    public RecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_recovery_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        _dbPath = Path.Combine(_dir, "state", "corpus-chunks.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class ListProgress : IProgress<long>
    {
        public readonly List<long> Values = new();
        public void Report(long value) { lock (Values) Values.Add(value); }
    }

    private static void SeedRandom(string path, int mib, int seed)
    {
        var buf = new byte[Mib];
        var rng = new Random(seed);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < mib; i++)
        {
            rng.NextBytes(buf);
            f.Write(buf, 0, buf.Length);
        }
    }

    private static void PreFill(string path, long length, byte fill = 0xAA)
    {
        var buf = new byte[Mib];
        Array.Fill(buf, fill);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        for (long left = length; left > 0;)
        {
            var n = (int)Math.Min(buf.Length, left);
            f.Write(buf, 0, n);
            left -= n;
        }
    }

    private static string ShaOfFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private Store NewStore(TestLogger log, int chunkMib = 1) =>
        new(_storeRoot, _dbPath, chunkMib, 3, log);

    private static string TokenFor(string targetPath)
    {
        using var t = new FileBlockTarget(targetPath);
        return BootDiskGuard.TargetToken(t.DisplayName, t.Capacity);
    }

    private static RestoreOptions Opts(string target, string storeRoot, string token) =>
        new(Generation: 1, Target: target, StorePath: storeRoot, IndexPath: "",
            ConfirmToken: token, AllowBootDisk: false, SkipVerify: false, DryRun: false);

    private static int RunRestore(Store store, TestLogger log, RestoreOptions opts, IProgress<long>? progress = null)
    {
        var orch = new RestoreOrchestrator(store, log, path => new FileBlockTarget(path));
        if (progress is null)
            return orch.Restore(opts);
        return orch.Restore(opts, progress);
    }

    [Fact]
    public void ExportToStream_MatchesExport()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        var fileOut = Path.Combine(_dir, "export.img");
        store.Export(1, fileOut);
        using var ms = new MemoryStream();
        store.ExportToStream(1, ms);
        Assert.Equal(File.ReadAllBytes(fileOut), ms.ToArray());
    }

    [Fact]
    public void GetManifest_ReturnsVerifiedManifest()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        using var store = NewStore(new TestLogger());
        SnapshotManifest imaged;
        using (var source = new FileBlockSource(vol))
            imaged = store.Image(source, "t1");

        var got = store.GetManifest(1);
        Assert.Equal(imaged.Generation, got.Generation);
        Assert.Equal(imaged.Name, got.Name);
        Assert.Equal(imaged.VolumeLength, got.VolumeLength);
        Assert.Equal(imaged.Chunks.Count, got.Chunks.Count);

        // Tamper one manifest byte (HMAC must reject, valid JSON or not).
        var manifestPath = Directory.GetFiles(Path.Combine(_storeRoot, "manifests"), "*.json").OrderBy(x => x).First();
        using (var f = new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            f.Seek(100, SeekOrigin.Begin);
            var b = (byte)f.ReadByte();
            f.Seek(100, SeekOrigin.Begin);
            f.WriteByte((byte)(b ^ 0xFF));
        }
        Assert.Throws<InvalidDataException>(() => store.GetManifest(1));
    }

    [Fact]
    public void RestoreToFile_RoundTripsBytes()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 64, 7);
        var log = new TestLogger();
        SnapshotManifest m;
        using (var store = NewStore(log, chunkMib: 4))
        using (var source = new FileBlockSource(vol))
            m = store.Image(source, "big");

        var target = Path.Combine(_dir, "restored.img");
        using (var f = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            f.SetLength(m.VolumeLength);

        using (var store = NewStore(log, chunkMib: 4))
            Assert.Equal(0, RunRestore(store, log, Opts(target, _storeRoot, TokenFor(target))));

        Assert.Equal(new FileInfo(vol).Length, new FileInfo(target).Length);
        Assert.Equal(ShaOfFile(vol), ShaOfFile(target));
    }

    [Fact]
    public void RestoreVerify_DetectsCorruption()
    {
        // Verify re-reads target bytes and recomputes ChunkId vs the
        // manifest digest, so a store-side flip is caught: either the
        // write-phase decode throws or the verify re-read mismatches.
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        // Flip a byte inside the first blob's payload (past the V1 header)
        // so the stored bytes no longer match the manifest digest.
        var pack = Directory.GetFiles(Path.Combine(_storeRoot, "packs"), "*.pack").OrderBy(x => x).First();
        using (var f = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            f.Seek(PackWriter.BlobHeaderBytes + 4, SeekOrigin.Begin);
            var b = (byte)f.ReadByte();
            f.Seek(-1, SeekOrigin.Current);
            f.WriteByte((byte)(b ^ 0xFF));
        }

        var backing = Path.Combine(_dir, "restored.img");
        using (var store = NewStore(log))
            Assert.Equal(1, RunRestore(store, log, Opts(backing, _storeRoot, TokenFor(backing))));
        Assert.Contains("verify", string.Join("\n", log.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreRefuses_ShortTarget()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        var target = Path.Combine(_dir, "short.img");
        PreFill(target, 1L * Mib);
        var before = File.ReadAllBytes(target);

        using (var store = NewStore(log))
            Assert.Equal(1, RunRestore(store, log, Opts(target, _storeRoot, TokenFor(target))));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void RestoreRequires_ConfirmToken()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        // Missing token refuses.
        var missing = Path.Combine(_dir, "missing.img");
        PreFill(missing, 5L * Mib);
        using (var store = NewStore(log))
            Assert.Equal(1, RunRestore(store, log, Opts(missing, _storeRoot, "")));
        Assert.Equal(0xAA, File.ReadAllBytes(missing)[0]);

        // Wrong token refuses.
        var wrong = Path.Combine(_dir, "wrong.img");
        PreFill(wrong, 5L * Mib);
        using (var store = NewStore(log))
            Assert.Equal(1, RunRestore(store, log, Opts(wrong, _storeRoot, "WRONG-TOKEN")));
        Assert.Equal(0xAA, File.ReadAllBytes(wrong)[0]);

        // Dry run with a valid token succeeds but writes nothing.
        var dry = Path.Combine(_dir, "dry.img");
        PreFill(dry, 5L * Mib);
        using (var store = NewStore(log))
        {
            var dryOpts = Opts(dry, _storeRoot, TokenFor(dry)) with { DryRun = true };
            Assert.Equal(0, RunRestore(store, log, dryOpts));
        }
        Assert.Equal(0xAA, File.ReadAllBytes(dry)[0]);
        Assert.Equal(5L * Mib, new FileInfo(dry).Length);

        // Dry run without a token still refuses (token gate precedes the branch).
        var dryNoToken = Path.Combine(_dir, "dry-notoken.img");
        PreFill(dryNoToken, 5L * Mib);
        using (var store = NewStore(log))
        {
            var dryNoTokenOpts = Opts(dryNoToken, _storeRoot, "") with { DryRun = true };
            Assert.Equal(1, RunRestore(store, log, dryNoTokenOpts));
        }
        Assert.Equal(0xAA, File.ReadAllBytes(dryNoToken)[0]);
    }

    [Fact]
    public void RestoreProgress_ReportsMonotonic()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using (var store = NewStore(log))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        var target = Path.Combine(_dir, "restored.img");
        using (var f = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            f.SetLength(5L * Mib);

        var prog = new ListProgress();
        using (var store = NewStore(log))
            Assert.Equal(0, RunRestore(store, log, Opts(target, _storeRoot, TokenFor(target)), prog));

        Assert.NotEmpty(prog.Values);
        var prev = 0L;
        foreach (var v in prog.Values)
        {
            Assert.InRange(v, 0, 5L * Mib);
            Assert.True(v >= prev, $"progress went backwards: {prev} -> {v}");
            prev = v;
        }
    }

    [Fact]
    public void RestoreRefuses_MissingIdKey()
    {
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 5, 42);
        var log = new TestLogger();
        using var store = NewStore(log);
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");

        // Restore must never mint a key: id.key gone from disk while the
        // (already-open) store still holds its bytes in memory.
        File.Delete(Path.Combine(_storeRoot, "id.key"));

        var calls = 0;
        var target = Path.Combine(_dir, "restored.img");
        var orch = new RestoreOrchestrator(store, log, path => { calls++; return new FileBlockTarget(path); });
        var rc = orch.Restore(Opts(target, _storeRoot, "any-token"));
        Assert.Equal(1, rc);
        Assert.Equal(0, calls);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(Path.Combine(_storeRoot, "id.key")));
        Assert.Contains("key", string.Join("\n", log.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StagePayload_LayoutExact()
    {
        Assert.Equal(26100, MediaLayout.AdksMinBuild);

        var publish = Path.Combine(_dir, "publish");
        Directory.CreateDirectory(publish);
        var exeBytes = new byte[] { 0x4D, 0x5A, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(publish, "Corpus.exe"), exeBytes);
        File.WriteAllBytes(Path.Combine(publish, "Corpus.dll"), new byte[] { 9, 9 });
        File.WriteAllText(Path.Combine(publish, "Corpus.runtimeconfig.json"), "{}");

        var stage = Path.Combine(_dir, "stage");
        var opts = new MediaBuildOptions(AdkDir: null, IncludeStore: null, DbPath: null,
            Drivers: null, StagingDir: stage, UsbDrive: null, IsoPath: null, Initialize: false);
        MediaBuilder.StagePayload(stage, publish, opts, new TestLogger());

        Assert.Equal(exeBytes, File.ReadAllBytes(Path.Combine(stage, "Corpus", "Corpus.exe")));
        foreach (var name in new[] { "config_corpus.yaml", "restore.cmd", "README-restore.txt", "media-info.json" })
            Assert.True(File.Exists(Path.Combine(stage, "Corpus", name)), "staged payload missing: " + name);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "Corpus", "media-info.json"))))
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        // Every Corpus/ entry the layout contract names must be staged
        // (store/* only with --include-store; copype EFI/Boot/sources outputs
        // are step-3a, never staged).
        foreach (var entry in MediaLayout.ExpectedPayloadFiles)
        {
            var norm = entry.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Trim();
            if (norm.Contains("store", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!norm.StartsWith("Corpus" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Assert.True(File.Exists(Path.Combine(stage, norm)), "staged payload missing: " + entry);
        }

        // With --include-store the packs, manifests, id.key, and the index
        // copy (which does NOT live under the store) all land on the media.
        var vol = Path.Combine(_dir, "vol.img");
        SeedRandom(vol, 2, 11);
        using (var store = NewStore(new TestLogger()))
        using (var source = new FileBlockSource(vol))
            store.Image(source, "t1");
        var stage2 = Path.Combine(_dir, "stage2");
        var opts2 = opts with { IncludeStore = _storeRoot, DbPath = _dbPath, StagingDir = stage2 };
        MediaBuilder.StagePayload(stage2, publish, opts2, new TestLogger());
        var storeDir = Path.Combine(stage2, "Corpus", "store");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(storeDir, "packs"), "*.pack", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(storeDir, "manifests"), "*.json", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(storeDir, "id.key")), "staged store missing id.key");
        Assert.True(File.Exists(Path.Combine(storeDir, "corpus-chunks.db")), "staged store missing index copy");
    }

    [Fact]
    public void TargetToken_StableShape()
    {
        var display = Path.Combine(_dir, "disk2.img");
        var a = BootDiskGuard.TargetToken(display, 1_000_000_000_000L);
        Assert.Equal(a, BootDiskGuard.TargetToken(display, 1_000_000_000_000L));
        Assert.NotEqual(a, BootDiskGuard.TargetToken(display, 2_000_000_000_000L));
        Assert.StartsWith("FILE-", a, StringComparison.OrdinalIgnoreCase);

        var disk = BootDiskGuard.TargetToken(@"\\.\PhysicalDrive2", 1_000_000_000_000L);
        Assert.StartsWith("DISK", disk, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootDiskGuard_Linux_NoFalsePositive()
    {
        if (!OperatingSystem.IsLinux())
        {
            _ = BootDiskGuard.SystemDiskDevice();
            return;
        }
        Assert.Equal("unknown-linux", BootDiskGuard.SystemDiskDevice());
        var probe = Path.Combine(_dir, "probe.img");
        File.WriteAllBytes(probe, new byte[] { 1 });
        using var t = new FileBlockTarget(probe);
        Assert.False(BootDiskGuard.IsBootDisk(t, out var boot));
        Assert.Equal("unknown-linux", boot);
    }

    [Fact]
    public void NativeDisk_GetLengthBytes_BindsEntryPoint()
    {
        if (!OperatingSystem.IsWindows()) return;
        var probe = Path.Combine(_dir, "plain.bin");
        File.WriteAllBytes(probe, new byte[4096]);
        // IOCTL_DISK_GET_LENGTH_INFO on a plain file fails with IOException —
        // but it must BIND first. A missing EntryPoint (DllImport naming a
        // nonexistent kernel32 export) throws EntryPointNotFoundException
        // instead (proven on-box 2026-10-13: --list-disks died in PE).
        Assert.Throws<IOException>(() => NativeDisk.GetLengthBytes(probe));
    }
}
