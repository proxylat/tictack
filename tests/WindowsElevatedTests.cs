using System.Diagnostics;

namespace TicTack;

// Admin-gated Windows tests. They are skipped unless TICTACK_ELEVATED_TESTS=1
// (set by tools/test-windows.ps1 -Elevated) AND the shell is elevated AND the
// TicTackSv EventLog source has been registered (the installer does this).
[Trait("Category", "Windows")]
[Trait("Category", "Elevated")]
public sealed class WindowsElevatedTests
{
    private const string Source = "TicTackSv";

    private static bool Enabled =>
        OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TICTACK_ELEVATED_TESTS") == "1";

    [Fact]
    public void EventLogLogger_WritesToTicTackSvSource()
    {
        if (!Enabled || !SourceAvailable()) return;

        var probe = "tictack-eventlog-probe-" + Guid.NewGuid().ToString("N");
        new EventLogLogger().Error(probe);

        Assert.True(ApplicationLogHas(probe), "no TicTackSv Application entry for the probe");
    }

    [Fact]
    public void Service_OnStartFailure_WritesEventLog()
    {
        if (!Enabled || !SourceAvailable()) return;

        var config = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yaml");
        var service = new ElevatedTestService(config);

        Assert.Throws<InvalidOperationException>(() => service.StartForTest());

        Assert.True(ApplicationLogHas("OnStart failed"), "OnStart failure was not written to the Application log");
    }

    private static bool SourceAvailable()
    {
        try { return EventLog.SourceExists(Source); }
        catch { return false; }
    }

    private static bool ApplicationLogHas(string fragment)
    {
        using var log = new EventLog("Application");
        return log.Entries.Cast<EventLogEntry>().Any(e =>
            string.Equals(e.Source, Source, StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains(fragment, StringComparison.Ordinal));
    }

    private sealed class ElevatedTestService : TicTackService
    {
        public ElevatedTestService(string configPath) : base(configPath) { }
        public void StartForTest() => OnStart(Array.Empty<string>());
    }
}
