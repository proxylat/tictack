namespace Corpus;

// Item 6 tests: storage-first universal restore. All drive the fake injector
// (Linux-green); the DISM path is Windows-gated and VM-proven.
public sealed class UniversalRestoreRecoveryTests : IDisposable
{
    private readonly string _dir;

    public UniversalRestoreRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_universal_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DiskLayout Layout(string style) => new()
    {
        SchemaVersion = 1,
        Generation = 1,
        DiskNumber = 0,
        DiskGuid = Guid.NewGuid().ToString("D"),
        DiskSizeBytes = 32L * 1024 * 1024,
        SectorSizeBytes = 512,
        Style = style,
    };

    [Fact]
    public void InjectStorage_FirstExistingRungWins()
    {
        var missing = Path.Combine(_dir, "user-dir-missing");
        var removable = Path.Combine(_dir, "usb");
        var inbox = Path.Combine(_dir, "inbox");
        Directory.CreateDirectory(removable);
        Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(removable, "stor.inf"), "fake");
        var log = new TestLogger();
        var fake = new FakeDriverInjector();

        var rc = UniversalRestore.InjectStorageDrivers(
            new UniversalRestoreOptions([missing, removable, inbox], "D:\\Windows"),
            fake, log);

        Assert.Equal(0, rc);
        Assert.Single(fake.Calls);
        Assert.Equal("D:\\Windows", fake.Calls[0].Image);
        Assert.Equal(removable, fake.Calls[0].Driver);
    }

    [Fact]
    public void InjectStorage_NoRung_Refuses()
    {
        var log = new TestLogger();
        var fake = new FakeDriverInjector();

        var rc = UniversalRestore.InjectStorageDrivers(
            new UniversalRestoreOptions([Path.Combine(_dir, "nope1"), Path.Combine(_dir, "nope2")], "D:\\Windows"),
            fake, log);

        Assert.Equal(1, rc);
        Assert.Empty(fake.Calls);
        Assert.Contains("INACCESSIBLE_BOOT_DEVICE", string.Join("\n", log.Messages));
    }

    [Fact]
    public void InjectStorage_DryRun_PlansOnly()
    {
        var rung = Path.Combine(_dir, "drivers");
        Directory.CreateDirectory(rung);
        var log = new TestLogger();
        var fake = new FakeDriverInjector();

        var rc = UniversalRestore.InjectStorageDrivers(
            new UniversalRestoreOptions([rung], "D:\\Windows", DryRun: true),
            fake, log);

        Assert.Equal(0, rc);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("gpt", "UEFI", 0)]
    [InlineData("mbr", "Legacy", 0)]
    [InlineData("mbr", "BIOS", 0)]
    [InlineData("gpt", "Legacy", 1)]
    [InlineData("mbr", "UEFI", 1)]
    public void CheckBootMode_Matrix(string style, string firmware, int want)
    {
        var log = new TestLogger();
        Assert.Equal(want, UniversalRestore.CheckBootMode(Layout(style), firmware, log));
        if (want == 1)
            Assert.Contains("MBR2GPT", string.Join("\n", log.Messages));
    }

    [Fact]
    public void StageFirstBoot_BothStagesRun()
    {
        var nic = Path.Combine(_dir, "nic");
        var gpu = Path.Combine(_dir, "gpu");
        var image = Path.Combine(_dir, "img", "Windows");
        Directory.CreateDirectory(nic);
        Directory.CreateDirectory(gpu);
        Directory.CreateDirectory(image);
        File.WriteAllText(Path.Combine(nic, "net.inf"), "fake");
        File.WriteAllText(Path.Combine(gpu, "disp.inf"), "fake");
        var log = new TestLogger();
        var fake = new FakeFirstBootStager();

        var rc = UniversalRestore.StageFirstBootDrivers(
            new FirstBootOptions(nic, gpu, image), fake, log);

        Assert.Equal(0, rc);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("Nic", fake.Calls[0].Category);
        Assert.Equal("Gpu", fake.Calls[1].Category);
        Assert.True(File.Exists(Path.Combine(image, "Drivers", "Install-FirstBoot-Drivers.cmd")));
    }

    [Fact]
    public void StageFirstBoot_MissingDirs_SkipHonestly()
    {
        var log = new TestLogger();
        var fake = new FakeFirstBootStager();

        var rc = UniversalRestore.StageFirstBootDrivers(
            new FirstBootOptions(Path.Combine(_dir, "no-nic"), null, Path.Combine(_dir, "img")),
            fake, log);

        Assert.Equal(0, rc);
        Assert.Empty(fake.Calls);
        Assert.Contains("missing", string.Join("\n", log.Messages));
    }

    [Fact]
    public void StageFirstBoot_DryRun_PlansOnly()
    {
        var nic = Path.Combine(_dir, "nic");
        var image = Path.Combine(_dir, "img", "Windows");
        Directory.CreateDirectory(nic);
        var log = new TestLogger();
        var fake = new FakeFirstBootStager();

        var rc = UniversalRestore.StageFirstBootDrivers(
            new FirstBootOptions(nic, null, image, DryRun: true), fake, log);

        Assert.Equal(0, rc);
        Assert.Empty(fake.Calls);
        Assert.False(Directory.Exists(Path.Combine(image, "Drivers")));
    }

    [Fact]
    public void StageFirstBoot_RealCopy_Offline()
    {
        var nic = Path.Combine(_dir, "nic");
        var sub = Path.Combine(nic, "x64");
        var image = Path.Combine(_dir, "img", "Windows");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(image);
        File.WriteAllBytes(Path.Combine(sub, "net.inf"), new byte[] { 1, 2, 3 });
        var log = new TestLogger();

        var rc = UniversalRestore.StageFirstBootDrivers(
            new FirstBootOptions(nic, null, image), new FileFirstBootStager(), log);

        Assert.Equal(0, rc);
        Assert.Equal(new byte[] { 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(image, "Drivers", "Nic", "x64", "net.inf")));
    }

    private sealed class ThrowingStager : IFirstBootStager
    {
        public void Stage(string offlineWindowsDir, string driverDir, string category) =>
            throw new InvalidDataException("fake copy failure");
    }

    [Fact]
    public void StageFirstBoot_Failure_ReturnsOne()
    {
        var nic = Path.Combine(_dir, "nic");
        Directory.CreateDirectory(nic);
        var log = new TestLogger();

        var rc = UniversalRestore.StageFirstBootDrivers(
            new FirstBootOptions(nic, null, Path.Combine(_dir, "img")), new ThrowingStager(), log);

        Assert.Equal(1, rc);
    }

    [Fact]
    public void WindowsInjector_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        var injector = new WindowsDismDriverInjector(new TestLogger());
        Assert.Throws<PlatformNotSupportedException>(() => injector.Inject("D:\\Windows", _dir));
    }

    [Fact]
    public void ParseAddDriverResult_AllSuccess()
    {
        var (installed, failed) = WindowsDismDriverInjector.ParseAddDriverResult(
            "Installing 1 of 2 - a.inf: The driver package was successfully installed.\n" +
            "Installing 2 of 2 - b.inf: The driver package was successfully installed.\n");
        Assert.Equal(2, installed);
        Assert.Empty(failed);
    }

    [Fact]
    public void ParseAddDriverResult_Mixed_NamesFailures()
    {
        // Real shapes from an on-box /Recurse run (exit 50): 4 unsigned
        // boot-critical failures among hundreds of installs, plus a
        // continuation line and the footer — all must parse or skip clean.
        var (installed, failed) = WindowsDismDriverInjector.ParseAddDriverResult(
            "Installing 8 of 731 - xboxgipsynthetic.inf: The driver package was successfully installed.\n" +
            "Installing 187 of 731 - hidcfu.inf: Error - The driver package contains x64 boot-critical drivers, but the drivers are not properly signed.\n" +
            "Use the /forceunsigned option to install the drivers.\n" +
            "Installing 623 of 731 - storfwupdate.inf: Error - The driver package contains x64 boot-critical drivers, but the drivers are not properly signed.\n" +
            "Error: 50\n");
        Assert.Equal(1, installed);
        Assert.Equal(2, failed.Count);
        Assert.Equal("hidcfu.inf", failed[0].Inf);
        Assert.Contains("not properly signed", failed[0].Reason);
        Assert.Equal("storfwupdate.inf", failed[1].Inf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unrelated output\n")]
    public void ParseAddDriverResult_Unparseable_YieldsZero(string output)
    {
        var (installed, failed) = WindowsDismDriverInjector.ParseAddDriverResult(output);
        Assert.Equal(0, installed);
        Assert.Empty(failed);
    }
}
