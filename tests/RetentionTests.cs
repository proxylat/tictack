namespace TicTack;

public class RetentionTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _dstDir;
    private readonly string _verDir;

    public RetentionTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "TicTackTest_ret_" + Guid.NewGuid());
        _dstDir = Path.Combine(_baseDir, "sync", "Desktop");
        _verDir = Path.Combine(_baseDir, "sync", ".versions");
        Directory.CreateDirectory(_dstDir);
        Directory.CreateDirectory(_verDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { }
    }

    string Dst(string name) => Path.Combine(_dstDir, name);

    static VersionFile V(string path, DateTime stamp, bool pinned = false) =>
        new VersionFile(path, stamp, true, pinned);

    [Fact]
    public void FromConfig_LegacyMaxVersionsBecomesKeepLast()
    {
        var policy = RetentionPolicy.FromConfig(new VersioningConfig { MaxVersions = 5 });
        Assert.Equal(5, policy.KeepLast);
        Assert.False(policy.IsEmpty);
    }

    [Fact]
    public void FromConfig_KeepLastBeatsLegacy()
    {
        var policy = RetentionPolicy.FromConfig(new VersioningConfig { MaxVersions = 10, KeepLast = 3 });
        Assert.Equal(3, policy.KeepLast);
    }

    [Fact]
    public void FromConfig_InvalidValuesIgnoredWithWarning()
    {
        var log = new RecordingLogger();
        var policy = RetentionPolicy.FromConfig(new VersioningConfig { KeepWithin = "bogus", KeepDaily = -1 }, log);
        Assert.Null(policy.KeepWithin);
        Assert.Null(policy.KeepDaily);
        Assert.Contains(log.Messages, m => m.Contains("keep_within"));
        Assert.Contains(log.Messages, m => m.Contains("keep_daily"));
    }

    [Fact]
    public void IsEmpty_OnlyWhenNoPolicySet()
    {
        Assert.True(new RetentionPolicy().IsEmpty);
        Assert.False(new RetentionPolicy { KeepLast = 0 }.IsEmpty);
    }

    [Fact]
    public void Evaluate_KeepLast_KeepsNewest()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0);
        var files = Enumerable.Range(1, 5).Select(i => V("v" + i, now.AddDays(-i))).ToList();
        var decisions = new RetentionPolicy { KeepLast = 3 }.Evaluate(files, now);
        Assert.Equal(new[] { true, true, true, false, false }, decisions.Select(d => d.Keep).ToArray());
        Assert.All(decisions.Take(3), d => Assert.Equal("keep-last", d.Reason));
        Assert.All(decisions.Skip(3), d => Assert.Equal("beyond-retention", d.Reason));
    }

    [Fact]
    public void Evaluate_KeepWithin_KeepsRecent()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0);
        var files = new List<VersionFile>
        {
            V("new", now.AddDays(-1)),
            V("mid", now.AddDays(-10)),
            V("old", now.AddDays(-40)),
        };
        var decisions = new RetentionPolicy { KeepWithin = TimeSpan.FromDays(30) }.Evaluate(files, now);
        Assert.Equal(new[] { true, true, false }, decisions.Select(d => d.Keep).ToArray());
    }

    [Fact]
    public void Evaluate_PinnedAndUnparseableAlwaysKept()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0);
        var files = new List<VersionFile>
        {
            new VersionFile("plain", now.AddDays(-100), true, false),
            new VersionFile("pinned", now.AddDays(-100), true, true),
            new VersionFile("foreign.txt", DateTime.MinValue, false, false),
        };
        // keep-last 0 keeps nothing by itself: only the exemptions survive.
        var decisions = new RetentionPolicy { KeepLast = 0 }.Evaluate(files, now);
        var byPath = decisions.ToDictionary(d => d.Path);
        Assert.False(byPath["plain"].Keep);
        Assert.True(byPath["pinned"].Keep);
        Assert.Equal("pinned", byPath["pinned"].Reason);
        Assert.True(byPath["foreign.txt"].Keep);
        Assert.Equal("unparseable-name", byPath["foreign.txt"].Reason);
    }

    [Fact]
    public void Evaluate_KeepDaily_KeepsOnePerDay()
    {
        var day1 = new DateTime(2026, 9, 29);
        var day2 = new DateTime(2026, 9, 30);
        var day3 = new DateTime(2026, 10, 1);
        var files = new List<VersionFile>
        {
            V("d1a", day1.AddHours(1)),
            V("d1b", day1.AddHours(2)),
            V("d1c", day1.AddHours(3)),
            V("d2", day2.AddHours(1)),
            V("d3", day3.AddHours(1)),
        };
        var kept = new RetentionPolicy { KeepDaily = 2 }.Evaluate(files, day3.AddHours(2))
            .Where(d => d.Keep).Select(d => d.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "d2", "d3" }, kept);
    }

    [Fact]
    public void Evaluate_KeepWeekly_UsesIsoWeeks()
    {
        // 2026-09-21/28 and 2026-10-05 are Mondays: three ISO weeks.
        var files = new List<VersionFile>
        {
            V("w1a", new DateTime(2026, 9, 22)),
            V("w1b", new DateTime(2026, 9, 23)),
            V("w2", new DateTime(2026, 9, 29)),
            V("w3", new DateTime(2026, 10, 6)),
        };
        var kept = new RetentionPolicy { KeepWeekly = 2 }.Evaluate(files, new DateTime(2026, 10, 7))
            .Where(d => d.Keep).Select(d => d.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "w2", "w3" }, kept);
    }

    [Fact]
    public void Evaluate_KeepMonthly_KeepsOnePerMonth()
    {
        var files = new List<VersionFile>
        {
            V("aug1", new DateTime(2026, 8, 10)),
            V("aug2", new DateTime(2026, 8, 20)),
            V("sep", new DateTime(2026, 9, 15)),
            V("oct", new DateTime(2026, 10, 1)),
        };
        var kept = new RetentionPolicy { KeepMonthly = 2 }.Evaluate(files, new DateTime(2026, 10, 2))
            .Where(d => d.Keep).Select(d => d.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { "oct", "sep" }, kept);
    }

    [Fact]
    public void Evaluate_OrSemantics_UnionSurvives()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0);
        var files = new List<VersionFile>
        {
            V("new", now.AddDays(-1)),
            V("mid", now.AddDays(-10)),
            V("old", now.AddDays(-100)),
        };
        // keep-last 1 takes "new"; keep-within 30d takes "new"+"mid".
        // Union = new+mid; only "old" falls out.
        var byPath = new RetentionPolicy { KeepLast = 1, KeepWithin = TimeSpan.FromDays(30) }
            .Evaluate(files, now).ToDictionary(d => d.Path);
        Assert.True(byPath["new"].Keep);
        Assert.True(byPath["mid"].Keep);
        Assert.False(byPath["old"].Keep);
    }

    [Fact]
    public void IsoWeekKey_YearBoundary()
    {
        Assert.Equal("2020-W53", RetentionPolicy.IsoWeekKey(new DateTime(2021, 1, 1)));
        Assert.Equal("2021-W01", RetentionPolicy.IsoWeekKey(new DateTime(2021, 1, 4)));
    }

    [Theory]
    [InlineData("notes_20261001_142210_123_0123456789abcdef0123456789abcdef.txt", "2026-10-01 14:22:10.123", "notes.txt")]
    [InlineData("my_notes_20261001_142210_123_0123456789abcdef0123456789abcdef.txt", "2026-10-01 14:22:10.123", "my_notes.txt")]
    [InlineData("noext_20261001_142210_123_0123456789abcdef0123456789abcdef", "2026-10-01 14:22:10.123", "noext")]
    public void TryParseStamp_RoundTrip(string fileName, string expectedStamp, string expectedOrig)
    {
        Assert.True(TimestampVersioning.TryParseStamp(fileName, out var stamp, out var orig));
        Assert.Equal(DateTime.Parse(expectedStamp), stamp);
        Assert.Equal(expectedOrig, orig);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("notes_20261001.txt")]
    [InlineData("notes_20261001_142210_123_short.txt")]
    public void TryParseStamp_RejectsGarbage(string fileName)
    {
        Assert.False(TimestampVersioning.TryParseStamp(fileName, out _, out _));
    }

    [Fact]
    public async Task Archive_PrefixCollision_DoesNotEatSiblingVersions()
    {
        // a.txt's prune group must not match a_b.txt's versions (the old
        // prefix match did: "a_" prefixes "a_b_...").
        File.WriteAllText(Dst("a.txt"), "v0");
        var versioning = new TimestampVersioning(_verDir, _dstDir, 1);
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
        var groupDir = Path.Combine(_verDir, "Desktop");
        var sibling = Path.Combine(groupDir, "a_b_20200101_000000_000_0123456789abcdef0123456789abcdef.txt");
        File.WriteAllText(sibling, "sibling");

        File.WriteAllText(Dst("a.txt"), "v1");
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);

        Assert.True(File.Exists(sibling));
        var aVersions = Directory.GetFiles(groupDir)
            .Where(f => TimestampVersioning.TryParseStamp(Path.GetFileName(f), out _, out var o) && o == "a.txt")
            .ToArray();
        Assert.Single(aVersions);
    }

    [Fact]
    public async Task Archive_PinnedVersionSurvivesPrune()
    {
        File.WriteAllText(Dst("a.txt"), "v0");
        var versioning = new TimestampVersioning(_verDir, _dstDir, 1);
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
        var groupDir = Path.Combine(_verDir, "Desktop");
        var first = Assert.Single(Directory.GetFiles(groupDir, "a_*"));
        File.WriteAllText(TimestampVersioning.PinPath(first), "pinned test");

        File.WriteAllText(Dst("a.txt"), "v1");
        await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);

        // Cap says 1, but the pinned version is exempt: both survive.
        Assert.Equal(2, Directory.GetFiles(groupDir, "a_*").Where(f => !f.EndsWith(".pinned", StringComparison.Ordinal)).ToArray().Length);
    }

    [Fact]
    public async Task Archive_EmptyPolicy_DefaultsToKeepTen()
    {
        var versioning = new TimestampVersioning(_verDir, _dstDir, new RetentionPolicy());
        for (int i = 0; i < 12; i++)
        {
            File.WriteAllText(Dst("a.txt"), "version " + i);
            await versioning.ArchivePreviousVersionAsync(Dst("a.txt"), CancellationToken.None);
        }
        Assert.Equal(10, Directory.GetFiles(Path.Combine(_verDir, "Desktop"), "a_*").Length);
    }
}
