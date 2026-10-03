namespace TicTack;

[Collection("SerialWatcher")]
public class CommandTests
{
    [Fact]
    public async Task RunOnceAsync_UsesTheSamePipelinePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-command-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            File.WriteAllText(Path.Combine(source, "file.txt"), "command");
            var cfg = new TicTackConfig
            {
                Logging = new LoggingConfig { Path = Path.Combine(root, "tictack.log") },
                Sources = new List<SourceConfig>
                {
                    new SourceConfig
                    {
                        Path = source,
                        Destination = destination,
                        StateDbPath = Path.Combine(root, "state")
                    }
                }
            };

            Assert.True(await Program.RunOnceAsync(cfg, new RecordingLogger()));
            Assert.Equal("command", File.ReadAllText(Path.Combine(destination, "file.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void GetStateDbPath_SameLeafDifferentSources_UsesPathHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-statedb-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(root, "a", "Desktop"));
        Directory.CreateDirectory(Path.Combine(root, "b", "Desktop"));
        Directory.CreateDirectory(stateDir);
        try
        {
            var a = new SourceConfig { Path = Path.Combine(root, "a", "Desktop"), Destination = Path.Combine(root, "x"), StateDbPath = stateDir };
            var b = new SourceConfig { Path = Path.Combine(root, "b", "Desktop"), Destination = Path.Combine(root, "y"), StateDbPath = stateDir };
            var pa = Program.GetStateDbPath(a);
            var pb = Program.GetStateDbPath(b);
            Assert.NotEqual(pa, pb);
            Assert.StartsWith(Path.Combine(stateDir, "Desktop-"), pa);
            Assert.StartsWith(Path.Combine(stateDir, "Desktop-"), pb);

            File.WriteAllText(Path.Combine(stateDir, "Desktop.db"), "legacy");
            File.WriteAllText(Path.Combine(stateDir, "Desktop.db-wal"), "wal");
            var moved = Program.GetStateDbPath(a);
            Assert.Equal(pa, moved);
            Assert.Equal("legacy", File.ReadAllText(moved));
            Assert.Equal("wal", File.ReadAllText(moved + "-wal"));
            Assert.False(File.Exists(Path.Combine(stateDir, "Desktop.db")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static TicTackConfig DeferredCfg(string logPath, params SourceConfig[] sources) => new TicTackConfig
    {
        Logging = new LoggingConfig { Path = logPath },
        Sources = new List<SourceConfig>(sources)
    };

    [Fact]
    public async Task DeferredList_PrintsStemPathAndCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-deferredcli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "a", "Desktop");
        Directory.CreateDirectory(source);
        try
        {
            var src = new SourceConfig { Path = source, Destination = Path.Combine(root, "dst") };
            var stem = Program.DeferredFilePrefix(src);
            var log = new RecordingLogger();
            using (var dd = new DeferredDeletion(root, stem, 7, log))
                dd.RecordPending(new List<string> { Path.Combine(source, "x.txt") }, source);

            var rc = await Program.RunDeferredAsync(DeferredCfg(Path.Combine(root, "tictack.log"), src), log, true, null, null);
            Assert.Equal(0, rc);
            Assert.Contains(log.Messages, m => m.Contains(stem) && m.Contains(source) && m.Contains("files=1"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DeferredList_NoHolds_PrintsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-deferredcli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = new SourceConfig { Path = Path.Combine(root, "src"), Destination = Path.Combine(root, "dst") };
            var log = new RecordingLogger();
            var rc = await Program.RunDeferredAsync(DeferredCfg(Path.Combine(root, "tictack.log"), src), log, true, null, null);
            Assert.Equal(0, rc);
            Assert.Contains(log.Messages, m => m.Contains("No deferred holds."));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DeferredCancel_ClearsHoldsKeepsFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-deferredcli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "a", "Desktop");
        Directory.CreateDirectory(source);
        try
        {
            var src = new SourceConfig { Path = source, Destination = Path.Combine(root, "dst") };
            var stem = Program.DeferredFilePrefix(src);
            using (var dd = new DeferredDeletion(root, stem, 7, new RecordingLogger()))
                dd.RecordPending(new List<string> { Path.Combine(source, "x.txt") }, source);

            var log = new RecordingLogger();
            var rc = await Program.RunDeferredAsync(DeferredCfg(Path.Combine(root, "tictack.log"), src), log, false, "--reprove", stem);
            Assert.Equal(0, rc);
            Assert.Contains(log.Messages, m => m.Contains("kept"));
            using var dd2 = new DeferredDeletion(root, stem, 7, new RecordingLogger());
            Assert.False(dd2.HasPending);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DeferredCheck_AmbiguousLeaf_ListsCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-deferredcli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "Desktop"));
        Directory.CreateDirectory(Path.Combine(root, "b", "Desktop"));
        try
        {
            var a = new SourceConfig { Path = Path.Combine(root, "a", "Desktop"), Destination = Path.Combine(root, "x") };
            var b = new SourceConfig { Path = Path.Combine(root, "b", "Desktop"), Destination = Path.Combine(root, "y") };
            var log = new RecordingLogger();
            var rc = await Program.RunDeferredAsync(DeferredCfg(Path.Combine(root, "tictack.log"), a, b), log, false, "--deferred-check", "Desktop");
            Assert.Equal(1, rc);
            Assert.Contains(log.Messages, m => m.Contains("Ambiguous")
                && m.Contains(Program.DeferredFilePrefix(a)) && m.Contains(Program.DeferredFilePrefix(b)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
