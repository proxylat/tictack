using System.Security.Cryptography;
using TicTack;
using Xunit.Sdk;

namespace Corpus;

// --optimize-wim: fake-runner arg-shape pins (DISM refusal shape is
// knowledge-sourced — DISM has no optimize verb — so the fake pins the
// refusal only) + a REAL wimlib E2E on Linux: capture -> append bloat ->
// delete (leaves a hole) -> optimize -> smaller + apply SHA-clean.

public sealed class WimOptimizeTests : IDisposable
{
    private readonly string _dir;

    public WimOptimizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimoptimize_" + Guid.NewGuid().ToString("N"));
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

    private string SeedWim(string name = "base.wim")
    {
        var wim = Path.Combine(_dir, name);
        File.WriteAllText(wim, "fake-wim-payload");
        return wim;
    }

    [Fact]
    public void Optimize_Wimlib_Plain_Shape_LogsSizes()
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        var log = new TestLogger();
        var (before, after) = WimOptimize.Optimize(wim, false, runner, log);
        Assert.Single(runner.Calls);
        var call = runner.Calls[0];
        Assert.StartsWith("optimize ", call, StringComparison.Ordinal);
        Assert.Contains("\"" + wim + "\"", call, StringComparison.Ordinal);
        Assert.DoesNotContain("--recompress", call, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(wim).Length, before);
        Assert.Equal(before, after);
        Assert.Contains(log.Messages, m => m.Contains(before.ToString(), StringComparison.Ordinal) && m.Contains("reclaimed", StringComparison.Ordinal));
    }

    [Fact]
    public void Optimize_Wimlib_Recompress_AppendsFlag()
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        WimOptimize.Optimize(wim, true, runner, new TestLogger());
        Assert.Single(runner.Calls);
        Assert.Contains("--recompress", runner.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Optimize_MissingFile_RefusesWithoutRunnerCall()
    {
        var missing = Path.Combine(_dir, "no.wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
            WimOptimize.Optimize(missing, false, runner, new TestLogger()));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Optimize_Dism_RefusesNamingWimlib()
    {
        // On-box-owed: needs Windows + real DISM; the fake pins the refusal only.
        var wim = SeedWim();
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<WimConvertRefusedException>(() =>
            WimOptimize.Optimize(wim, false, runner, new TestLogger()));
        Assert.Contains(wim, ex.Message);
        Assert.Contains("wimlib", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Optimize_Failure_MapsWimBackendException()
    {
        var wim = SeedWim();
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(1, "", "boom"),
        };
        var ex = Assert.Throws<WimBackendException>(() =>
            WimOptimize.Optimize(wim, true, runner, new TestLogger()));
        Assert.Contains(wim, ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Linux_Wimlib_CaptureBloatDelete_OptimizeReclaims_ApplyClean()
    {
        // Real-binary E2E (wimlib 1.14.5 observed): capture (2 images
        // with distinct incompressible payloads) -> soft-delete image 2
        // (plain `delete` rebuilds the archive itself and leaves no hole,
        // probed on-box; --soft only marks it unavailable, leaving the
        // hole) -> optimize -> smaller -> apply image 1 -> SHA compare.
        if (!OperatingSystem.IsLinux() || !File.Exists(WimlibRunner.DefaultBinaryPath))
            throw SkipException.ForSkip("wimlib E2E needs Linux + " + WimlibRunner.DefaultBinaryPath);
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello-optimize");
        var big = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
        var log = new TestLogger();
        var runner = new WimlibRunner();
        async Task Run(string args)
        {
            var (code, _, stderr, _) = await ProcessRunner.RunAsync(
                WimlibRunner.DefaultBinaryPath + " " + args, _dir, true, CancellationToken.None, Timeout.Infinite);
            Assert.True(code == 0, args + " failed: " + stderr);
        }
        var wim = Path.Combine(_dir, "base.wim");
        await Run($"capture {ProcessRunner.QuoteArg(src)} {ProcessRunner.QuoteArg(wim)} \"e2e\" --compress=none");
        var bloat = Path.Combine(_dir, "bloat");
        Directory.CreateDirectory(bloat);
        var big2 = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(big2); // distinct payload: unique streams, so deleting image 2 orphans real bytes
        File.WriteAllBytes(Path.Combine(bloat, "big2.bin"), big2);
        await Run($"append {ProcessRunner.QuoteArg(bloat)} {ProcessRunner.QuoteArg(wim)} \"bloat\" --compress=none");
        await Run($"delete {ProcessRunner.QuoteArg(wim)} 2 --soft");
        var (before, after) = WimOptimize.Optimize(wim, false, runner, log);
        Assert.True(after < before, $"want optimize to reclaim the soft-deleted image's hole: {before} -> {after}");
        Assert.Contains(log.Messages, m => m.Contains((before - after).ToString(), StringComparison.Ordinal));
        var applied = Path.Combine(_dir, "out");
        Directory.CreateDirectory(applied);
        await Run($"apply {ProcessRunner.QuoteArg(wim)} 1 {ProcessRunner.QuoteArg(applied)}");
        var (sampled, problems) = WimSample.Compare(applied, src, SingleFileBackup.MaxWimSamples);
        Assert.Equal(2, sampled);
        Assert.Empty(problems);
    }
}
