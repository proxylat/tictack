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
    public void StagedFollowUps_ListsNicAndGpu()
    {
        Assert.Contains(UniversalRestore.StagedFollowUps, s => s.Contains("NIC"));
        Assert.Contains(UniversalRestore.StagedFollowUps, s => s.Contains("GPU"));
    }

    [Fact]
    public void WindowsInjector_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        var injector = new WindowsDismDriverInjector(new TestLogger());
        Assert.Throws<PlatformNotSupportedException>(() => injector.Inject("D:\\Windows", _dir));
    }
}
