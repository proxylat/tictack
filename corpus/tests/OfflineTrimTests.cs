namespace Corpus;

// Item 12.14 tests: footprint-trim ops as caller-supplied data + one
// executor. All drive the fake runner (Linux-green); the DISM/powercfg
// path is Windows-gated and on-box-unprobed — arg SHAPES below are from
// DISM training knowledge, pinned so a VM run can diff them.
public sealed class OfflineTrimTests : IDisposable
{
    private readonly string _dir;
    private readonly string _windows;

    public OfflineTrimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_trim_" + Guid.NewGuid());
        _windows = Path.Combine(_dir, "Windows");
        Directory.CreateDirectory(Path.Combine(_windows, "System32", "config"));
        File.WriteAllText(Path.Combine(_windows, "System32", "config", "SYSTEM"), "fake hive");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string ImageRoot(string windows) =>
        Path.GetDirectoryName(windows.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;

    [Fact]
    public void Guard_NoSystemHive_RefusesAndNamesPath()
    {
        var bare = Path.Combine(Path.GetTempPath(), "CorpusTest_trim_bare_" + Guid.NewGuid());
        Directory.CreateDirectory(bare);
        try
        {
            var log = new TestLogger();
            var fake = new FakeTrimRunner();
            var rc = OfflineTrim.Trim(
                new OfflineTrimOptions(Path.Combine(bare, "Windows"), ResetBase: true), fake, log);
            Assert.Equal(1, rc);
            Assert.Empty(fake.Calls);
            var text = string.Join("\n", log.Messages);
            Assert.Contains("SYSTEM", text);
            Assert.Contains(Path.Combine(bare, "Windows"), text);
        }
        finally
        {
            try { Directory.Delete(bare, true); } catch { }
        }
    }

    [Fact]
    public void ResetBase_ArgShape()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(_windows, ResetBase: true), fake, log);
        Assert.Equal(0, rc);
        var want = $"dism /Image:\"{ImageRoot(_windows)}\" /Cleanup-Image /ResetBase";
        Assert.Equal(new[] { want }, fake.Calls);
        Assert.Contains("exit 0", string.Join("\n", log.Messages));
    }

    [Fact]
    public void DisableFeatures_OneCommandPerCallerName()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(
            new OfflineTrimOptions(_windows, DisableFeatures: ["WorkFolders-Client", "FaxServicesClientPackage"]),
            fake, log);
        Assert.Equal(0, rc);
        var root = $"\"{ImageRoot(_windows)}\"";
        Assert.Equal(
            new[]
            {
                $"dism /Image:{root} /Disable-Feature /FeatureName:WorkFolders-Client /Remove",
                $"dism /Image:{root} /Disable-Feature /FeatureName:FaxServicesClientPackage /Remove",
            }, fake.Calls);
    }

    [Fact]
    public void RemovePackages_ArgShape()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(
            new OfflineTrimOptions(_windows, RemovePackages: ["Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~fr-FR~10.0.19041.1"]),
            fake, log);
        Assert.Equal(0, rc);
        Assert.Single(fake.Calls);
        Assert.Equal(
            $"dism /Image:\"{ImageRoot(_windows)}\" /Remove-Package /PackageName:Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~fr-FR~10.0.19041.1",
            fake.Calls[0]);
    }

    [Fact]
    public void DisableHibernation_ArgShape()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(_windows, DisableHibernation: true), fake, log);
        Assert.Equal(0, rc);
        Assert.Equal(new[] { "powercfg /h off" }, fake.Calls);
    }

    [Fact]
    public void AllOps_RunInOrder()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(
            _windows, ResetBase: true, DisableFeatures: ["F"], RemovePackages: ["P"], DisableHibernation: true),
            fake, log);
        Assert.Equal(0, rc);
        Assert.Equal(4, fake.Calls.Count);
        Assert.Contains("/ResetBase", fake.Calls[0]);
        Assert.Contains("/Disable-Feature", fake.Calls[1]);
        Assert.Contains("/Remove-Package", fake.Calls[2]);
        Assert.Equal("powercfg /h off", fake.Calls[3]);
    }

    [Fact]
    public void NoOps_NothingToDo()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineTrim.Trim(new OfflineTrimOptions(_windows), fake, log));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void NonZeroExit_FailsLoudWithCode()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, 2, "some dism complaint")
        };
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(_windows, ResetBase: true), fake, log);
        Assert.Equal(1, rc);
        Assert.Contains("exit 2", string.Join("\n", log.Messages));
    }

    [Fact]
    public void RunnerThrows_CountsAsFailure()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => throw new InvalidDataException("fake transport failure")
        };
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(_windows, ResetBase: true), fake, log);
        Assert.Equal(1, rc);
    }

    [Fact]
    public void DryRun_PlansOnly()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var rc = OfflineTrim.Trim(new OfflineTrimOptions(
            _windows, ResetBase: true, DisableFeatures: ["F"], RemovePackages: ["P"], DisableHibernation: true, DryRun: true),
            fake, log);
        Assert.Equal(0, rc);
        Assert.Empty(fake.Calls);
        Assert.Contains("dry run", string.Join("\n", log.Messages));
    }

    [Fact]
    public void WindowsRunner_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        Assert.Throws<PlatformNotSupportedException>(() => new WindowsTrimRunner().Run("dism /Get-WimInfo"));
    }

    [Fact]
    public void StageNoHibernate_CreatesScript()
    {
        var log = new TestLogger();
        var rc = OfflineTrim.StageNoHibernate(_windows, false, log);
        Assert.Equal(0, rc);
        var script = Path.Combine(_windows, "Setup", "Scripts", "SetupComplete.cmd");
        var text = File.ReadAllText(script);
        Assert.Contains("powercfg /h off", text);
    }

    [Fact]
    public void StageNoHibernate_IdempotentAndAppends()
    {
        var log = new TestLogger();
        var script = Path.Combine(_windows, "Setup", "Scripts", "SetupComplete.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "@echo off\r\necho oem\r\n");
        Assert.Equal(0, OfflineTrim.StageNoHibernate(_windows, false, log));
        Assert.Equal(0, OfflineTrim.StageNoHibernate(_windows, false, log));
        var text = File.ReadAllText(script);
        Assert.Contains("echo oem", text);
        Assert.Equal(1, text.Split(["powercfg /h off"], StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void StageNoHibernate_DryRun_WritesNothing()
    {
        var log = new TestLogger();
        Assert.Equal(0, OfflineTrim.StageNoHibernate(_windows, true, log));
        Assert.False(File.Exists(Path.Combine(_windows, "Setup", "Scripts", "SetupComplete.cmd")));
    }
}
