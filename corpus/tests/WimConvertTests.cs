using System.Security.Cryptography;
using TicTack;

namespace Corpus;

// Items 3+4: convert + SWM merge. All backend surface runs behind
// FakeWimRunner — the DISM /Export-Image shapes are knowledge-sourced
// (on-box Windows probe owed); wimlib export/join shapes verified against
// wimlib-imagex 1.14.5 on this box. The Linux E2E at the bottom runs the
// REAL binary: capture -> convert -> info -> split -> merge -> apply.

public sealed class WimConvertTests : IDisposable
{
    private readonly string _dir;

    public WimConvertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wimconvert_" + Guid.NewGuid().ToString("N"));
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

    private static string DismInfo(int indexes) =>
        string.Join("\n", Enumerable.Range(1, Math.Max(indexes, 0))
            .Select(i => $"Index : {i}\nName : gen-{i}\nLanguages : en-US"));

    private static string WimlibInfo(int indexes) =>
        "WIM Information:\n----------------\nImage Count:    " + indexes + "\nCompression:    XPRESS\nPart Number:    1/1\nBoot Index:     0\n\nAvailable Images:\n-----------------\n"
        + string.Join("", Enumerable.Range(1, Math.Max(indexes, 0)).Select(i => $"Index:                  {i}\nName:                   img-{i}\nTotal Bytes:            {100 + i}\n"));

    private string SeedWim(string name = "base.wim")
    {
        var wim = Path.Combine(_dir, name);
        File.WriteAllText(wim, "fake-wim");
        return wim;
    }

