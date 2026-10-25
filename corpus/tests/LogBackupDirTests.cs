using Xunit;

namespace Corpus.Tests;

public sealed class LogBackupDirTests
{
    private static CorpusConfig Config(bool backup = true) =>
        new() { Logging = new CorpusLoggingConfig { Backup = backup } };

    [Fact]
    public void Target_NullCfg_Null()
    {
        Assert.Null(CorpusConfig.LogBackupTargetDir(null, "a.vhdx", null, null));
    }

    [Fact]
    public void Target_KnobOff_Null()
    {
        Assert.Null(CorpusConfig.LogBackupTargetDir(Config(false), "a.vhdx", null, null));
    }

    [Fact]
    public void Target_StoreLane_Null()
    {
        Assert.Null(CorpusConfig.LogBackupTargetDir(Config(), null, null, null));
    }

    [Fact]
    public void Target_SingleFile_OutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = CorpusConfig.LogBackupTargetDir(Config(), Path.Combine(dir, "b.vhdx"), null, null);
        Assert.Equal(dir, target);
    }

    [Fact]
    public void Target_FirstOutputWins()
    {
        var target = CorpusConfig.LogBackupTargetDir(Config(), "a.vhdx", "b.wim", "c.esd");
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath("a.vhdx")), target);
    }

    [Fact]
    public void Resolve_KnobOff_Null()
    {
        Assert.Null(CorpusConfig.ResolveLogBackupDir(Config(false), "a.vhdx", null, null, "/state"));
    }

    [Fact]
    public void Resolve_WritableTarget_Target()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "b.vhdx");
            Assert.Equal(dir, CorpusConfig.ResolveLogBackupDir(Config(), target, null, null, "/state"));
            Assert.Empty(Directory.GetFiles(dir)); // probe leaves no litter
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resolve_MissingTargetDir_FallsBackToStateRoot()
    {
        var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "b.vhdx");
        Assert.Equal("/state", CorpusConfig.ResolveLogBackupDir(Config(), target, null, null, "/state"));
    }
}
