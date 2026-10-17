using System.Security.Cryptography;
using TicTack;
using Xunit.Sdk;

namespace Corpus;

// Install-without-USB apply: fake-runner pins for both backends' arg
// shapes (DISM shapes are knowledge-sourced — on-box Windows probe owed;
// wimlib `apply WIMFILE [IMAGE] TARGET` verified against wimlib-imagex
// 1.14.5 on this box) + a real wimlib Linux E2E (capture -> Apply ->
// byte compare). The live-OS refusal is Windows-only, so its string
// logic is pinned directly via GuardLiveOs (runnable anywhere).

public sealed class WimApplyTests : IDisposable
{
    private readonly string _dir;

    public WimApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimapply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeWimRunner : IWimRunner
    {
        public WimBackendKind Backend { get; init; }
        public List<string> Calls { get; } = new();
        public Func<string, DismResult>? Handler;

        public DismResult Run(string arguments)
        {
            Calls.Add(arguments);
            return Handler != null ? Handler(arguments) : new DismResult(0, "", "");
        }
    }

    private sealed class FakeBoot : IWimApplyBootRunner
    {
        public List<string> Calls { get; } = new();
        public bool Throw;

        public void Install(string windowsDir)
        {
            Calls.Add("Install:" + windowsDir);
            if (Throw)
                throw new WimApplyBootException(windowsDir, 1, "fake bcdboot failure");
        }
    }

    private static string DismInfo(int indexes) =>
        string.Join("\n", Enumerable.Range(1, Math.Max(indexes, 0))
            .Select(i => $"Index : {i}\nName : gen-{i}"));

    private static string WimlibInfo(int indexes) =>
        "WIM Information:\n----------------\nImage Count:    " + indexes + "\n\nAvailable Images:\n-----------------\n"
        + string.Join("", Enumerable.Range(1, Math.Max(indexes, 0)).Select(i => $"Index:                  {i}\nName:                   img-{i}\n"));

    private string SeedWim(string name = "base.wim")
    {
        var wim = Path.Combine(_dir, name);
        File.WriteAllText(wim, "fake-wim");
        return wim;
    }

    [Fact]
    public void Apply_Wimlib_Shape_ThenBcdboot_Order()
    {
        var wim = SeedWim();
        var target = Path.Combine(_dir, "t");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        var boot = new FakeBoot();
        var log = new TestLogger();
        WimApply.Apply(wim, null, target, runner, log, boot);
        Assert.Equal(2, runner.Calls.Count);
        Assert.StartsWith("info ", runner.Calls[0], StringComparison.Ordinal);
        var apply = runner.Calls[1];
        Assert.StartsWith("apply ", apply, StringComparison.Ordinal);
        Assert.Contains("\"" + wim + "\"", apply, StringComparison.Ordinal);
        Assert.Contains(" 1 ", apply, StringComparison.Ordinal);
        Assert.Contains("\"" + target + "\"", apply, StringComparison.Ordinal);
        Assert.Single(boot.Calls);
        Assert.Equal("Install:" + Path.Combine(target, "Windows"), boot.Calls[0]);
        Assert.Contains(log.Messages, m => m.Contains("defaults to index 1", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.Contains("bootloader installed", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_Dism_ApplyImageShape()
    {
        // On-box-owed: needs Windows + real DISM; the fake pins shape only.
        var wim = SeedWim();
        var target = Path.Combine(_dir, "t");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(1), "")
                : new DismResult(0, "", ""),
        };
        WimApply.Apply(wim, null, target, runner, new TestLogger(), new FakeBoot());
        Assert.Equal(2, runner.Calls.Count);
        var apply = runner.Calls[1];
        Assert.StartsWith("/Apply-Image", apply, StringComparison.Ordinal);
        Assert.Contains("/ImageFile:", apply, StringComparison.Ordinal);
        Assert.Contains("/Index:1", apply, StringComparison.Ordinal);
        Assert.Contains("/ApplyDir:", apply, StringComparison.Ordinal);
        Assert.Contains("/CheckIntegrity", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MultiIndex_NullIndex_RefusesNamingCount()
    {
        var wim = SeedWim();
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner
            {
                Backend = backend,
                Handler = args => backend == WimBackendKind.Wimlib && args.StartsWith("info ", StringComparison.Ordinal)
                    || backend == WimBackendKind.Dism && args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                    ? new DismResult(0, backend == WimBackendKind.Wimlib ? WimlibInfo(3) : DismInfo(3), "")
                    : new DismResult(0, "", ""),
            };
            var boot = new FakeBoot();
            var ex = Assert.Throws<WimApplyRefusedException>(() =>
                WimApply.Apply(wim, null, Path.Combine(_dir, "t"), runner, new TestLogger(), boot));
            Assert.Contains("3", ex.Message, StringComparison.Ordinal);
            Assert.Single(runner.Calls); // probe only, no apply
            Assert.Empty(boot.Calls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Apply_IndexOutOfRange_RefusesBeforeApply(int index)
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(2), "")
                : new DismResult(0, "", ""),
        };
        var boot = new FakeBoot();
        var ex = Assert.Throws<WimApplyRefusedException>(() =>
            WimApply.Apply(wim, index, Path.Combine(_dir, "t"), runner, new TestLogger(), boot));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Single(runner.Calls);
        Assert.Empty(boot.Calls);
    }

    [Fact]
    public void Apply_MissingWim_RefusesWithZeroCalls()
    {
        var missing = Path.Combine(_dir, "no.wim");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var boot = new FakeBoot();
            var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
                WimApply.Apply(missing, null, Path.Combine(_dir, "t"), runner, new TestLogger(), boot));
            Assert.Contains(missing, ex.Message);
            Assert.Empty(runner.Calls);
            Assert.Empty(boot.Calls);
        }
    }

    [Fact]
    public void GuardLiveOs_SameRootRefuses_CaseInsensitive()
    {
        var ex = Assert.Throws<WimApplyRefusedException>(() =>
            WimApply.GuardLiveOs(@"c:\target", @"C:\Windows\System32", true));
        Assert.Contains("booted OS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardLiveOs_OtherRootPasses()
    {
        WimApply.GuardLiveOs(@"D:\target", @"C:\Windows\System32", true);
    }

    [Fact]
    public void GuardLiveOs_OffWindows_NoOp()
    {
        // wimlib dir-applies are the Linux norm; nothing determinable to protect.
        WimApply.GuardLiveOs("/mnt/target", "", false);
    }

    [Fact]
    public void Apply_Failure_MapsBackendException_NoBootCall()
    {
        var wim = SeedWim();
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(1), "")
                : new DismResult(1, "", "boom"),
        };
        var dboot = new FakeBoot();
        var dex = Assert.Throws<SingleFileDismException>(() =>
            WimApply.Apply(wim, null, Path.Combine(_dir, "t"), dism, new TestLogger(), dboot));
        Assert.Contains(wim, dex.Message);
        Assert.Empty(dboot.Calls);
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(1, "", "boom"),
        };
        var wboot = new FakeBoot();
        var wex = Assert.Throws<WimBackendException>(() =>
            WimApply.Apply(wim, null, Path.Combine(_dir, "t2"), wimlib, new TestLogger(), wboot));
        Assert.Contains(wim, wex.Message);
        Assert.Contains("boom", wex.Message);
        Assert.Empty(wboot.Calls);
    }