    [Fact]
    public void Convert_Wimlib_SingleIndex_DefaultsTo1()
    {
        var src = SeedWim();
        var dest = Path.Combine(_dir, "out.esd");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        var log = new TestLogger();
        Assert.Equal(dest, WimImageOps.Convert(src, dest, null, WimCompression.Recovery, runner, log));
        Assert.Equal(2, runner.Calls.Count);
        Assert.StartsWith("info ", runner.Calls[0], StringComparison.Ordinal);
        var export = runner.Calls[1];
        Assert.StartsWith("export ", export, StringComparison.Ordinal);
        Assert.Contains("\"" + src + "\"", export, StringComparison.Ordinal);
        Assert.Contains(" 1 ", export, StringComparison.Ordinal);
        Assert.Contains("\"" + dest + "\"", export, StringComparison.Ordinal);
        Assert.Contains("--compress=recovery", export, StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.Contains("defaults to index 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_Wimlib_Max_MapsToMaximum()
    {
        var src = SeedWim();
        var dest = Path.Combine(_dir, "out.wim");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(0, "", ""),
        };
        WimImageOps.Convert(src, dest, null, WimCompression.Max, runner, new TestLogger());
        Assert.Contains("--compress=maximum", runner.Calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_Dism_ExplicitIndex_ExportShape()
    {
        var src = SeedWim();
        var dest = Path.Combine(_dir, "out.esd");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(2), "")
                : new DismResult(0, "", ""),
        };
        // On-box-owed: needs Windows + real DISM; the fake pins shape only.
        WimImageOps.Convert(src, dest, 2, WimCompression.Recovery, runner, new TestLogger());
        Assert.Equal(2, runner.Calls.Count);
        var export = runner.Calls[1];
        Assert.StartsWith("/Export-Image", export, StringComparison.Ordinal);
        Assert.Contains("/SourceImageFile:", export, StringComparison.Ordinal);
        Assert.Contains("/SourceIndex:2", export, StringComparison.Ordinal);
        Assert.Contains("/DestinationImageFile:", export, StringComparison.Ordinal);
        Assert.Contains("/Compress:recovery", export, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_Dism_Max_MapsToMax()
    {
        var src = SeedWim();
        var dest = Path.Combine(_dir, "out.wim");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(1), "")
                : new DismResult(0, "", ""),
        };
        WimImageOps.Convert(src, dest, null, WimCompression.Max, runner, new TestLogger());
        Assert.Contains("/Compress:max", runner.Calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_MultiIndex_NullIndex_RefusesNamingCount()
    {
        var src = SeedWim();
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
            var ex = Assert.Throws<WimConvertRefusedException>(() =>
                WimImageOps.Convert(src, Path.Combine(_dir, "o.wim"), null, WimCompression.Max, runner, new TestLogger()));
            Assert.Contains("3", ex.Message, StringComparison.Ordinal);
            Assert.Single(runner.Calls); // probe only, no export
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Convert_IndexOutOfRange_RefusesBeforeExport(int index)
    {
        var src = SeedWim();
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(2), "")
                : new DismResult(0, "", ""),
        };
        var ex = Assert.Throws<WimConvertRefusedException>(() =>
            WimImageOps.Convert(src, Path.Combine(_dir, "o.wim"), index, WimCompression.Max, runner, new TestLogger()));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void Convert_MissingSource_RefusesWithoutRunnerCall()
    {
        var missing = Path.Combine(_dir, "no.wim");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
                WimImageOps.Convert(missing, Path.Combine(_dir, "o.wim"), null, WimCompression.Max, runner, new TestLogger()));
            Assert.Contains(missing, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void Convert_SourceEqualsDest_RefusesWithoutRunnerCall()
    {
        var src = SeedWim();
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<WimConvertRefusedException>(() =>
                WimImageOps.Convert(src, src, 1, WimCompression.Max, runner, new TestLogger()));
            Assert.Contains(src, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void Convert_EsdDestWithMax_RefusesPointingAtRecovery()
    {
        var src = SeedWim("base.wim");
        var esd = Path.Combine(_dir, "out.esd");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<WimConvertRefusedException>(() =>
                WimImageOps.Convert(src, esd, null, WimCompression.Max, runner, new TestLogger()));
            Assert.Contains(esd, ex.Message);
            Assert.Contains("Recovery", ex.Message, StringComparison.Ordinal);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void Convert_Failure_MapsBackendException()
    {
        var src = SeedWim();
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = args => args.Contains("/Get-WimInfo", StringComparison.Ordinal)
                ? new DismResult(0, DismInfo(1), "")
                : new DismResult(1, "", "boom"),
        };
        var dex = Assert.Throws<SingleFileDismException>(() =>
            WimImageOps.Convert(src, Path.Combine(_dir, "o.wim"), null, WimCompression.Max, dism, new TestLogger()));
        Assert.Contains(src, dex.Message);
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = args => args.StartsWith("info ", StringComparison.Ordinal)
                ? new DismResult(0, WimlibInfo(1), "")
                : new DismResult(1, "", "boom"),
        };
        var wex = Assert.Throws<WimBackendException>(() =>
            WimImageOps.Convert(src, Path.Combine(_dir, "o.wim"), null, WimCompression.Max, wimlib, new TestLogger()));
        Assert.Contains(src, wex.Message);
        Assert.Contains("boom", wex.Message);
    }

    [Fact]
    public void MergeSwm_Wimlib_JoinShape_AllParts()
    {
        var first = Path.Combine(_dir, "m.swm");
        File.WriteAllText(first, "1");
        var second = Path.Combine(_dir, "m2.swm");
        File.WriteAllText(second, "2");
        var dest = Path.Combine(_dir, "merged.wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        Assert.Equal(dest, WimImageOps.MergeSwm(first, dest, runner, new TestLogger()));
        Assert.Single(runner.Calls);
        var join = runner.Calls[0];
        Assert.StartsWith("join ", join, StringComparison.Ordinal);
        Assert.Contains("\"" + dest + "\"", join, StringComparison.Ordinal);
        Assert.True(join.IndexOf("\"" + first + "\"", StringComparison.Ordinal) < join.IndexOf("\"" + second + "\"", StringComparison.Ordinal));
    }

    [Fact]
    public void MergeSwm_Dism_ExportShapeWithSwmGlob()
    {
        // On-box-owed: needs Windows + real DISM; the fake pins shape only.
        var first = Path.Combine(_dir, "m.swm");
        File.WriteAllText(first, "1");
        File.WriteAllText(Path.Combine(_dir, "m2.swm"), "2");
        var dest = Path.Combine(_dir, "merged.wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        WimImageOps.MergeSwm(first, dest, runner, new TestLogger());
        Assert.Single(runner.Calls);
        var export = runner.Calls[0];
        Assert.StartsWith("/Export-Image", export, StringComparison.Ordinal);
        Assert.Contains("/SourceImageFile:", export, StringComparison.Ordinal);
        Assert.Contains("/SWMFile:", export, StringComparison.Ordinal);
        Assert.Contains("m*.swm", export, StringComparison.Ordinal);
        Assert.Contains("/SourceIndex:1", export, StringComparison.Ordinal);
        Assert.Contains("/DestinationImageFile:", export, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeSwm_MissingFirst_RefusesNamingPath()
    {
        var missing = Path.Combine(_dir, "no.swm");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
                WimImageOps.MergeSwm(missing, Path.Combine(_dir, "m.wim"), runner, new TestLogger()));
            Assert.Contains(missing, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void MergeSwm_LonePart_RefusesWithoutRunnerCall()
    {
        var lone = Path.Combine(_dir, "lone.swm");
        File.WriteAllText(lone, "1");
        foreach (var backend in new[] { WimBackendKind.Dism, WimBackendKind.Wimlib })
        {
            var runner = new FakeWimRunner { Backend = backend };
            var ex = Assert.Throws<WimConvertRefusedException>(() =>
                WimImageOps.MergeSwm(lone, Path.Combine(_dir, "m.wim"), runner, new TestLogger()));
            Assert.Contains(lone, ex.Message);
            Assert.Empty(runner.Calls);
        }
    }

    [Fact]
    public void MergeSwm_Failure_NamesFirstPart()
    {
        var first = Path.Combine(_dir, "m.swm");
        File.WriteAllText(first, "1");
        File.WriteAllText(Path.Combine(_dir, "m2.swm"), "2");
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(1, "", "boom"),
        };
        var ex = Assert.Throws<WimBackendException>(() =>
            WimImageOps.MergeSwm(first, Path.Combine(_dir, "m.wim"), wimlib, new TestLogger()));
        Assert.Contains(first, ex.Message);
    }

    [Fact]
    public async Task Linux_Wimlib_CaptureConvertInfoSplitMerge_RoundTrip()
    {
        // Real-binary E2E (wimlib 1.14.5 observed): capture -> SplitSwm ->
        // Convert (single-index export) -> WimInfoReport -> MergeSwm(join)
        // -> apply -> SHA compare.
        // No wimlib binary here: not a failure, nothing to exercise.
        if (!OperatingSystem.IsLinux() || !File.Exists(WimlibRunner.DefaultBinaryPath))
            return;
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello-convert");
        var big = new byte[7 * 1024 * 1024]; // incompressible payload forces a multi-part split at 1 MB
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
        var converted = WimImageOps.Convert(wim, Path.Combine(_dir, "conv.wim"), null, WimCompression.Max, runner, log);
        var report = WimInfoReport.Get(converted, runner, log);
        Assert.Equal(1, report.IndexCount);
        var swm = Path.Combine(_dir, "conv.swm");
        WimBackend.SplitSwm(converted, swm, 1, runner, log);
        var parts = Directory.GetFiles(_dir, "conv*.swm").OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(parts.Count >= 2, "want a multi-part set, got: " + string.Join(",", parts));
        var merged = WimImageOps.MergeSwm(swm, Path.Combine(_dir, "merged.wim"), runner, log);
        var applied = Path.Combine(_dir, "out");
        Directory.CreateDirectory(applied);
        await Run($"apply {ProcessRunner.QuoteArg(merged)} 1 {ProcessRunner.QuoteArg(applied)}");
        var (sampled, problems) = WimSample.Compare(applied, src, SingleFileBackup.MaxWimSamples);
        Assert.Equal(2, sampled);
        Assert.Empty(problems);
    }
}
