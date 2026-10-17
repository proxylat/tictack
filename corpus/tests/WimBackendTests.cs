using System.Security.Cryptography;
using TicTack;
using Xunit.Sdk;

namespace Corpus;

// §12.14 WIM-backend + SWM-split tests. All backend surface runs behind
// FakeWimRunner — real-DISM arg shapes and the on-box split-of-solid probe
// need Windows + DISM and are VM steps (on-box-unprobed, marked below).
// The Linux E2E at the bottom runs the REAL /usr/bin/wimlib-imagex:
// capture (file-level; VSS snapshots don't exist on Linux) -> SplitSwm ->
// first-part info -> join -> apply -> SHA compare.

public sealed class WimBackendTests : IDisposable
{
    private readonly string _dir;

    public WimBackendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimbackend_" + Guid.NewGuid().ToString("N"));
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

    private static string DismInfo(int indexes, bool solid = false) =>
        string.Join("\n", Enumerable.Range(1, Math.Max(indexes, 0))
            .Select(i => $"Index : {i}\nName : gen-{i}\nCompression : {(solid ? "Solid" : "Max")}"));

    private static string WimlibInfo(int indexes) =>
        "WIM Information:\n----------------\nImage Count:    " + indexes + "\nCompression:    XPRESS\nPart Number:    1/1\nBoot Index:     0\n\nAvailable Images:\n-----------------\n"
        + string.Join("", Enumerable.Range(1, Math.Max(indexes, 0)).Select(i => $"Index:                  {i}\nName:                   img-{i}\n"));

    [Fact]
    public void CountWimlibIndexes_AcceptsBothShapes()
    {
        Assert.Equal(0, WimBackend.CountWimlibIndexes(""));
        Assert.Equal(1, WimBackend.CountWimlibIndexes(WimlibInfo(1)));
        Assert.Equal(2, WimBackend.CountWimlibIndexes(WimlibInfo(2))); // Boot Index: line above must not count
        Assert.Equal(1, WimBackend.CountWimlibIndexes(DismInfo(1))); // DISM-shaped fixtures still parse
        // The existing DISM heuristic does NOT match wimlib output (colon-tight
        // "Index:" vs colon-spaced "Index :") — probed, hence this parser.
        Assert.Equal(0, SingleFileBackup.CountImageIndexes(WimlibInfo(2)));
    }

