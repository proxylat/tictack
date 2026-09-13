namespace TicTack;

public class VolumeResolverTests
{
    [Fact]
    public void Resolve_ReplacesVolumeLabelCaseInsensitively()
    {
        var result = VolumeResolver.Resolve("[Backup]\\folder\\file.txt", new[]
        {
            (label: "backup", root: "D:")
        });

        Assert.Equal("D:\\folder\\file.txt", result);
    }

    [Fact]
    public void Resolve_LeavesUnknownLabelLiteral()
    {
        var path = "[Missing]\\file.txt";

        Assert.Equal(path, VolumeResolver.Resolve(path, Array.Empty<(string label, string root)>()));
    }

    [Fact]
    public void ResolveConfig_ResolvesAllDocumentedPathFields()
    {
        var cfg = new TicTackConfig
        {
            Sources = new List<SourceConfig>
            {
                new SourceConfig
                {
                    Path = "[SRC]\\input",
                    Paths = new List<string> { "[SRC]\\one", "[SRC]\\two" },
                    Destination = "[DST]\\output",
                    StateDbPath = "[DST]\\state",
                    Sync = new SyncConfig
                    {
                        Versioning = new VersioningConfig { Path = "[DST]\\versions" },
                        Deletion = new DeletionConfig { Path = "[DST]\\archive" }
                    }
                }
            },
            Logging = new LoggingConfig { Path = "[DST]\\log.txt", AlertPath = "[DST]\\alerts" },
            Jobs = new List<JobConfig> { new JobConfig { WorkingDir = "[SRC]\\jobs" } },
            ExternalDrives = new ExternalDrivesConfig { WorkingDir = "[DST]\\external" }
        };
        var volumes = new[]
        {
            (label: "SRC", root: "C:"),
            (label: "DST", root: "D:")
        };

        VolumeResolver.ResolveConfig(cfg, volumes);

        var src = cfg.Sources[0];
        Assert.Equal("C:\\input", src.Path);
        Assert.Equal(new[] { "C:\\one", "C:\\two" }, src.Paths);
        Assert.Equal("D:\\output", src.Destination);
        Assert.Equal("D:\\state", src.StateDbPath);
        Assert.Equal("D:\\versions", src.Sync.Versioning!.Path);
        Assert.Equal("D:\\archive", src.Sync.Deletion!.Path);
        Assert.Equal("D:\\log.txt", cfg.Logging.Path);
        Assert.Equal("D:\\alerts", cfg.Logging.AlertPath);
        Assert.Equal("C:\\jobs", cfg.Jobs[0].WorkingDir);
        Assert.Equal("D:\\external", cfg.ExternalDrives.WorkingDir);
    }
}
