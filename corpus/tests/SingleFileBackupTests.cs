using DiscUtils.Vhdx;

namespace Corpus;

// §12.1 tests. Windows-only seams (VirtDisk writes, real DISM) are
// platform-throw-pinned here; the on-box A/B and round trips are VM steps.
// NOTE (wiring): the test project links corpus sources explicitly — this
// file needs `<Compile Include="../SingleFileBackup.cs"
// Link="corpus/SingleFileBackup.cs" />` added to Corpus.Tests.csproj
// (same for Chunker/CapturePolicy/IBlockSource/Vhdx, most already linked).

internal sealed class SfbFakeDism : IDismRunner
{
    public List<string> Calls { get; } = new();
    public Func<string, DismResult>? Handler;

    public DismResult Run(string arguments)
    {
        Calls.Add(arguments);
        return Handler != null ? Handler(arguments) : new DismResult(0, "", "");
    }

    public static string? ExtractQuoted(string args, string flag)
    {
        var marker = flag + ":\"";
        var start = args.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = args.IndexOf('"', start);
        return end < 0 ? null : args.Substring(start, end - start);
    }
}

internal sealed class SfbSparseSource : ISparseBlockSource
{
    private readonly byte[] _data;
    private readonly List<(long Start, long Count)> _runs;
    private bool _disposed;

    public SfbSparseSource(byte[] data, List<(long Start, long Count)> runs, long bytesPerCluster = 4096)
    {
        _data = data;
        _runs = runs;
        BytesPerCluster = bytesPerCluster;
    }

    public long Length => _data.Length;
    public long BytesPerCluster { get; }
    public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns() => _runs;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MemoryStream(_data, writable: false);
    }

    public void Dispose() => _disposed = true;
}

internal sealed class SfbExclusionSource : IBlockSource, IExclusionResolver
{
    private readonly IBlockSource _inner;
    private readonly List<(long StartLcn, long ClusterCount)> _runs;
    private bool _disposed;

    public SfbExclusionSource(IBlockSource inner, List<(long StartLcn, long ClusterCount)> runs, long bytesPerCluster, IReadOnlyList<string> names)
    {
        _inner = inner;
        _runs = runs;
        BytesPerCluster = bytesPerCluster;
        ExclusionNames = names;
    }

    public long Length => _inner.Length;
    public IReadOnlyList<string> ExclusionNames { get; }
    public long BytesPerCluster { get; }
    public IReadOnlyList<(long StartLcn, long ClusterCount)> ExcludedRuns() => _runs;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.OpenRead();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }
}

public sealed class SingleFileBackupTests : IDisposable
{
    private const int Mib = 1024 * 1024;
    private static readonly DateTime Stamp = new(2026, 10, 16, 14, 30, 22);
    private readonly string _dir;

    public SingleFileBackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_sfb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string SeedVolume(string name = "vol.img", int mib = 5)
    {
        var path = Path.Combine(_dir, name);
        var random = new byte[Mib];
        new Random(42).NextBytes(random);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (var i = 0; i < mib; i++)
            f.Write(i % 2 == 0 ? random : new byte[Mib], 0, Mib);
        return path;
    }