    [Fact]
    public void Apply_BootFailure_NamesWindowsDir()
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        var boot = new FakeBoot { Throw = true };
        var target = Path.Combine(_dir, "t");
        var ex = Assert.Throws<WimApplyBootException>(() =>
            WimApply.Apply(wim, null, target, runner, new TestLogger(), boot));
        Assert.Contains(Path.Combine(target, "Windows"), ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Calls.Count); // apply greened before bcdboot failed
    }

    [Fact]
    public void Apply_NullBoot_SkipsBcdbootWithInfo()
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        var log = new TestLogger();
        WimApply.Apply(wim, null, Path.Combine(_dir, "t"), runner, log, null);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains(log.Messages, m => m.Contains("skipping bcdboot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linux_Wimlib_CaptureApply_RoundTrip()
    {
        // Real-binary E2E (wimlib 1.14.5 observed): capture -> WimApply.Apply
        // (5-arg: bcdboot skips off Windows) -> byte compare clean.
        if (!OperatingSystem.IsLinux() || !File.Exists(WimlibRunner.DefaultBinaryPath))
            throw SkipException.ForSkip("wimlib E2E needs Linux + " + WimlibRunner.DefaultBinaryPath);
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello-apply");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "world-apply");
        var big = new byte[256 * 1024];
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
        var wim = Path.Combine(_dir, "base.wim");
        var (code, _, stderr, _) = await ProcessRunner.RunAsync(
            WimlibRunner.DefaultBinaryPath + $" capture {ProcessRunner.QuoteArg(src)} {ProcessRunner.QuoteArg(wim)} \"e2e\" --compress=none",
            _dir, true, CancellationToken.None, Timeout.Infinite);
        Assert.True(code == 0, "capture failed: " + stderr);
        var log = new TestLogger();
        var target = Path.Combine(_dir, "applied"); // absent: Apply creates it
        WimApply.Apply(wim, null, target, new WimlibRunner(), log);
        Assert.Equal("hello-apply", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("world-apply", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(target, "big.bin")));
        Assert.Contains(log.Messages, m => m.Contains("skipping bcdboot", StringComparison.Ordinal));
    }
}
