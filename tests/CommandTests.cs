namespace TicTack;

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
}
