namespace Corpus;

// Item 15a tests: winre.wim locate logic. All drive the fake probe
// (Linux-green); the reagentc runner is Windows-gated and VM-proven.
public sealed class WinReLocateTests : IDisposable
{
    private readonly string _dir;

    public WinReLocateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_winre_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private FakeWinReProbe Probe(string? reagentc, params string[] drives) =>
        new() { ReagentcOutput = reagentc, SystemRootValue = Path.Combine(_dir, "Windows"), DriveRootsValue = drives };

    [Fact]
    public void ParseReagentc_ExtractsLocation()
    {
        const string output = "Windows Recovery Environment (Windows RE) and system reset configuration\n"
            + "Information:\n"
            + "    Windows RE status:         Enabled\n"
            + "    Windows RE location:       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\n"
            + "REAGENTC.EXE: Operation Successful.\n";
        Assert.Equal(@"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE",
            WinReLocate.ParseReagentcLocation(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Windows RE status:         Disabled\n    Windows RE location:       \n")]
    [InlineData("something unrelated\n")]
    public void ParseReagentc_MissingOrDisabled_ReturnsNull(string? output)
    {
        Assert.Null(WinReLocate.ParseReagentcLocation(output));
    }

    [Fact]
    public void CandidatePaths_OrderAndNoAssumption()
    {
        // A drive-letter reagentc path leads; the inbox copy follows; every
        // drive root gets a scan entry — and no bare C:\Recovery guess.
        var cands = WinReLocate.CandidatePaths(@"D:\Recovery\WindowsRE",
            Path.Combine(_dir, "Windows"), new[] { Path.Combine(_dir, "c"), Path.Combine(_dir, "d") });
        Assert.Equal(4, cands.Length);
        Assert.Equal("reagentc", cands[0].Source);
        Assert.Equal("known-path", cands[1].Source);
        Assert.All(cands[2..], c => Assert.Equal("partition-scan", c.Source));
        Assert.DoesNotContain(cands, c => c.Path == @"C:\Recovery\WindowsRE\winre.wim");
    }

    [Fact]
    public void CandidatePaths_GlobalRoot_ProbedFirst()
    {
        // VM-proven 2026-10-15 (Win11 26200, elevated): GLOBALROOT recovery
        // paths are user-mode readable, so they lead as reagentc candidates.
        var cands = WinReLocate.CandidatePaths(@"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE",
            Path.Combine(_dir, "Windows"), [Path.Combine(_dir, "c")]);
        Assert.Equal(3, cands.Length); // reagentc + known-path + one scan entry
        Assert.Equal("reagentc", cands[0].Source);
        Assert.EndsWith("winre.wim", cands[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_FindsViaReagentc()
    {
        var rec = Path.Combine(_dir, "rec", "WindowsRE");
        Directory.CreateDirectory(rec);
        var wim = Path.Combine(rec, "winre.wim");
        File.WriteAllBytes(wim, new byte[] { 1 });
        var probe = Probe("    Windows RE location:       " + Path.Combine(_dir, "rec", "WindowsRE") + "\n",
            Path.Combine(_dir, "c"));
        // Fake compares exact candidate strings: register the real file.
        probe.ExistingFiles.Add(wim);
        var log = new TestLogger();

        var found = WinReLocate.Locate(probe, log);

        Assert.NotNull(found);
        Assert.Equal("reagentc", found.Source);
    }

    [Fact]
    public void Locate_FindsViaKnownPath()
    {
        var inbox = Path.Combine(_dir, "Windows", "System32", "Recovery", "winre.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(inbox)!);
        File.WriteAllBytes(inbox, new byte[] { 2 });
        var probe = Probe("    Windows RE status:         Disabled\n    Windows RE location:       \n",
            Path.Combine(_dir, "c"));
        probe.ExistingFiles.Add(inbox);

        var found = WinReLocate.Locate(probe, new TestLogger());

        Assert.NotNull(found);
        Assert.Equal("known-path", found.Source);
    }

    [Fact]
    public void Locate_FindsViaPartitionScan()
    {
        var scan = Path.Combine(_dir, "d", "Recovery", "WindowsRE", "winre.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(scan)!);
        File.WriteAllBytes(scan, new byte[] { 3 });
        var probe = Probe(null, Path.Combine(_dir, "c"), Path.Combine(_dir, "d"));
        probe.ExistingFiles.Add(scan);

        var found = WinReLocate.Locate(probe, new TestLogger());

        Assert.NotNull(found);
        Assert.Equal("partition-scan", found.Source);
    }

    [Fact]
    public void Locate_Missing_ReturnsNullAndNamesAdk()
    {
        var probe = Probe(null, Path.Combine(_dir, "c"));
        var log = new TestLogger();

        Assert.Null(WinReLocate.Locate(probe, log));
        Assert.Contains("ADK", string.Join("\n", log.Messages));
    }

    [Fact]
    public void WindowsProbe_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        var probe = new WindowsWinReProbe();
        Assert.Throws<PlatformNotSupportedException>(() => probe.ReadReagentcOutput());
    }
}
