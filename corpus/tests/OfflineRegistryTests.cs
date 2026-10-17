namespace Corpus;

// Item 8 tests: offline reg load → add → unload through the
// FakeRegistryRunner (Linux-green). reg.exe arg shapes pinned so a VM run
// can diff them.
public sealed class OfflineRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _hive;

    public OfflineRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_reg_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _hive = Path.Combine(_dir, "SOFTWARE");
        File.WriteAllText(_hive, "fake hive");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Key = @"SOFTWARE\Corpus\Test";

    [Fact]
    public void SetValue_ArgShapes_LoadAddUnload()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner();
        Assert.Equal(0, OfflineRegistry.SetValue(_hive, Key, "TestValue", "REG_DWORD", "1", fake, log, false));
        Assert.Equal(
            new[]
            {
                $"reg load HKLM\\corpus-tmp \"{_hive}\"",
                $"reg add \"HKLM\\corpus-tmp\\{Key}\" /v TestValue /t REG_DWORD /d 1 /f",
                "reg unload HKLM\\corpus-tmp",
            }, fake.Calls);
    }

    [Fact]
    public void SetValue_MissingHive_Refuses()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner();
        Assert.Equal(1, OfflineRegistry.SetValue(Path.Combine(_dir, "nope"), Key, "V", "REG_SZ", "x", fake, log, false));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void SetValue_AddFails_UnloadsAnyway()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner
        {
            Handler = c => new RegistryResult(c, c.StartsWith("reg add", StringComparison.Ordinal) ? 1 : 0, "add failed")
        };
        Assert.Equal(1, OfflineRegistry.SetValue(_hive, Key, "V", "REG_SZ", "x", fake, log, false));
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("reg unload HKLM\\corpus-tmp", fake.Calls[2]);
    }

    [Fact]
    public void SetValue_LoadFails_NoAddNoUnload()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner
        {
            Handler = c => new RegistryResult(c, c.StartsWith("reg load", StringComparison.Ordinal) ? 5 : 0, "in use")
        };
        Assert.Equal(1, OfflineRegistry.SetValue(_hive, Key, "V", "REG_SZ", "x", fake, log, false));
        Assert.Single(fake.Calls);
        Assert.Contains("load", string.Join("\n", log.Messages));
    }

    [Fact]
    public void SetValue_RunnerThrowsOnAdd_UnloadsAnyway()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner
        {
            Handler = c => c.StartsWith("reg add", StringComparison.Ordinal)
                ? throw new InvalidDataException("fake transport failure")
                : new RegistryResult(c, 0, string.Empty)
        };
        Assert.Equal(1, OfflineRegistry.SetValue(_hive, Key, "V", "REG_SZ", "x", fake, log, false));
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("reg unload HKLM\\corpus-tmp", fake.Calls[2]);
    }

    [Fact]
    public void SetValue_DryRun_ZeroCalls()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner();
        Assert.Equal(0, OfflineRegistry.SetValue(_hive, Key, "V", "REG_SZ", "x", fake, log, true));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void SetValue_QuoteInData_Refuses()
    {
        var log = new TestLogger();
        var fake = new FakeRegistryRunner();
        Assert.Equal(1, OfflineRegistry.SetValue(_hive, Key, "V", "REG_SZ", "a\"b", fake, log, false));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void WindowsRunner_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        Assert.Throws<PlatformNotSupportedException>(() => new WindowsRegistryRunner().Run("reg query HKLM"));
    }
}
