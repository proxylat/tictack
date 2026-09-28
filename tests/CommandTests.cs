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
}
