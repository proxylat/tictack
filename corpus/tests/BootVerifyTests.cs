namespace Corpus;

// Item 7 tests: boot-verify gate. All drive the fake Hyper-V runner
// (Linux-green); the real runner's Windows gate is pinned by the
// Linux-throws test, the on-box boot is a VM step (see lane summary).
public sealed class BootVerifyTests : IDisposable
{
    private readonly string _dir;

    public BootVerifyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_bootverify_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Verify_Fake_BootsAndHealthy()
    {
        var vhdx = Path.Combine(_dir, "snap.vhdx");
        File.WriteAllBytes(vhdx, new byte[] { 1, 2, 3 }); // stand-in: fake never parses it
        var log = new TestLogger();
        var fake = new FakeHyperVRunner();

        Assert.Equal(0, BootVerifyOrchestrator.Verify(new BootVerifyOptions(vhdx), fake, log));

        Assert.Equal(3, fake.Calls.Count);
        Assert.StartsWith("Boot:", fake.Calls[0], StringComparison.Ordinal);
        Assert.StartsWith("HealthCheck:", fake.Calls[1], StringComparison.Ordinal);
        Assert.StartsWith("Cleanup:", fake.Calls[2], StringComparison.Ordinal);
        Assert.Contains("healthy", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Verify_MissingVhdx_RefusesWithoutBooting()
    {
        var log = new TestLogger();
        var fake = new FakeHyperVRunner();

        Assert.Equal(1, BootVerifyOrchestrator.Verify(
            new BootVerifyOptions(Path.Combine(_dir, "absent.vhdx")), fake, log));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Verify_Unhealthy_Returns1_StillCleansUp()
    {
        var vhdx = Path.Combine(_dir, "snap.vhdx");
        File.WriteAllBytes(vhdx, new byte[] { 1 });
        var log = new TestLogger();
        var fake = new FakeHyperVRunner { FailHealth = true };

        Assert.Equal(1, BootVerifyOrchestrator.Verify(new BootVerifyOptions(vhdx), fake, log));
        Assert.Contains(fake.Calls, c => c.StartsWith("Cleanup:", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_BootFailure_Returns1()
    {
        var vhdx = Path.Combine(_dir, "snap.vhdx");
        File.WriteAllBytes(vhdx, new byte[] { 1 });
        var log = new TestLogger();
        var fake = new FakeHyperVRunner { FailBoot = true };

        Assert.Equal(1, BootVerifyOrchestrator.Verify(new BootVerifyOptions(vhdx), fake, log));
    }

    [Fact]
    public void RunScheduled_ExportThenVerify()
    {
        var vhdx = Path.Combine(_dir, "sched.vhdx");
        File.WriteAllBytes(vhdx, new byte[] { 7 });
        var log = new TestLogger();
        var fake = new FakeHyperVRunner();
        var exported = false;

        var rc = BootVerifyOrchestrator.RunScheduled(() => { exported = true; return vhdx; }, "nightly", fake, log);

        Assert.Equal(0, rc);
        Assert.True(exported);
        Assert.Contains("Boot:nightly", fake.Calls);
    }

    [Fact]
    public void RunScheduled_ExportFailure_Returns1WithoutBooting()
    {
        var log = new TestLogger();
        var fake = new FakeHyperVRunner();

        var rc = BootVerifyOrchestrator.RunScheduled(
            () => throw new InvalidDataException("fake export failure"), "nightly", fake, log);

        Assert.Equal(1, rc);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void WindowsRunner_Linux_ThrowsPlatform()
    {
        if (OperatingSystem.IsWindows())
            return; // On-box run is a VM step, not CI.
        var runner = new WindowsHyperVRunner(new TestLogger());
        Assert.Throws<PlatformNotSupportedException>(() => runner.Boot("x.vhdx", "vm"));
        Assert.Throws<PlatformNotSupportedException>(() => runner.HealthCheck("vm"));
        Assert.Throws<PlatformNotSupportedException>(() => runner.Cleanup("vm"));
    }

    [Fact]
    public void PollHealth_RetriesToHealthy_NoRealSleep()
    {
        var log = new TestLogger();
        var beats = new Queue<string>(new[] { "NoContact", "NoContact", "OkApplicationsHealthy" });
        var sleeps = new List<int>();

        var result = WindowsHyperVRunner.PollHealth(() => beats.Dequeue(), 300000, ms => sleeps.Add(ms), log);

        Assert.Equal("heartbeat OkApplicationsHealthy", result);
        Assert.Equal(new[] { 15000, 15000 }, sleeps);
        Assert.Empty(beats);
    }

    [Fact]
    public void PollHealth_QueryErrors_RetryNotFail()
    {
        var log = new TestLogger();
        var calls = 0;
        string Query()
        {
            calls++;
            if (calls < 3) throw new InvalidDataException("fake Get-VM failure");
            return "Ok";
        }

        var result = WindowsHyperVRunner.PollHealth(Query, 300000, _ => { }, log);

        Assert.Equal("heartbeat Ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void PollHealth_Deadline_ThrowsNamingLastBeat()
    {
        var log = new TestLogger();
        var ex = Assert.Throws<InvalidDataException>(() =>
            WindowsHyperVRunner.PollHealth(() => "NoContact", 0, _ => { }, log));
        Assert.Contains("NoContact", ex.Message);
    }

    [Fact]
    public void BuildBootCommand_SizesMemoryInBytes()
    {
        var cmd = WindowsHyperVRunner.BuildBootCommand("vm1", "D:\\snap.vhdx", 1024);
        Assert.Contains("-MemoryStartupBytes 1073741824", cmd);
        Assert.Contains("-VHDPath 'D:\\snap.vhdx'", cmd);
        Assert.Contains("-Name 'vm1'", cmd);
    }

    [Fact]
    public void BootVerifyOptions_Defaults()
    {
        var opts = new BootVerifyOptions("x.vhdx");
        Assert.Equal("corpus-bootverify", opts.VmName);
        Assert.Equal(300, opts.TimeoutSeconds);
        Assert.Equal(2048, opts.StartupMemoryMb);
    }

    [Theory]
    [InlineData("OkApplicationsHealthy\r\n", "OkApplicationsHealthy")]
    [InlineData("NoContact\r\nNoContact\r\n", "NoContact")]
    [InlineData("\r\n  Ok  \r\n", "Ok")]
    [InlineData("", "")]
    public void FirstBeatLine_TakesFirstNonEmptyLine(string stdout, string expected)
    {
        Assert.Equal(expected, WindowsHyperVRunner.FirstBeatLine(stdout));
    }
}