    private static byte[] ReadAllContent(string vhdxPath, long length)
    {
        using var disk = new Disk(vhdxPath, FileAccess.Read);
        var back = new byte[length];
        disk.Content.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < back.Length)
        {
            var n = disk.Content.Read(back, read, back.Length - read);
            Assert.True(n > 0, "VHDX content ended early at " + read);
            read += n;
        }
        return back;
    }

    [Fact]
    public void ResolveTarget_FileForm_ReturnsVerbatim()
    {
        var target = Path.Combine(_dir, "custom.vhdx");
        Assert.Equal(target, SingleFileTarget.Resolve(target, "--to-vhdx", "T:", "vhdx", Stamp));
    }

    [Fact]
    public void ResolveTarget_BareFlag_RefusesNamingFlag()
    {
        var ex = Assert.Throws<SingleFileTargetMissingException>(() =>
            SingleFileTarget.Resolve(null, "--to-wim", "T:", "wim", Stamp));
        Assert.Contains("--to-wim", ex.Message);
    }

    [Fact]
    public void ResolveTarget_DirForm_AutoNames()
    {
        var resolved = SingleFileTarget.Resolve(_dir, "--to-vhdx", "T:", "vhdx", Stamp);
        Assert.Equal(Path.Combine(_dir, "T-20261016-143022.vhdx"), resolved);
    }

    [Fact]
    public void ResolveTarget_TrailingSeparator_AutoNames()
    {
        var arg = _dir + Path.DirectorySeparatorChar;
        var resolved = SingleFileTarget.Resolve(arg, "--to-esd", "T:", "esd", Stamp);
        Assert.Equal(Path.Combine(_dir, "T-20261016-143022.esd"), resolved);
    }

    [Fact]
    public void ResolveTarget_Collision_RefusesWithoutTouching()
    {
        var target = Path.Combine(_dir, "taken.vhdx");
        File.WriteAllText(target, "keep");
        var ex = Assert.Throws<SingleFileTargetExistsException>(() =>
            SingleFileTarget.Resolve(target, "--to-vhdx", "T:", "vhdx", Stamp));
        Assert.Contains(target, ex.Message);
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("T:", "T:\\backups\\x.vhdx", true)]
    [InlineData("T:", "t:\\X.VHDX", true)]
    [InlineData("T:\\", "T:\\x.vhdx", true)]
    [InlineData("T:", "E:\\x.vhdx", false)]
    [InlineData("C:\\mnt\\vol", "C:\\mnt\\vol\\x.vhdx", true)]
    [InlineData("C:\\mnt\\vol", "C:\\mnt\\vol2\\x.vhdx", false)]
    public void IsTargetOnSource_PrefixRules(string volume, string target, bool expect)
    {
        Assert.Equal(expect, SingleFileTarget.IsTargetOnSource(volume, target));
    }

    [Fact]
    public void SanitizeVolume_StripsDriveSyntax()
    {
        Assert.Equal("T", SingleFileTarget.SanitizeVolume("T:"));
        Assert.Equal("T", SingleFileTarget.SanitizeVolume("T:\\"));
        Assert.Equal("C--mnt-vol", SingleFileTarget.SanitizeVolume("C:\\mnt\\vol"));
    }

    [Fact]
    public void ParseKind_DefaultsDiscutils_RefusesGarbage()
    {
        Assert.Equal(VhdxWriterKind.DiscUtils, ExtentSinkFactory.ParseKind(null));
        Assert.Equal(VhdxWriterKind.DiscUtils, ExtentSinkFactory.ParseKind(""));
        Assert.Equal(VhdxWriterKind.VirtDisk, ExtentSinkFactory.ParseKind("virtdisk"));
        Assert.Equal(VhdxWriterKind.VirtDisk, ExtentSinkFactory.ParseKind("VirtDisk"));
        Assert.Throws<SingleFileBackupException>(() => ExtentSinkFactory.ParseKind("bogus"));
    }

    [Fact]
    public void GuardZstd_RefusesWimAndEsd_AllowsVhdx()
    {
        SingleFileBackup.GuardZstd(SingleFileKind.Vhdx, true);
        SingleFileBackup.GuardZstd(SingleFileKind.Wim, false);
        var wim = Assert.Throws<SingleFileZstdRefusedException>(() => SingleFileBackup.GuardZstd(SingleFileKind.Wim, true));
        Assert.Contains("--to-wim", wim.Message);
        var esd = Assert.Throws<SingleFileZstdRefusedException>(() => SingleFileBackup.GuardZstd(SingleFileKind.Esd, true));
        Assert.Contains("--to-esd", esd.Message);
    }

    [Fact]
    public void BackupVhdx_RoundTrips_AndVerifies()
    {
        var vol = SeedVolume();
        var target = Path.Combine(_dir, "t.vhdx");
        using var source = new FileBlockSource(vol);
        var log = new TestLogger();
        var returned = SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, log, Stamp);
        Assert.Equal(target, returned);
        Assert.Equal(File.ReadAllBytes(vol), ReadAllContent(target, source.Length));
        Assert.Contains(log.Messages, m => m.StartsWith("INF:verified ", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupVhdx_DirTarget_AutoNames()
    {
        var vol = SeedVolume(mib: 2);
        using var source = new FileBlockSource(vol);
        var returned = SingleFileBackup.BackupVhdx(source, "T:", _dir, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp);
        Assert.Equal(Path.Combine(_dir, "T-20261016-143022.vhdx"), returned);
        Assert.True(File.Exists(returned));
    }

    [Fact]
    public void BackupVhdx_AllZero_StaysSparse()
    {
        var zeroVol = Path.Combine(_dir, "zero.img");
        using (var f = new FileStream(zeroVol, FileMode.Create, FileAccess.Write))
            f.SetLength(8L * Mib);
        using var source = new FileBlockSource(zeroVol);
        var target = Path.Combine(_dir, "zero.vhdx");
        SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp);
        Assert.True(new FileInfo(target).Length < 8L * Mib, "VHDX length: " + new FileInfo(target).Length);
    }

    [Fact]
    public void BackupVhdx_SparseHoles_VerifiedSparse()
    {
        // 32 MiB logical, 1 MiB allocated: VHDX metadata has a ~4 MiB floor
        // on tiny volumes, so the volume must dwarf it for the size assert
        // to mean anything (per-range sparsity is asserted by the verify
        // pass itself via block extents, independent of volume size).
        var data = new byte[32 * Mib];
        new Random(7).NextBytes(data.AsSpan(0, Mib));
        var clusters = Mib / 4096;
        using var source = new SfbSparseSource(data, new List<(long, long)> { (0, clusters) });
        var target = Path.Combine(_dir, "sparse.vhdx");
        SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp);
        Assert.Equal(data, ReadAllContent(target, source.Length));
        Assert.True(new FileInfo(target).Length < 32L * Mib, "VHDX length: " + new FileInfo(target).Length);
    }

    [Fact]
    public void BackupVhdx_ExclusionParity_ZerosExcludedRanges()
    {
        var vol = SeedVolume(mib: 2); // MiB0 random, MiB1 zeros
        using var inner = new FileBlockSource(vol);
        using var source = new SfbExclusionSource(inner,
            new List<(long, long)> { (0, Mib / 4096) },
            4096, new[] { "pagefile.sys" });
        var target = Path.Combine(_dir, "excl.vhdx");
        var log = new TestLogger();
        SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, log, Stamp);
        var back = ReadAllContent(target, source.Length);
        Assert.All(back.AsSpan(0, Mib).ToArray(), b => Assert.Equal(0, b));
        Assert.Contains(log.Messages, m => m.Contains("pagefile.sys", StringComparison.Ordinal));
        // Same exclusion list reaches the WIM ini verbatim (all-three parity).
        Assert.Contains("pagefile.sys", WimScript.BuildIni(CapturePolicy.EffectiveSkip(new[] { "pagefile.sys" })), StringComparison.Ordinal);
    }

    [Fact]
    public void BackupVhdx_Collision_DoesNotOverwrite()
    {
        var vol = SeedVolume(mib: 1);
        var target = Path.Combine(_dir, "taken.vhdx");
        File.WriteAllText(target, "keep");
        using var source = new FileBlockSource(vol);
        var ex = Assert.Throws<SingleFileTargetExistsException>(() =>
            SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp));
        Assert.Contains(target, ex.Message);
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void BackupVhdx_TargetOnSource_RefusesBeforeWriting()
    {
        var vol = SeedVolume(mib: 1);
        using var source = new FileBlockSource(vol);
        var ex = Assert.Throws<SingleFileTargetOnSourceException>(() =>
            SingleFileBackup.BackupVhdx(source, "T:", "T:\\backups\\x.vhdx", VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp));
        Assert.Contains("T:", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "x.vhdx")));
    }

    [Fact]
    public void WimScript_BuildIni_HeaderPlusNames_StripsLogFile()
    {
        var ini = WimScript.BuildIni(CapturePolicy.EffectiveSkip(null));
        Assert.StartsWith("[ExclusionList]\r\n", ini, StringComparison.Ordinal);
        Assert.Contains("\r\npagefile.sys\r\n", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("$LogFile", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyVhdx_TamperedContent_FailsLoud()
    {
        var vol = SeedVolume(mib: 2);
        using var source = new FileBlockSource(vol);
        var target = Path.Combine(_dir, "tamper.vhdx");
        SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp);
        // Flip a byte in the first (random-data) extent through DiscUtils.
        using (var disk = new Disk(target, FileAccess.ReadWrite))
        {
            disk.Content.Seek(0, SeekOrigin.Begin);
            disk.Content.WriteByte((byte)(File.ReadAllBytes(vol)[0] ^ 0xFF));
        }
        using var source2 = new FileBlockSource(vol);
        var ex = Assert.Throws<SingleFileVerifyException>(() => SingleFileBackup.VerifyVhdx(
            target, source2,
            new List<(long, long)> { (0, Mib), (Mib, Mib) },
            new List<(long, long)>(), new TestLogger()));
        Assert.Contains(target, ex.Message);
    }

    private string SeedTree(string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "world-bytes");
        return root;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    [Fact]
    public void BackupWim_FakeRunner_CapturesThenVerifies()
    {
        var snap = SeedTree("snap");
        var dism = new SfbFakeDism();
        string? iniSeen = null;
        dism.Handler = args =>
        {
            if (args.Contains("/Capture-Image", StringComparison.Ordinal))
                iniSeen = File.ReadAllText(SfbFakeDism.ExtractQuoted(args, "/ConfigFile")!);
            if (args.Contains("/Mount-Image", StringComparison.Ordinal))
                CopyTree(snap, SfbFakeDism.ExtractQuoted(args, "/MountDir")!);
            return new DismResult(0, "", "");
        };
        var target = Path.Combine(_dir, "t.wim");
        File.WriteAllText(target + ".placeholder", "x"); // proves collision check targets the .wim, not strays
        var log = new TestLogger();
        var returned = SingleFileBackup.BackupWim(snap, "T:", target, new[] { "pagefile.sys" }, dism, log, Stamp);
        Assert.Equal(target, returned);
        Assert.StartsWith("/Capture-Image", dism.Calls[0], StringComparison.Ordinal);
        Assert.Contains("/Compress:max", dism.Calls[0], StringComparison.Ordinal);
        Assert.Contains("/CheckIntegrity", dism.Calls[0], StringComparison.Ordinal);
        Assert.Contains("/ConfigFile:", dism.Calls[0], StringComparison.Ordinal);
        Assert.Contains("pagefile.sys", iniSeen, StringComparison.Ordinal);
        Assert.Contains(dism.Calls, c => c.Contains("/Get-WimInfo", StringComparison.Ordinal));
        Assert.Contains(dism.Calls, c => c.Contains("/Mount-Image", StringComparison.Ordinal) && c.Contains("/ReadOnly", StringComparison.Ordinal));
        Assert.Contains(dism.Calls, c => c.Contains("/Unmount-Image", StringComparison.Ordinal) && c.Contains("/Discard", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("2 mount samples match", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupWim_SampleMismatch_ThrowsVerify_StillUnmounts()
    {
        var snap = SeedTree("snap2");
        var dism = new SfbFakeDism
        {
            Handler = args =>
            {
                if (args.Contains("/Mount-Image", StringComparison.Ordinal))
                {
                    var mount = SfbFakeDism.ExtractQuoted(args, "/MountDir")!;
                    CopyTree(snap, mount);
                    File.WriteAllText(Path.Combine(mount, "a.txt"), "tampered");
                }
                return new DismResult(0, "", "");
            }
        };
        var ex = Assert.Throws<SingleFileVerifyException>(() =>
            SingleFileBackup.BackupWim(snap, "T:", Path.Combine(_dir, "bad.wim"), null, dism, new TestLogger(), Stamp));
        Assert.Contains("a.txt", ex.Message);
        Assert.Contains(dism.Calls, c => c.Contains("/Unmount-Image", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupWim_DismFailure_ThrowsCarryingExitAndTarget()
    {
        var snap = SeedTree("snap3");
        var dism = new SfbFakeDism { Handler = _ => new DismResult(3, "", "dism exploded") };
        var target = Path.Combine(_dir, "fail.wim");
        var ex = Assert.Throws<SingleFileDismException>(() =>
            SingleFileBackup.BackupWim(snap, "T:", target, null, dism, new TestLogger(), Stamp));
        Assert.Equal(3, ex.ExitCode);
        Assert.Contains(target, ex.Message);
        Assert.Contains("dism exploded", ex.Message);
    }

    [Fact]
    public void BackupWim_MissingSnapshot_Refuses()
    {
        var missing = Path.Combine(_dir, "nope");
        var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
            SingleFileBackup.BackupWim(missing, "T:", Path.Combine(_dir, "x.wim"), null, new SfbFakeDism(), new TestLogger(), Stamp));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void BackupEsd_FakeRunner_CaptureVerifyExport_TempCleaned()
    {
        var snap = SeedTree("snap4");
        var dism = new SfbFakeDism();
        string? tempWim = null;
        dism.Handler = args =>
        {
            if (args.Contains("/Capture-Image", StringComparison.Ordinal))
                tempWim = SfbFakeDism.ExtractQuoted(args, "/ImageFile");
            if (args.Contains("/Mount-Image", StringComparison.Ordinal))
                CopyTree(snap, SfbFakeDism.ExtractQuoted(args, "/MountDir")!);
            return new DismResult(0, "", "");
        };
        var target = Path.Combine(_dir, "t.esd");
        SingleFileBackup.BackupEsd(snap, "T:", target, null, dism, new TestLogger(), Stamp);
        var export = Assert.Single(dism.Calls, c => c.Contains("/Export-Image", StringComparison.Ordinal));
        Assert.Contains("/Compress:recovery", export, StringComparison.Ordinal);
        Assert.Contains("/CheckIntegrity", export, StringComparison.Ordinal);
        Assert.NotNull(tempWim);
        Assert.False(File.Exists(tempWim), "temp WIM left behind: " + tempWim);
    }

    [Fact]
    public void CompressSidecar_RoundTrips_DeletesOriginal()
    {
        var vol = SeedVolume(mib: 3);
        var target = Path.Combine(_dir, "arc.vhdx");
        using (var source = new FileBlockSource(vol))
            SingleFileBackup.BackupVhdx(source, "T:", target, VhdxWriterKind.DiscUtils, null, new TestLogger(), Stamp);
        var before = File.ReadAllBytes(target);
        var zst = SingleFileBackup.CompressSidecar(target, new TestLogger());
        Assert.Equal(target + ".zst", zst);
        Assert.False(File.Exists(target));
        // In-process decode: every frame must reproduce the original bytes.
        using var input = new FileStream(zst, FileMode.Open, FileAccess.Read);
        using var z = new ZstdSharp.DecompressionStream(input, 128 * 1024, checkEndOfStream: true, leaveOpen: false);
        using var plain = new MemoryStream();
        z.CopyTo(plain);
        Assert.Equal(before, plain.ToArray());
    }

    [Fact]
    public void CompressSidecar_Collision_RefusesBeforeTouchingOriginal()
    {
        var target = Path.Combine(_dir, "keep.vhdx");
        File.WriteAllText(target, "image");
        File.WriteAllText(target + ".zst", "old");
        var ex = Assert.Throws<SingleFileTargetExistsException>(() => SingleFileBackup.CompressSidecar(target, new TestLogger()));
        Assert.Contains(target + ".zst", ex.Message);
        Assert.Equal("image", File.ReadAllText(target));
    }

    [Fact]
    public void VirtDiskCtor_ArgValidation_BeforePlatformCheck()
    {
        Assert.Throws<ArgumentNullException>(() => new VirtDiskExtentSink(null!, 1024));
        Assert.Throws<InvalidDataException>(() => new VirtDiskExtentSink(Path.Combine(_dir, "x.vhdx"), 0));
    }

    [Fact]
    public void VirtDiskCtor_OffWindows_ThrowsLoud()
    {
        if (OperatingSystem.IsWindows())
            return; // on-box A/B covers the real path (VM step, main agent)
        var ex = Assert.Throws<PlatformNotSupportedException>(() =>
            new VirtDiskExtentSink(Path.Combine(_dir, "v.vhdx"), Mib));
        Assert.Contains("Windows", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "v.vhdx")));
    }

    [Fact]
    public void DismRunner_OffWindows_ThrowsLoud()
    {
        if (OperatingSystem.IsWindows())
            return;
        Assert.Throws<PlatformNotSupportedException>(() => new DismRunner().Run("/Get-WimInfo /WimFile:x"));
    }

    [Theory]
    [InlineData(101089280L, 100663296L, 1048576)] // B9 offset: start of its 1 MiB chunk
    [InlineData(0L, 0L, 1048576)]
    [InlineData(1048575L, 0L, 1048576)]
    [InlineData(1048576L, 1048576L, 1048576)]
    public void RepairChunkFor_ChunkAligned(long offset, long expectedStart, int expectedLen)
    {
        var (start, len) = SingleFileBackup.RepairChunkFor(offset, 100L * 1024 * 1024 * 1024);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedLen, len);
    }

    [Fact]
    public void RepairChunkFor_TailChunk_Truncated()
    {
        var total = 100L * 1048576 + 123;
        var (start, len) = SingleFileBackup.RepairChunkFor(total - 1, total);
        Assert.Equal(100L * 1048576, start);
        Assert.Equal(123, len);
    }

    [Fact]
    public void VerifyException_CarriesMismatchOffset()
    {
        var ex = new SingleFileVerifyException("x.vhdx", "byte mismatch at offset 5", 5);
        Assert.Equal(5, ex.MismatchOffset);
        Assert.Equal(null, new SingleFileVerifyException("x.vhdx", "other").MismatchOffset);
    }

    [Fact]
    public void ResumeRanges_FromZero_KeepsAll()
    {
        var written = new List<(long Start, long Length)> { (0, 10), (10, 10), (20, 10) };
        Assert.Equal(written, SingleFileBackup.ResumeRanges(written, 0));
    }

    [Fact]
    public void ResumeRanges_SkipsVerified_TrimsStraddler()
    {
        var written = new List<(long Start, long Length)> { (0, 10), (10, 10), (20, 10) };
        Assert.Equal(new List<(long Start, long Length)> { (10, 10), (20, 10) },
            SingleFileBackup.ResumeRanges(written, 10));
        Assert.Equal(new List<(long Start, long Length)> { (15, 5), (20, 10) },
            SingleFileBackup.ResumeRanges(written, 15));
        Assert.Empty(SingleFileBackup.ResumeRanges(written, 30));
    }
}
