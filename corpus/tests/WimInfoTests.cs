using TicTack;

namespace Corpus;

// Item 5: info dossier. Fake-runner pins for both output shapes +
// sidecar handling; one wimlib-shaped live parse comes from the convert
// E2E (WimConvertTests), so no separate E2E here.

public sealed class WimInfoTests : IDisposable
{
    private readonly string _dir;

    public WimInfoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_wiminfo_" + Guid.NewGuid().ToString("N"));
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

    private const string DismTwo =
        "Index : 1\nName : gen-1\nSize : 1234 bytes\nLanguages : en-US\n\nIndex : 2\nName : gen-2\nSize : 5678 bytes\nLanguages : en-US de-DE";

    private const string WimlibTwo =
        "WIM Information:\n----------------\nImage Count:    2\nCompression:    LZX\nPart Number:    1/1\nBoot Index:     0\n\nAvailable Images:\n-----------------\nIndex:                  1\nName:                   img-a\nTotal Bytes:            100\nIndex:                  2\nName:                   img-b\nTotal Bytes:            200\n";

    [Fact]
    public void Get_Dism_ParsesIndexesNamesSizesLanguages()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = _ => new DismResult(0, DismTwo, ""),
        };
        var report = WimInfoReport.Get(wim, runner, new TestLogger());
        Assert.Contains("/Get-WimInfo", runner.Calls[0], StringComparison.Ordinal);
        Assert.Equal(2, report.IndexCount);
        Assert.Equal(2, report.Images.Count);
        Assert.Equal(new WimImageEntry(1, "gen-1", 1234L), report.Images[0]);
        Assert.Equal(new WimImageEntry(2, "gen-2", 5678L), report.Images[1]);
        Assert.Contains("en-US", report.Languages);
        Assert.Contains("de-DE", report.Languages);
        Assert.Null(report.CompactState);
    }

    [Fact]
    public void Get_Wimlib_ParsesIndexesNamesTotalBytes()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(0, WimlibTwo, ""),
        };
        var report = WimInfoReport.Get(wim, runner, new TestLogger());
        Assert.StartsWith("info ", runner.Calls[0], StringComparison.Ordinal);
        Assert.Equal(2, report.IndexCount);
        Assert.Equal(new WimImageEntry(1, "img-a", 100L), report.Images[0]);
        Assert.Equal(new WimImageEntry(2, "img-b", 200L), report.Images[1]);
    }

    [Fact]
    public void Parse_SizeAbsent_LeavesNull_NameAbsent_LeavesEmpty()
    {
        var (images, languages) = WimInfoReport.Parse("Index:                  1\nName:                   \n");
        Assert.Single(images);
        Assert.Equal(1, images[0].Index);
        Assert.Null(images[0].SizeBytes);
        Assert.Empty(languages);
        var dossier = new WimInfoReport(images.Count, images, languages, null).ToString();
        Assert.Contains("size unknown", dossier, StringComparison.Ordinal);
        Assert.Contains("(unnamed)", dossier, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BootIndexLine_DoesNotCount()
    {
        // "Boot Index:" must not parse as an image index.
        var (images, _) = WimInfoReport.Parse("Boot Index:     0\nIndex:                  1\nName:                   x\n");
        Assert.Single(images);
    }

    [Fact]
    public void Get_Sidecar_EnabledWithAlgorithm()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var sidecar = Path.Combine(_dir, "gen.compact.json");
        File.WriteAllText(sidecar, """{"Generation":3,"CompactEnabled":true,"Algorithm":"XPRESS","QueriedUtc":"2026-10-17T00:00:00Z"}""");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(0, WimlibTwo, ""),
        };
        var report = WimInfoReport.Get(wim, runner, new TestLogger(), sidecar);
        Assert.Equal("enabled (XPRESS)", report.CompactState);
        Assert.Contains("enabled (XPRESS)", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Sidecar_DisabledAndMissingFile()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var off = Path.Combine(_dir, "off.compact.json");
        File.WriteAllText(off, """{"Generation":3,"CompactEnabled":false,"Algorithm":null,"QueriedUtc":"x"}""");
        var runner = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = _ => new DismResult(0, DismTwo, ""),
        };
        Assert.Equal("disabled", WimInfoReport.Get(wim, runner, new TestLogger(), off).CompactState);
        Assert.Null(WimInfoReport.Get(wim, runner, new TestLogger(), Path.Combine(_dir, "absent.compact.json")).CompactState);
    }

    [Fact]
    public void Get_Sidecar_Corrupt_RefusesLoud()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var bad = Path.Combine(_dir, "bad.compact.json");
        File.WriteAllText(bad, "{not json");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Dism };
        var ex = Assert.Throws<InvalidDataException>(() =>
            WimInfoReport.Get(wim, runner, new TestLogger(), bad));
        Assert.Contains(bad, ex.Message);
    }

    [Fact]
    public void ToString_DossierLines()
    {
        var report = new WimInfoReport(2,
            new[] { new WimImageEntry(1, "gen-1", 1234L), new WimImageEntry(2, "gen-2", null) },
            new[] { "en-US" }, null);
        var text = report.ToString();
        Assert.Contains("2 image(s)", text, StringComparison.Ordinal);
        Assert.Contains("[1] gen-1 (1234 bytes)", text, StringComparison.Ordinal);
        Assert.Contains("[2] gen-2 (size unknown)", text, StringComparison.Ordinal);
        Assert.Contains("Languages: en-US", text, StringComparison.Ordinal);
        Assert.Contains("Compact: n/a", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_MissingImage_RefusesWithoutRunnerCall()
    {
        var missing = Path.Combine(_dir, "no.wim");
        var runner = new FakeWimRunner { Backend = WimBackendKind.Wimlib };
        var ex = Assert.Throws<SingleFileSourceNotFoundException>(() =>
            WimInfoReport.Get(missing, runner, new TestLogger()));
        Assert.Contains(missing, ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Get_Failure_MapsBackendException()
    {
        var wim = Path.Combine(_dir, "a.wim");
        File.WriteAllText(wim, "fake");
        var dism = new FakeWimRunner
        {
            Backend = WimBackendKind.Dism,
            Handler = _ => new DismResult(1, "", "boom"),
        };
        Assert.Throws<SingleFileDismException>(() => WimInfoReport.Get(wim, dism, new TestLogger()));
        var wimlib = new FakeWimRunner
        {
            Backend = WimBackendKind.Wimlib,
            Handler = _ => new DismResult(1, "", "boom"),
        };
        var ex = Assert.Throws<WimBackendException>(() => WimInfoReport.Get(wim, wimlib, new TestLogger()));
        Assert.Contains("boom", ex.Message);
    }
}
