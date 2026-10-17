namespace Corpus;

// --optimize-vhd tests. Fake runner pins the exact cmdlet shape; failure
// classification is pure (Linux-green). Compact itself refuses off-Windows
// first, so its runner paths are Windows-gated house-style (early return):
// they compile everywhere and prove on-box.
// NOTE (wiring): the test project links corpus sources explicitly — this
// file needs `<Compile Include="../OptimizeVhd.cs"
// Link="corpus/OptimizeVhd.cs" />` added to Corpus.Tests.csproj.

internal sealed class FakeOptimizeRunner : IOptimizeRunner
{
    public List<string> Calls { get; } = new();
    public Func<string, DismResult>? Handler;

    public DismResult Run(string arguments)
    {
        Calls.Add(arguments);
        return Handler != null ? Handler(arguments) : new DismResult(0, "", "");
    }
}

public sealed class OptimizeVhdTests : IDisposable
{
    private readonly string _dir;

    public OptimizeVhdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_optvhd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void BuildArguments_ExactShape_DefaultsFull()
    {
        Assert.Equal(
            "-NoProfile -NonInteractive -Command \"Optimize-VHD -Path 'C:\\back\\x.vhdx' -Mode Full\"",
            OptimizeVhd.BuildArguments("C:\\back\\x.vhdx"));
    }

    [Fact]
    public void BuildArguments_EscapesSingleQuote()
    {
        Assert.Contains("'o''clock.vhdx'", OptimizeVhd.BuildArguments("o'clock.vhdx"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildArguments_BlankPath_Refuses(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => OptimizeVhd.BuildArguments(path!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildArguments_BlankMode_Refuses(string? mode)
    {
        Assert.ThrowsAny<ArgumentException>(() => OptimizeVhd.BuildArguments("x.vhdx", mode!));
    }

    [Theory]
    [InlineData("Optimize-VHD : The term 'Optimize-VHD' is not recognized as the name of a cmdlet", true)]
    [InlineData("optimize-vhd failed: access denied", false)]
    [InlineData("", false)]
    public void IsModuleMissing_MatchesOnlyUnknownCmdlet(string output, bool expect)
    {
        Assert.Equal(expect, OptimizeVhd.IsModuleMissing(output));
    }

    [Fact]
    public void ThrowIfFailed_ExitZero_Passes()
    {
        OptimizeVhd.ThrowIfFailed("x.vhdx", "args", new DismResult(0, "", ""));
    }

    [Fact]
    public void ThrowIfFailed_NonZero_NamesPathAndStderr()
    {
        var target = Path.Combine(_dir, "x.vhdx");
        var ex = Assert.Throws<OptimizeVhdException>(() =>
            OptimizeVhd.ThrowIfFailed(target, "-Mode Full", new DismResult(1, "", "disk is locked")));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains(target, ex.Message);
        Assert.Contains("disk is locked", ex.Message);
    }

    [Fact]
    public void ThrowIfFailed_ModuleMissing_NamesModuleAndPath()
    {
        var target = Path.Combine(_dir, "x.vhdx");
        var ex = Assert.Throws<OptimizeVhdModuleException>(() =>
            OptimizeVhd.ThrowIfFailed(target, "-Mode Full",
                new DismResult(1, "", "Optimize-VHD : The term 'Optimize-VHD' is not recognized as the name of a cmdlet")));
        Assert.Contains("Hyper-V PowerShell module required", ex.Message);
        Assert.Contains(target, ex.Message);
    }

    [Fact]
    public void Compact_OffWindows_ThrowsLoudBeforeTouchingRunner()
    {
        if (OperatingSystem.IsWindows())
            return;
        var target = Path.Combine(_dir, "x.vhdx");
        var runner = new FakeOptimizeRunner();
        var ex = Assert.Throws<PlatformNotSupportedException>(() =>
            OptimizeVhd.Compact(target, runner, new TestLogger()));
        Assert.Contains(target, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void PowershellRunner_OffWindows_ThrowsLoud()
    {
        if (OperatingSystem.IsWindows())
            return;
        Assert.Throws<PlatformNotSupportedException>(() => new PowershellOptimizeRunner().Run("-Mode Full"));
    }

    [Fact]
    public void Compact_Success_ReturnsSizesAndLogsReclaimed()
    {
        if (!OperatingSystem.IsWindows())
            return; // on-box: Compact refuses off-Windows first (see above)
        var target = Path.Combine(_dir, "x.vhdx");
        File.WriteAllBytes(target, new byte[4096]);
        var runner = new FakeOptimizeRunner
        {
            // Simulate the compact reclaiming half the file.
            Handler = _ =>
            {
                using var f = new FileStream(target, FileMode.Open, FileAccess.Write);
                f.SetLength(2048);
                return new DismResult(0, "", "");
            }
        };
        var log = new TestLogger();
        var (before, after) = OptimizeVhd.Compact(target, runner, log);
        Assert.Equal(4096, before);
        Assert.Equal(2048, after);
        var call = Assert.Single(runner.Calls);
        Assert.Contains("Optimize-VHD", call, StringComparison.Ordinal);
        Assert.Contains("-Mode Full", call, StringComparison.Ordinal);
        Assert.Contains($"'{target}'", call, StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.Contains("4096", StringComparison.Ordinal)
            && m.Contains("2048", StringComparison.Ordinal)
            && m.Contains("reclaimed", StringComparison.Ordinal));
    }

    [Fact]
    public void Compact_RunnerFailure_ThrowsLoud()
    {
        if (!OperatingSystem.IsWindows())
            return; // on-box: Compact refuses off-Windows first (see above)
        var target = Path.Combine(_dir, "x.vhdx");
        File.WriteAllBytes(target, new byte[16]);
        var runner = new FakeOptimizeRunner { Handler = _ => new DismResult(2, "", "boom") };
        var ex = Assert.Throws<OptimizeVhdException>(() =>
            OptimizeVhd.Compact(target, runner, new TestLogger()));
        Assert.Contains(target, ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void ProductionRunner_LaunchesPowershell_Live()
    {
        if (!OperatingSystem.IsWindows())
            return; // proves the powershell.exe launch shape on-box only
        var r = new PowershellOptimizeRunner().Run("-NoProfile -NonInteractive -Command \"exit 0\"");
        Assert.Equal(0, r.ExitCode);
    }
}
