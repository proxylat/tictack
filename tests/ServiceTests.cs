namespace TicTack;

[Collection("SerialWatcher")]
public class ServiceTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public void OnStart_RejectsMissingConfigOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new TestService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".yaml"));
        Assert.Throws<InvalidOperationException>(() => service.StartForTest());
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnStartAndStop_UsesRealPipelineOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "tictack-service-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        var config = Path.Combine(root, "config.yaml");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "service.txt"), "service");
        File.WriteAllText(config, "sources:\n  - path: '" + source + "'\n    destination: '" + destination + "'\nlogging:\n  path: '" + Path.Combine(root, "tictack.log") + "'\n");

        try
        {
            var service = new TestService(config);
            service.StartForTest();
            service.StopForTest();
            Assert.True(File.Exists(Path.Combine(destination, "service.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class TestService : TicTackService
    {
        public TestService(string configPath) : base(configPath) { }
        public void StartForTest() => OnStart(Array.Empty<string>());
        public void StopForTest() => OnStop();
    }
}
