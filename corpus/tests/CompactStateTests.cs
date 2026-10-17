namespace Corpus;

public sealed class CompactStateTests : IDisposable
{
    private readonly string _dir;

    public CompactStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_compact_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Pinned real-world compact.exe sample shapes (MS Learn command
    // reference sample outputs, Oct 2025 refresh per §12.14 research —
    // embedded so the parser runs on Linux; live re-verification owned
    // by LiveQuery_WindowsOnly below).
    private const string OnOutput =
        "The system is in the Compact state. It will remain in this state unless the administrator changes it.\r\n";
    private const string OffOutput =
        "The system is not in the Compact state, but compression is available should the administrator choose to apply it.\r\n";

    private sealed class FakeRunner : ICompactRunner
    {
        private readonly string _stdout;
        public string SeenArgs { get; private set; } = "";

        public FakeRunner(string stdout) => _stdout = stdout;

        public string Run(string arguments)
        {
            SeenArgs = arguments;
            return _stdout;
        }
    }

    [Fact]
    public void Parse_OnShape_ReturnsEnabled()
    {
        var state = CompactQuery.Parse(OnOutput);
        Assert.True(state.Enabled);
        Assert.Null(state.Algorithm);
    }

    [Fact]
    public void Parse_OffShape_ReturnsDisabled()
    {
        var state = CompactQuery.Parse(OffOutput);
        Assert.False(state.Enabled);
        Assert.Null(state.Algorithm);
    }

    [Fact]
    public void Parse_UnknownOutput_ThrowsNamingSeen()
    {
        const string weird = "some future localized output 42";
        var ex = Assert.Throws<InvalidDataException>(() => CompactQuery.Parse(weird));
        Assert.Contains(weird, ex.Message);
    }

    [Fact]
    public void Parse_ReportsAlgorithmTokenWhenPresent()
    {
        // Token scan only: no confirmed reporting line exists for the query
        // verb, so this pins detection, not a line shape.
        var state = CompactQuery.Parse(OnOutput + "EXE compression: XPRESS8K\r\n");
        Assert.True(state.Enabled);
        Assert.Equal("XPRESS8K", state.Algorithm);
    }

    [Fact]
    public void BuildArgs_Shapes()
    {
        Assert.Equal("/CompactOS:query", CompactQuery.BuildArgs(null));
        Assert.Equal("/CompactOS:query /windir \"E:\\Windows\"", CompactQuery.BuildArgs(@"E:\Windows"));
    }

    [Fact]
    public void Query_PassesArgsAndParses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new FakeRunner(OnOutput);
        var state = CompactQuery.Query(runner, null);
        Assert.True(state.Enabled);
        Assert.Equal("/CompactOS:query", runner.SeenArgs);
    }

    [Fact]
    public void Query_OffWindows_ThrowsLoud()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => CompactQuery.Query(new FakeRunner(OnOutput), null));
        Assert.Throws<PlatformNotSupportedException>(() => new CompactQuery.ProcessCompactRunner().Run("/CompactOS:query"));
    }

    [Fact]
    public void Sidecar_RoundTrip()
    {
        var log = new TestLogger();
        var path = CompactStateRecord.Write(_dir, 7, new CompactOsState(true, "XPRESS8K"), log);
        Assert.Equal(Path.Combine(_dir, "manifests", "000007.compact.json"), path);
        var loaded = CompactStateRecord.Read(_dir, 7);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.Generation);
        Assert.True(loaded.CompactEnabled);
        Assert.Equal("XPRESS8K", loaded.Algorithm);
        Assert.NotEmpty(loaded.QueriedUtc);
    }

    [Fact]
    public void Read_Absent_ReturnsNull()
    {
        Assert.Null(CompactStateRecord.Read(_dir, 3));
    }

    [Fact]
    public void Read_Corrupt_ThrowsLoud()
    {
        var dir = Path.Combine(_dir, "manifests");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "000002.compact.json"), "not json{{"u8.ToArray());
        var ex = Assert.Throws<InvalidDataException>(() => CompactStateRecord.Read(_dir, 2));
        Assert.Contains("000002.compact.json", ex.Message);
    }

    [Fact]
    public void LiveQuery_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = CompactQuery.Query(new CompactQuery.ProcessCompactRunner(), null);
        // Must not throw: if this fails the pinned shapes in Parse need
        // updating to the live output (paste stdout into the failure).
        Assert.True(state.Enabled || !state.Enabled);
    }
}
