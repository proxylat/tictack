namespace Corpus;

// Items 7+10 tests: unattend apply + lang-pack add through the shared
// ITrimRunner fake (Linux-green). DISM arg shapes are training-knowledge,
// on-box-unprobed — pinned here so a VM run can diff them.
public sealed class OfflineServicingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _windows;

    public OfflineServicingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_serv_" + Guid.NewGuid());
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
    public void ApplyUnattend_ArgShape()
    {
        var unattend = Path.Combine(_dir, "unattend.xml");
        File.WriteAllText(unattend, "<unattend/>");
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineServicing.ApplyUnattend(_windows, unattend, fake, log, false));
        Assert.Equal(
            new[] { $"dism /Image:\"{ImageRoot(_windows)}\" /Apply-Unattend:\"{unattend}\"" },
            fake.Calls);
    }

    [Fact]
    public void ApplyUnattend_MissingFile_Refuses()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(1, OfflineServicing.ApplyUnattend(_windows, Path.Combine(_dir, "nope.xml"), fake, log, false));
        Assert.Empty(fake.Calls);
        Assert.Contains("nope.xml", string.Join("\n", log.Messages));
    }

    [Fact]
    public void ApplyUnattend_MissingHive_Refuses()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        var unattend = Path.Combine(_dir, "unattend.xml");
        File.WriteAllText(unattend, "<unattend/>");
        Assert.Equal(1, OfflineServicing.ApplyUnattend(Path.Combine(_dir, "bare"), unattend, fake, log, false));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void ApplyUnattend_DryRun_ZeroCalls()
    {
        var unattend = Path.Combine(_dir, "unattend.xml");
        File.WriteAllText(unattend, "<unattend/>");
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineServicing.ApplyUnattend(_windows, unattend, fake, log, true));
        Assert.Empty(fake.Calls);
        Assert.Contains("dry run", string.Join("\n", log.Messages));
    }

    [Fact]
    public void ApplyUnattend_NonZeroExit_Fails()
    {
        var unattend = Path.Combine(_dir, "unattend.xml");
        File.WriteAllText(unattend, "<unattend/>");
        var log = new TestLogger();
        var fake = new FakeTrimRunner { Handler = c => new TrimResult(c, 2, "dism complaint") };
        Assert.Equal(1, OfflineServicing.ApplyUnattend(_windows, unattend, fake, log, false));
        Assert.Contains("exit 2", string.Join("\n", log.Messages));
    }

    [Fact]
    public void AddLangPack_ArgShape_PerCabInOrder()
    {
        var a = Path.Combine(_dir, "a.cab");
        var b = Path.Combine(_dir, "b.cab");
        File.WriteAllText(a, "cab");
        File.WriteAllText(b, "cab");
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineServicing.AddLangPack(_windows, [a, b], fake, log, false));
        var root = $"\"{ImageRoot(_windows)}\"";
        Assert.Equal(
            new[]
            {
                $"dism /Image:{root} /Add-Package /PackagePath:\"{a}\"",
                $"dism /Image:{root} /Add-Package /PackagePath:\"{b}\"",
            }, fake.Calls);
    }

    [Fact]
    public void AddLangPack_MissingCab_RefusesBeforeAnyMutation()
    {
        var a = Path.Combine(_dir, "a.cab");
        File.WriteAllText(a, "cab");
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(1, OfflineServicing.AddLangPack(_windows, [a, Path.Combine(_dir, "missing.cab")], fake, log, false));
        Assert.Empty(fake.Calls);
        Assert.Contains("missing.cab", string.Join("\n", log.Messages));
    }

    [Fact]
    public void AddLangPack_EmptyList_Noop()
    {
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineServicing.AddLangPack(_windows, [], fake, log, false));
        Assert.Empty(fake.Calls);
        Assert.Contains("nothing to do", string.Join("\n", log.Messages));
    }

    [Fact]
    public void AddLangPack_DryRun_ZeroCalls()
    {
        var a = Path.Combine(_dir, "a.cab");
        File.WriteAllText(a, "cab");
        var log = new TestLogger();
        var fake = new FakeTrimRunner();
        Assert.Equal(0, OfflineServicing.AddLangPack(_windows, [a], fake, log, true));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void AddLangPack_FirstFails_SecondStillRuns()
    {
        var a = Path.Combine(_dir, "a.cab");
        var b = Path.Combine(_dir, "b.cab");
        File.WriteAllText(a, "cab");
        File.WriteAllText(b, "cab");
        var log = new TestLogger();
        var fake = new FakeTrimRunner
        {
            Handler = c => new TrimResult(c, c.Contains($"\"{a}\"") ? 3 : 0, string.Empty)
        };
        Assert.Equal(1, OfflineServicing.AddLangPack(_windows, [a, b], fake, log, false));
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("exit 3", string.Join("\n", log.Messages));
    }
}
