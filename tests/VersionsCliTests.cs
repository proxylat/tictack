using System.Text.Json;

namespace TicTack;

public class VersionsCliTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _verDir;

    public VersionsCliTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "TicTackTest_vercli_" + Guid.NewGuid());
        _verDir = Path.Combine(_baseDir, "versions");
        Directory.CreateDirectory(_verDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { }
    }

    TicTackConfig Cfg(VersioningConfig ver) => new TicTackConfig
    {
        Sources = new List<SourceConfig>
        {
            new SourceConfig
            {
                Path = Path.Combine(_baseDir, "src"),
                Destination = Path.Combine(_baseDir, "dst"),
                Sync = new SyncConfig { Versioning = ver }
            }
        }
    };

    string MakeVersion(string orig, DateTime stamp, string? subdir = null)
    {
        var dir = subdir == null ? _verDir : Path.Combine(_verDir, subdir);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(orig) + "_" + stamp.ToString("yyyyMMdd_HHmmss_fff")
            + "_" + Guid.NewGuid().ToString("N") + Path.GetExtension(orig);
        var full = Path.Combine(dir, name);
        File.WriteAllText(full, "data " + stamp.ToString("o"));
        return full;
    }

    [Fact]
    public void GroupVersions_GroupsByOriginal()
    {
        MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        MakeVersion("notes.txt", new DateTime(2026, 10, 2, 10, 0, 0));
        MakeVersion("todo.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        File.WriteAllText(Path.Combine(_verDir, "foreign.txt"), "not a version");

        var groups = Program.GroupVersions(_verDir);
        Assert.Equal(3, groups.Count);
        var notes = Assert.Single(groups.Values, g => g[0].Display == "notes.txt");
        Assert.Equal(2, notes.Count);
        var foreign = Assert.Single(groups.Values, g => g[0].Display == "foreign.txt");
        Assert.False(Assert.Single(foreign).HasStamp);
    }

    [Fact]
    public void RunForget_DryRun_ChangesNothing()
    {
        MakeVersion("notes.txt", new DateTime(2026, 9, 1, 10, 0, 0));
        MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        var log = new RecordingLogger();
        var cfg = Cfg(new VersioningConfig { Path = _verDir, KeepLast = 1 });

        var rc = Program.RunForget(cfg, log, true, null, null, null, null, null);

        Assert.Equal(0, rc);
        Assert.Equal(2, Directory.GetFiles(_verDir).Length);
        Assert.Contains(log.Messages, m => m.Contains("would remove"));
        Assert.Contains(log.Messages, m => m.Contains("would keep 1"));
    }

    [Fact]
    public void RunForget_AppliesPolicy()
    {
        var old = MakeVersion("notes.txt", new DateTime(2026, 9, 1, 10, 0, 0));
        var recent = MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        var log = new RecordingLogger();
        var cfg = Cfg(new VersioningConfig { Path = _verDir, KeepLast = 1 });

        var rc = Program.RunForget(cfg, log, false, null, null, null, null, null);

        Assert.Equal(0, rc);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Contains(log.Messages, m => m.Contains("removed 1"));
    }

    [Fact]
    public void RunForget_OverridesReplaceConfig()
    {
        MakeVersion("notes.txt", new DateTime(2026, 9, 1, 10, 0, 0));
        MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        var log = new RecordingLogger();
        var cfg = Cfg(new VersioningConfig { Path = _verDir, KeepLast = 5 });

        var rc = Program.RunForget(cfg, log, false, "1", null, null, null, null);

        Assert.Equal(0, rc);
        Assert.Single(Directory.GetFiles(_verDir));
    }

    [Fact]
    public void RunForget_InvalidOverride_ReturnsOne()
    {
        var log = new RecordingLogger();
        var cfg = Cfg(new VersioningConfig { Path = _verDir });
        Assert.Equal(1, Program.RunForget(cfg, log, false, "-2", null, null, null, null));
        Assert.Equal(1, Program.RunForget(cfg, log, false, null, "bogus", null, null, null));
    }

    [Fact]
    public void RunVersionsList_TableLatestAndGlob()
    {
        MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        MakeVersion("notes.txt", new DateTime(2026, 10, 2, 10, 0, 0));
        MakeVersion("todo.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        var cfg = Cfg(new VersioningConfig { Path = _verDir });

        var log = new RecordingLogger();
        Assert.Equal(0, Program.RunVersionsList(cfg, log, null, null, false));
        Assert.Equal(3, log.Messages.Count(m => m.Contains(".txt")));

        var latest = new RecordingLogger();
        Assert.Equal(0, Program.RunVersionsList(cfg, latest, null, "1", false));
        Assert.Equal(2, latest.Messages.Count(m => m.Contains(".txt")));
        Assert.Contains(latest.Messages, m => m.Contains("2026-10-02"));

        var glob = new RecordingLogger();
        Assert.Equal(0, Program.RunVersionsList(cfg, glob, "todo*", null, false));
        Assert.Single(glob.Messages, m => m.Contains(".txt"));
        Assert.Contains(glob.Messages, m => m.Contains("todo.txt"));

        var empty = new RecordingLogger();
        Assert.Equal(0, Program.RunVersionsList(cfg, empty, "nomatch*", null, false));
        Assert.Contains(empty.Messages, m => m.Contains("No versions found."));
    }

    [Fact]
    public void RunVersionsList_Json_IsParseableArray()
    {
        MakeVersion("notes.txt", new DateTime(2026, 10, 2, 10, 0, 0));
        var cfg = Cfg(new VersioningConfig { Path = _verDir });
        var capture = new StringWriter();
        var original = Console.Out;
        Console.SetOut(capture);
        try
        {
            Assert.Equal(0, Program.RunVersionsList(cfg, new RecordingLogger(), null, null, true));
        }
        finally
        {
            Console.SetOut(original);
        }
        var json = capture.ToString();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("notes.txt", entry.GetProperty("file").GetString());
        Assert.Equal("2026-10-02T10:00:00", entry.GetProperty("timestamp").GetString());
        Assert.False(entry.GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public void Pin_RoundTrip_ProtectsFromForget()
    {
        MakeVersion("notes.txt", new DateTime(2026, 9, 1, 10, 0, 0));
        var recent = MakeVersion("notes.txt", new DateTime(2026, 10, 1, 10, 0, 0));
        var cfg = Cfg(new VersioningConfig { Path = _verDir, KeepLast = 0 });
        var log = new RecordingLogger();

        Assert.Equal(0, Program.RunVersionsPin(cfg, log, "notes@2026-10-01", true));
        Assert.True(File.Exists(TimestampVersioning.PinPath(recent)));

        // keep-last 0 removes everything unpinned; the pinned version stays.
        Assert.Equal(0, Program.RunForget(cfg, log, false, null, null, null, null, null));
        Assert.True(File.Exists(recent));
        Assert.Single(Directory.GetFiles(_verDir), f => !f.EndsWith(".pinned", StringComparison.Ordinal));

        Assert.Equal(0, Program.RunVersionsPin(cfg, log, recent, false));
        Assert.False(File.Exists(TimestampVersioning.PinPath(recent)));
    }

    [Fact]
    public void ResolvePinTargets_AcceptsTimestampSpellings()
    {
        var target = MakeVersion("notes.txt", new DateTime(2026, 10, 1, 14, 22, 10));
        MakeVersion("notes.txt", new DateTime(2026, 10, 2, 10, 0, 0));
        var cfg = Cfg(new VersioningConfig { Path = _verDir });
        var log = new RecordingLogger();

        foreach (var spec in new[] { "notes@2026-10-01", "notes@20261001", "@2026-10-01T14:22", "notes.txt@20261001_1422" })
        {
            var hits = Program.ResolvePinTargets(cfg, spec, log);
            Assert.NotNull(hits);
            Assert.Equal(new[] { target }, hits);
        }
        Assert.Empty(Program.ResolvePinTargets(cfg, "notes@2025-01-01", log)!);
        Assert.Null(Program.ResolvePinTargets(cfg, "no-at-sign", log));
    }

    [Theory]
    [InlineData("Desktop/notes.txt", "*.txt", true)]
    [InlineData("Desktop/notes.txt", "desktop/*.txt", true)]
    [InlineData("Desktop/notes.txt", "note?.txt", false)]
    [InlineData("notes.txt", "note?.txt", true)]
    public void WildcardMatch_Matrix(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, Program.WildcardMatch(text, pattern));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void FormatSize_Matrix(long bytes, string expected)
    {
        Assert.Equal(expected, Program.FormatSize(bytes));
    }
}
