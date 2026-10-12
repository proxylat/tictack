using TicTack;

namespace Corpus;

public sealed class CorpusConfigTests : IDisposable
{
    private readonly string _dir;

    public CorpusConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_cfg_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteYaml(string body)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".yaml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(CorpusConfig.Load(Path.Combine(_dir, "nope.yaml")));
    }

    [Fact]
    public void Load_MinimalYaml_AppliesDefaults()
    {
        var cfg = CorpusConfig.Load(WriteYaml("store_path: /data/store\n"));
        Assert.NotNull(cfg);
        Assert.Equal(4, cfg!.ChunkMib);
        Assert.Equal(3, cfg.Compression.Level);
        Assert.Equal("info", cfg.Logging.Level);
        Assert.True(CorpusConfig.Validate(cfg, new TestLogger()));
    }

    [Fact]
    public void Validate_EmptyStorePath_Fails()
    {
        var log = new TestLogger();
        Assert.False(CorpusConfig.Validate(new CorpusConfig(), log));
        Assert.Contains(log.Messages, m => m.Contains("store_path"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Validate_BadChunkMib_Fails(int chunkMib)
    {
        var cfg = new CorpusConfig { StorePath = "/data/store", ChunkMib = chunkMib };
        Assert.False(CorpusConfig.Validate(cfg, new TestLogger()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void Validate_BadCompressionLevel_Fails(int level)
    {
        var cfg = new CorpusConfig { StorePath = "/data/store" };
        cfg.Compression.Level = level;
        Assert.False(CorpusConfig.Validate(cfg, new TestLogger()));
    }

    [Fact]
    public void StateRoot_ExplicitPath_Wins()
    {
        var cfg = new CorpusConfig { StateDbPath = "/custom/root" };
        Assert.Equal("/custom/root", CorpusConfig.StateRoot(cfg));
    }

    [Fact]
    public void StateRoot_Empty_UsesPlatformDefault()
    {
        var root = CorpusConfig.StateRoot(new CorpusConfig());
        if (OperatingSystem.IsWindows())
            Assert.EndsWith(Path.Combine("Corpus"), root);
        else
            Assert.Equal("/var/lib/corpus", root);
    }

    [Fact]
    public void ChunkIndexSchema_Open_CreatesChunksTable()
    {
        var db = Path.Combine(_dir, "chunks.db");
        using (var conn = SqliteBootstrap.Open(db, SqliteSchema.Corpus))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='chunks'";
            Assert.Equal("chunks", cmd.ExecuteScalar());
        }
    }
}
