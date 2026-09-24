using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace TicTack.Tests;

public sealed class VolumeResolveTests
{
    static TicTackConfig CfgWith(string destination, string? command = null)
    {
        var cfg = new TicTackConfig();
        cfg.Sources.Add(new SourceConfig { Path = "/tmp/src", Destination = destination });
        if (command != null)
            cfg.Jobs.Add(new JobConfig { Command = command });
        return cfg;
    }

    static List<(string label, string root)> Vols(params (string label, string root)[] vs) => vs.ToList();

    [Fact]
    public void CommandResolution_SubstitutesKnownLabel()
    {
        var cfg = CfgWith("/tmp/dst", "[D]\\backup \"{source}\"");
        VolumeResolver.ResolveConfig(cfg, Vols(("D", "E:")));
        Assert.Equal("E:\\backup \"{source}\"", cfg.Jobs[0].Command);
    }

    [Fact]
    public void CommandResolution_LeavesShellBracketsAlone()
    {
        var cfg = CfgWith("/tmp/dst", "powershell $a[0] --out [ZZZ]");
        VolumeResolver.ResolveConfig(cfg, Vols(("D", "E:")));
        Assert.Equal("powershell $a[0] --out [ZZZ]", cfg.Jobs[0].Command);
    }

    [Fact]
    public void FindUnresolvedLabels_IgnoresCommandText()
    {
        var cfg = CfgWith("[Nope]\\x", "echo $a[0]");
        VolumeResolver.ResolveConfig(cfg, Vols(("D", "E:")));
        var missing = VolumeResolver.FindUnresolvedLabels(cfg);
        Assert.Equal(new[] { "Nope" }, missing);
    }

    [Fact]
    public void EnsureResolved_FalseOnZeroWaitWhenMissing()
    {
        var cfg = CfgWith("[NopeTestLabel]\\x");
        var log = new RecordingLogger();
        Assert.False(VolumeResolver.EnsureResolved(cfg, 0, log));
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:") && m.Contains("NopeTestLabel"));
    }

    [Fact]
    public void EnsureResolved_TrueWhenResolved()
    {
        var cfg = CfgWith("/tmp/dst");
        var log = new RecordingLogger();
        Assert.True(VolumeResolver.EnsureResolved(cfg, 0, log));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("ERR:"));
    }

    [Fact]
    public void VolumeWaitMinutes_DefaultIsTwo()
    {
        Assert.Equal(2, new TicTackConfig().VolumeWaitMinutes);
    }

    [Fact]
    public void VolumeWaitMinutes_YamlKeyMaps()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "volume_wait_minutes: 5\nsources: []\n");
            Assert.Equal(5, Config.Load(path)!.VolumeWaitMinutes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validator_RejectsNegativeVolumeWait()
    {
        var cfg = CfgWith(Path.GetTempPath(), null);
        cfg.VolumeWaitMinutes = -1;
        var log = new RecordingLogger();
        Assert.False(Config.Validate(cfg, log));
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:") && m.Contains("volume_wait_minutes"));
    }
}
