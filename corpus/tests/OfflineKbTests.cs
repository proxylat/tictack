namespace Corpus;

// Item 9 tests: offline-only KB removal through the shared ITrimRunner
// fake (Linux-green). DISM /Get-Packages shape + identity-contains-KB
// match are training-knowledge, on-box-unprobed — pinned so a VM run can
// diff them.
public sealed class OfflineKbTests : IDisposable
{
    private readonly string _dir;
    private readonly string _windows;

    public OfflineKbTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_kb_" + Guid.NewGuid());
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

    private const string ListOutput = """
        Deployment Image Servicing and Management tool

        Packages listing:

        Package Identity : Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.22621.1
        State : Installed

        Package Identity : Package_for_RollupFix_KB5034441~31bf3856ad364e35~amd64~~22621.1234.1.9
        State : Installed
        """;

    [Fact]
    public void IsLive_ExactCaseInsensitiveAndSlashed()
    {
        Assert.True(OfflineKb.IsLiveWindowsDir(@"C:\Windows", [@"C:\Windows"]));
        Assert.True(OfflineKb.IsLiveWindowsDir(@"c:\windows", [@"C:\WINDOWS"]));
        Assert.True(OfflineKb.IsLiveWindowsDir(@"C:\Windows\", [@"C:\Windows"]));
        Assert.False(OfflineKb.IsLiveWindowsDir(@"D:\Image\Windows", [@"C:\Windows"]));
        Assert.False(OfflineKb.IsLiveWindowsDir(@"D:\Image\Windows", []));
        Assert.False(OfflineKb.IsLiveWindowsDir(@"D:\Image\Windows", [null!]));
    }

    [Fact]
    public void ParseIdentities_KeepsOnlyKbMatches()
    {
        var ids = OfflineKb.ParseIdentities(ListOutput, "KB5034441");
        Assert.Equal(
            ["Package_for_RollupFix_KB5034441~31bf3856ad364e35~amd64~~22621.1234.1.9"], ids);
        Assert.Empty(OfflineKb.ParseIdentities(ListOutput, "KB9999999"));
        Assert.Empty(OfflineKb.ParseIdentities(string.Empty, "KB5034441"));
    }

    [Fact]
    public void RemoveKb_QueryThenRemove_ArgShapes()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, 0, c.Contains("/Get-Packages") ? ListOutput : string.Empty)
        };
        Assert.Equal(0, OfflineKb.RemoveKb(_windows, ["KB5034441"], fake, log, false));
        Assert.Equal(
            new[]
            {
                $"dism /Image:\"{ImageRoot(_windows)}\" /Get-Packages",
                $"dism /Image:\"{ImageRoot(_windows)}\" /Remove-Package /PackageName:Package_for_RollupFix_KB5034441~31bf3856ad364e35~amd64~~22621.1234.1.9",
            }, fake.Calls);
    }

    [Fact]
    public void RemoveKb_NoMatch_WarnsAndContinues()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, 0, c.Contains("/Get-Packages") ? ListOutput : string.Empty)
        };
        Assert.Equal(0, OfflineKb.RemoveKb(_windows, ["KB9999999"], fake, log, false));
        Assert.Single(fake.Calls);
        Assert.Contains("WRN:", string.Join("\n", log.Messages));
    }

    [Fact]
    public void RemoveKb_FirstUnknownSecondKnown_BothQueriedOneRemoved()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, 0, c.Contains("/Get-Packages") ? ListOutput : string.Empty)
        };
        Assert.Equal(0, OfflineKb.RemoveKb(_windows, ["KB9999999", "KB5034441"], fake, log, false));
        Assert.Equal(3, fake.Calls.Count);
        Assert.Contains("/Remove-Package", fake.Calls[2]);
    }

    [Fact]
    public void RemoveKb_QueryFails_FailsLoud()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner { Handler = c => new TrimResult(c, 2, "dism complaint") };
        Assert.Equal(1, OfflineKb.RemoveKb(_windows, ["KB5034441"], fake, log, false));
    }

    [Fact]
    public void RemoveKb_RemoveFails_FailsLoud()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, c.Contains("/Remove-Package") ? 5 : 0,
                c.Contains("/Get-Packages") ? ListOutput : string.Empty)
        };
        Assert.Equal(1, OfflineKb.RemoveKb(_windows, ["KB5034441"], fake, log, false));
        Assert.Contains("exit 5", string.Join("\n", log.Messages));
    }

    [Fact]
    public void RemoveKb_LiveDir_RefusesWithZeroCalls()
    {
        // Fake the live OS via %SystemRoot%: the guard must refuse a tree
        // that resolves to it, before any DISM call.
        var old = Environment.GetEnvironmentVariable("SystemRoot");
        Environment.SetEnvironmentVariable("SystemRoot", _windows);
        try
        {
            var log = new TestLogger();
            var fake = new FakeTrimRunner();
            Assert.Equal(1, OfflineKb.RemoveKb(_windows, ["KB5034441"], fake, log, false));
            Assert.Empty(fake.Calls);
            Assert.Contains("LIVE", string.Join("\n", log.Messages));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SystemRoot", old);
        }
    }

    [Fact]
    public void RemoveKb_EmptyList_Noop()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineKb.RemoveKb(_windows, [], fake, log, false));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void RemoveKb_DryRun_ZeroCalls()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineKb.RemoveKb(_windows, ["KB5034441"], fake, log, true));
        Assert.Empty(fake.Calls);
    }
}
