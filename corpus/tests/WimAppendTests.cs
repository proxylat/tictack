namespace Corpus;

// §12.11 WIM-append tests. All DISM surface runs behind SfbFakeDism
// (SingleFileBackupTests.cs) — real DISM growth/index-count assertions need
// Windows + DISM and are a VM step (VM-TESTS §10). What fakes pin: arg
// shapes/sequence, ini reuse, index naming, and every refusal.
// NOTE (wiring): no csproj change — this file compiles by SDK default glob;
// corpus sources are already linked in Corpus.Tests.csproj.

public sealed class WimAppendTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 10, 16, 14, 30, 22);
    private readonly string _dir;

    public WimAppendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimappend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
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

    private static string WimInfo(int indexes, bool solid = false) =>
        string.Join("\n", Enumerable.Range(1, Math.Max(indexes, 0))
            .Select(i => $"Index : {i}\nName : gen-{i}\nCompression : {(solid ? "Solid" : "Max")}"));

    [Fact]
    public void BuildAppendImageName_Format()
    {
        Assert.Equal("gen-3-20261016-143022", SingleFileBackup.BuildAppendImageName(3, Stamp));
        Assert.Throws<ArgumentOutOfRangeException>(() => SingleFileBackup.BuildAppendImageName(-1, Stamp));
    }

    [Fact]
    public void CountImageIndexes_CountsIndexLines()
    {
        Assert.Equal(0, SingleFileBackup.CountImageIndexes("no images here"));
        Assert.Equal(1, SingleFileBackup.CountImageIndexes(WimInfo(1)));
        Assert.Equal(2, SingleFileBackup.CountImageIndexes(WimInfo(2)));
    }

    [Fact]
    public void IsSolidImageInfo_DetectsMarker()
    {
        Assert.True(SingleFileBackup.IsSolidImageInfo(WimInfo(1, solid: true)));
        Assert.False(SingleFileBackup.IsSolidImageInfo(WimInfo(2)));
    }

    [Fact]
    public void AppendWim_FakeRunner_AppendsWith121FlagsAndIni()
    {
        var snap = SeedTree("snap");
        var baseWim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(baseWim, "fake-wim");
        var dism = new SfbFakeDism();
        string? iniSeen = null;
        var infos = 0;
        dism.Handler = args =>
        {
            if (args.Contains("/Append-Image", StringComparison.Ordinal))
                iniSeen = File.ReadAllText(SfbFakeDism.ExtractQuoted(args, "/ConfigFile")!);
            if (args.Contains("/Get-WimInfo", StringComparison.Ordinal))
                return new DismResult(0, WimInfo(infos++ == 0 ? 1 : 2), "");
            if (args.Contains("/Mount-Image", StringComparison.Ordinal))
                CopyTree(snap, SfbFakeDism.ExtractQuoted(args, "/MountDir")!);
            return new DismResult(0, "", "");
        };
        var log = new TestLogger();
        var returned = SingleFileBackup.AppendWim(snap, baseWim, 3, new[] { "pagefile.sys" }, dism, log, Stamp);
        Assert.Equal(baseWim, returned);
        Assert.Equal(5, dism.Calls.Count);
        Assert.Contains("/Get-WimInfo", dism.Calls[0], StringComparison.Ordinal);
        var append = dism.Calls[1];
        Assert.StartsWith("/Append-Image", append, StringComparison.Ordinal);
        Assert.Contains("/CaptureDir:", append, StringComparison.Ordinal);
        Assert.Contains("/CheckIntegrity", append, StringComparison.Ordinal);
        Assert.Contains("/ConfigFile:", append, StringComparison.Ordinal);
        Assert.Contains("/Name:\"gen-3-20261016-143022\"", append, StringComparison.Ordinal);
        Assert.DoesNotContain("/Compress", append, StringComparison.Ordinal); // capture-only flag: append inherits the WIM's compression
        Assert.Contains("pagefile.sys", iniSeen, StringComparison.Ordinal); // same WimScript.ini exclusions as 12.1
        Assert.Contains("/Get-WimInfo", dism.Calls[2], StringComparison.Ordinal);
        Assert.Contains("/Mount-Image", dism.Calls[3], StringComparison.Ordinal);
        Assert.Contains("/Index:2", dism.Calls[3], StringComparison.Ordinal);
        Assert.Contains("/ReadOnly", dism.Calls[3], StringComparison.Ordinal);
        Assert.Contains("/Unmount-Image", dism.Calls[4], StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.Contains("index 2 (gen-3-20261016-143022)", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("2 mount samples match", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendWim_MissingSnapshot_Refuses()
    {
        var missing = Path.Combine(_dir, "nope");
        var baseWim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(baseWim, "fake-wim");
        var dism = new SfbFakeDism();
        var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
            SingleFileBackup.AppendWim(missing, baseWim, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(dism.Calls);
    }

    [Fact]
    public void AppendWim_MissingBase_RefusesNamingPath()
    {
        var snap = SeedTree("snap2");
        var missing = Path.Combine(_dir, "no-base.wim");
        var dism = new SfbFakeDism();
        var ex = Assert.Throws<SingleFileBaseNotFoundException>(() =>
            SingleFileBackup.AppendWim(snap, missing, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(dism.Calls);
    }

    [Fact]
    public void AppendWim_EsdExtension_RefusesWithoutDism()
    {
        var snap = SeedTree("snap3");
        var esd = Path.Combine(_dir, "base.esd");
        File.WriteAllText(esd, "fake-esd");
        var dism = new SfbFakeDism();
        var ex = Assert.Throws<SingleFileAppendRefusedException>(() =>
            SingleFileBackup.AppendWim(snap, esd, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains(esd, ex.Message);
        Assert.Contains("ESD", ex.Message, StringComparison.Ordinal);
        Assert.Empty(dism.Calls);
    }

    [Fact]
    public void AppendWim_SolidWimInfo_RefusesBeforeAppend()
    {
        var snap = SeedTree("snap4");
        var baseWim = Path.Combine(_dir, "solid.wim");
        File.WriteAllText(baseWim, "fake-wim");
        var dism = new SfbFakeDism
        {
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, WimInfo(1, solid: true), "")
                : new DismResult(0, "", ""),
        };
        var ex = Assert.Throws<SingleFileAppendRefusedException>(() =>
            SingleFileBackup.AppendWim(snap, baseWim, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains(baseWim, ex.Message);
        Assert.DoesNotContain(dism.Calls, c => c.Contains("/Append-Image", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendWim_IndexCountStuck_FailsVerify()
    {
        var snap = SeedTree("snap5");
        var baseWim = Path.Combine(_dir, "stuck.wim");
        File.WriteAllText(baseWim, "fake-wim");
        var dism = new SfbFakeDism
        {
            Handler = args =>
            {
                if (args.Contains("/Get-WimInfo", StringComparison.Ordinal))
                    return new DismResult(0, WimInfo(1), "");
                return new DismResult(0, "", "");
            },
        };
        var ex = Assert.Throws<SingleFileVerifyException>(() =>
            SingleFileBackup.AppendWim(snap, baseWim, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains(baseWim, ex.Message);
        Assert.DoesNotContain(dism.Calls, c => c.Contains("/Mount-Image", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendWim_SampleMismatch_ThrowsVerify_StillUnmounts()
    {
        var snap = SeedTree("snap6");
        var baseWim = Path.Combine(_dir, "bad.wim");
        File.WriteAllText(baseWim, "fake-wim");
        var dism = new SfbFakeDism();
        var infos = 0;
        dism.Handler = args =>
        {
            if (args.Contains("/Get-WimInfo", StringComparison.Ordinal))
                return new DismResult(0, WimInfo(infos++ == 0 ? 1 : 2), "");
            if (args.Contains("/Mount-Image", StringComparison.Ordinal))
            {
                var mount = SfbFakeDism.ExtractQuoted(args, "/MountDir")!;
                CopyTree(snap, mount);
                File.WriteAllText(Path.Combine(mount, "a.txt"), "tampered");
            }
            return new DismResult(0, "", "");
        };
        var ex = Assert.Throws<SingleFileVerifyException>(() =>
            SingleFileBackup.AppendWim(snap, baseWim, 1, null, dism, new TestLogger(), Stamp));
        Assert.Contains("a.txt", ex.Message);
        Assert.Contains(dism.Calls, c => c.Contains("/Unmount-Image", StringComparison.Ordinal));
    }
}