    [Fact]
    public void DismWimRunner_Passthrough()
    {
        var inner = new SfbFakeDism { Handler = _ => new DismResult(0, "ok", "") };
        var runner = new DismWimRunner(inner);
        Assert.Equal(WimBackendKind.Dism, runner.Backend);
        var r = runner.Run("/Get-WimInfo /WimFile:\"x.wim\"");
        Assert.Equal("ok", r.Stdout);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public void WimlibRunner_MissingBinary_RefusesNamingPath()
    {
        var missing = Path.Combine(_dir, "nope", "wimlib-imagex");
        var runner = new WimlibRunner(missing);
        Assert.Equal(WimBackendKind.Wimlib, runner.Backend);
        var ex = Assert.Throws<WimSplitRefusedException>(() => runner.Run("info \"x.wim\""));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void SplitSwm_Dism_FakeRunner_SplitShapeAndFirstPartProof()
    {
        // On-box-unprobed: needs Windows + real DISM (VM step). The fake pins
        // the arg shape + call sequence only.
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var swm = Path.Combine(_dir, "base.swm");
        File.WriteAllText(swm, "fake-part");
        var infos = 0;
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(infos++ == 0 ? 1 : 1), "")
                : new DismResult(0, "", ""),
        };
        var log = new TestLogger();
        var returned = WimBackend.SplitSwm(wim, swm, 3800, runner, log);
        Assert.Equal(swm, returned);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Contains("/Get-WimInfo", runner.Calls[0], StringComparison.Ordinal); // source probe: solid gate + image count
        var split = runner.Calls[1];
        Assert.StartsWith("/Split-Image", split, StringComparison.Ordinal);
        Assert.Contains("/ImageFile:", split, StringComparison.Ordinal);
        Assert.Contains("/SWMFile:", split, StringComparison.Ordinal);
        Assert.Contains("/FileSize:3800", split, StringComparison.Ordinal);
        Assert.Contains("/Get-WimInfo", runner.Calls[2], StringComparison.Ordinal); // per-part equality proof
        Assert.Contains(swm, runner.Calls[2], StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.Contains("split", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitSwm_Wimlib_FakeRunner_SplitShapeAndFirstPartProof()
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var swm = Path.Combine(_dir, "base.swm");
        File.WriteAllText(swm, "fake-part");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        var returned = WimBackend.SplitSwm(wim, swm, 100, runner, new TestLogger());
        Assert.Equal(swm, returned);
        Assert.Equal(3, runner.Calls.Count); // source info + split + per-part info (no solid probe on wimlib)
        Assert.StartsWith("info ", runner.Calls[0], StringComparison.Ordinal); // source count first
        Assert.Contains(wim, runner.Calls[0], StringComparison.Ordinal);
        var split = runner.Calls[1];
        Assert.StartsWith("split ", split, StringComparison.Ordinal);
        Assert.Contains("\"" + wim + "\"", split, StringComparison.Ordinal);
        Assert.Contains("\"" + swm + "\"", split, StringComparison.Ordinal);
        Assert.EndsWith(" 100", split, StringComparison.Ordinal);
        Assert.StartsWith("info ", runner.Calls[2], StringComparison.Ordinal);
        Assert.Contains(swm, runner.Calls[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitSwm_BareTarget_RefusesWithoutRunnerCall(string? target)
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<SingleFileTargetMissingException>(() =>
            WimBackend.SplitSwm(wim, target!, 100, runner, new TestLogger()));
        Assert.Contains("--split-swm", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SplitSwm_BadSize_RefusesWithoutRunnerCall(int size)
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WimBackend.SplitSwm(wim, Path.Combine(_dir, "b.swm"), size, runner, new TestLogger()));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void SplitSwm_MissingInput_RefusesNamingPath()
    {
        var missing = Path.Combine(_dir, "no.wim");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
                WimBackend.SplitSwm(missing, Path.Combine(_dir, "b.swm"), 100, runner, new TestLogger()));
            Assert.Contains(missing, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void SplitSwm_EsdInput_RefusesWithoutRunnerCall()
    {
        var esd = Path.Combine(_dir, "base.esd");
        File.WriteAllText(esd, "fake-esd");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<WimSplitRefusedException>(() =>
                WimBackend.SplitSwm(esd, Path.Combine(_dir, "b.swm"), 100, runner, new TestLogger()));
            Assert.Contains(esd, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void SplitSwm_Dism_SolidProbe_RefusesBeforeSplit()
    {
        // On-box-unprobed: the locked default stands until the Windows DISM
        // split-of-solid probe lands (same posture as --append).
        var wim = Path.Combine(_dir, "solid.wim");
        File.WriteAllText(wim, "fake-wim");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(1, solid: true), "")
                : new DismResult(0, "", ""),
        };
        var ex = Assert.Throws<WimSplitRefusedException>(() =>
            WimBackend.SplitSwm(wim, Path.Combine(_dir, "b.swm"), 100, runner, new TestLogger()));
        Assert.Contains(wim, ex.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("/Split-Image", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitSwm_SplitFails_ThrowsNamingPath()
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Split-Image", StringComparison.Ordinal)
                ? new DismResult(1, "", "boom")
                : new DismResult(0, DismInfo(1), ""),
        };
        var dex = Assert.Throws<SingleFileDismException>(() =>
            WimBackend.SplitSwm(wim, Path.Combine(_dir, "b.swm"), 100, dism, new TestLogger()));
        Assert.Contains(wim, dex.Message);
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("split ", StringComparison.Ordinal)
                ? new DismResult(1, "", "boom")
                : new DismResult(0, WimlibInfo(1), ""),
        };
        var wex = Assert.Throws<WimBackendException>(() =>
            WimBackend.SplitSwm(wim, Path.Combine(_dir, "c.swm"), 100, wimlib, new TestLogger()));
        Assert.Contains(wim, wex.Message);
        Assert.Contains("boom", wex.Message);
    }

    [Fact]
    public void SplitSwm_FirstPartUnreadable_FailsVerify()
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(0), "")
                : new DismResult(0, "", ""),
        };
        var swm = Path.Combine(_dir, "b.swm");
        var ex = Assert.Throws<SingleFileVerifyException>(() =>
            WimBackend.SplitSwm(wim, swm, 100, runner, new TestLogger()));
        Assert.Contains(swm, ex.Message);
    }

    [Fact]
    public void SplitSwm_PartCountMismatch_FailsAndCleansParts()
    {
        // Source lists 2 images, the emitted part lists 1: incomplete set —
        // fail loud and leave no parts behind.
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var swm = Path.Combine(_dir, "base.swm");
        var swm2 = Path.Combine(_dir, "base2.swm");
        File.WriteAllText(swm, "fake-part");
        File.WriteAllText(swm2, "fake-part");
        var calls = 0;
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args =>
            {
                if (!args.StartsWith("info ", StringComparison.Ordinal))
                    return new DismResult(0, "", "");
                calls++;
                return new DismResult(0, WimlibInfo(calls == 1 ? 2 : 1), "");
            },
        };
        var ex = Assert.Throws<SingleFileVerifyException>(() =>
            WimBackend.SplitSwm(wim, swm, 100, runner, new TestLogger()));
        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(swm), "failed split must not leave the first part");
        Assert.False(File.Exists(swm2), "failed split must not leave part 2");
    }

    [Fact]
    public void SplitSwm_SplitFails_DeletesFreshPartsKeepsStale()
    {
        // Fresh part (this run) is deleted; a stale same-stem file from an
        // older run is somebody else's — left alone and named in the log.
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var swm = Path.Combine(_dir, "base.swm");
        var stale = Path.Combine(_dir, "base2.swm");
        File.WriteAllText(swm, "fake-part");
        File.WriteAllText(stale, "older-run");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("split ", StringComparison.Ordinal)
                ? new DismResult(1, "", "boom")
                : new DismResult(0, WimlibInfo(1), ""),
        };
        var log = new TestLogger();
        Assert.Throws<WimBackendException>(() =>
            WimBackend.SplitSwm(wim, swm, 100, runner, log));
        Assert.False(File.Exists(swm), "failed split must delete its fresh part");
        Assert.True(File.Exists(stale), "stale same-stem files are left alone");
        Assert.Contains(log.Messages, m => m.Contains(stale, StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateSwmParts_StemConvention()
    {
        var first = Path.Combine(_dir, "inst.swm");
        var second = Path.Combine(_dir, "inst2.swm");
        var third = Path.Combine(_dir, "inst3.swm");
        File.WriteAllText(first, "1");
        File.WriteAllText(second, "2");
        File.WriteAllText(third, "3");
        File.WriteAllText(Path.Combine(_dir, "other.swm"), "unrelated");
        var parts = WimBackend.EnumerateSwmParts(first).ToList();
        Assert.Equal(new[] { first, second, third }, parts);
        Assert.Empty(WimBackend.EnumerateSwmParts(Path.Combine(_dir, "missing.swm")));
    }

    [Fact]
    public void WimInfo_PassthroughShapes()
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = _ => new DismResult(0, DismInfo(2), ""),
        };
        Assert.Equal(2, WimBackend.WimInfo(wim, dism));
        Assert.Contains("/Get-WimInfo", dism.Calls[0], StringComparison.Ordinal);
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(0, WimlibInfo(3), ""),
        };
        Assert.Equal(3, WimBackend.WimInfo(wim, wimlib));
        Assert.StartsWith("info ", wimlib.Calls[0], StringComparison.Ordinal);
        var failing = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(1, "", "nope"),
        };
        var ex = Assert.Throws<WimBackendException>(() => WimBackend.WimInfo(wim, failing));
        Assert.Contains(wim, ex.Message);
    }

    [Fact]
    public void WimVerify_PassthroughShapes()
    {
        var wim = Path.Combine(_dir, "base.wim");
        File.WriteAllText(wim, "fake-wim");
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = _ => new DismResult(0, DismInfo(1), ""),
        };
        var log = new TestLogger();
        WimBackend.WimVerify(wim, dism, log);
        Assert.Contains("/Get-WimInfo", dism.Calls[0], StringComparison.Ordinal);
        var wimlib = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        WimBackend.WimVerify(wim, wimlib, log);
        Assert.StartsWith("verify ", wimlib.Calls[0], StringComparison.Ordinal);
        Assert.Contains(wim, wimlib.Calls[0], StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.Contains("integrity check OK", StringComparison.Ordinal));
        var failing = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(1, "", "corrupt"),
        };
        Assert.Throws<WimBackendException>(() => WimBackend.WimVerify(wim, failing, log));
    }

    [Fact]
    public async Task Linux_Wimlib_CaptureSplitJoinApply_RoundTrip()
    {
        // Real-binary E2E (wimlib 1.14.5 observed). File-level capture: VSS
        // snapshots don't exist on Linux, so wimcapture-from-dir is the
        // correct analogue — byte proof comes from the apply+SHA round trip.
        if (!OperatingSystem.IsLinux() || !File.Exists(WimlibRunner.DefaultBinaryPath))
            throw SkipException.ForSkip("wimlib E2E needs Linux + " + WimlibRunner.DefaultBinaryPath);
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello-split");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "world-split");
        var big = new byte[7 * 1024 * 1024]; // incompressible payload forces a multi-part split at 1 MB
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
        var wim = Path.Combine(_dir, "big.wim");
        var log = new TestLogger();
        async Task Run(string args)
        {
            var (code, _, stderr, _) = await ProcessRunner.RunAsync(
                WimlibRunner.DefaultBinaryPath + " " + args, _dir, true, CancellationToken.None, Timeout.Infinite);
            Assert.True(code == 0, args + " failed: " + stderr);
        }
        await Run($"capture {ProcessRunner.QuoteArg(src)} {ProcessRunner.QuoteArg(wim)} \"e2e\" --compress=none");
        var runner = new WimlibRunner();
        var swm = Path.Combine(_dir, "big.swm");
        Assert.Equal(swm, WimBackend.SplitSwm(wim, swm, 1, runner, log));
        var parts = Directory.GetFiles(_dir, "big*.swm").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(parts.Count >= 2, "want a multi-part set, got: " + string.Join(",", parts));
        Assert.Equal(swm, parts[0]);
        Assert.True(WimBackend.WimInfo(swm, runner) >= 1, "first SWM part must list the image");
        WimBackend.WimVerify(wim, runner, log);
        var joined = Path.Combine(_dir, "joined.wim");
        await Run($"join {ProcessRunner.QuoteArg(joined)} {string.Join(" ", parts.Select(ProcessRunner.QuoteArg))}");
        var applied = Path.Combine(_dir, "out");
        Directory.CreateDirectory(applied);
        await Run($"apply {ProcessRunner.QuoteArg(joined)} 1 {ProcessRunner.QuoteArg(applied)}");
        var (sampled, problems) = WimSample.Compare(applied, src, SingleFileBackup.MaxWimSamples);
        Assert.Equal(3, sampled);
        Assert.Empty(problems);
    }
}
