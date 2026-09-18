using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace TicTack;

// Admin-gated Windows tests. They are skipped unless TICTACK_ELEVATED_TESTS=1
// (set by tools/test-windows.ps1 -Elevated) AND the shell is elevated AND the
// TicTackSv EventLog source has been registered (the installer does this).
// They also need the Application channel to be readable: reading via the
// legacy EventLog.Entries enumerator uses the legacy RPC path and throws
// "RPC server is unavailable" on hosts where that is blocked, so if the
// channel cannot be opened the write is unobservable and the test skips.
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
        if (!Enabled || !SourceAvailable() || !ApplicationLogReadable()) return;

        var probe = "tictack-eventlog-probe-" + Guid.NewGuid().ToString("N");
        new EventLogLogger().Error(probe);

        Assert.True(WaitForApplicationLog(probe), "no TicTackSv Application entry for the probe");
    }

    [Fact]
    public void Service_OnStartFailure_WritesEventLog()
    {
        if (!Enabled || !SourceAvailable() || !ApplicationLogReadable()) return;

        var config = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yaml");
        var service = new ElevatedTestService(config);

        Assert.Throws<InvalidOperationException>(() => service.StartForTest());

        Assert.True(WaitForApplicationLog("OnStart failed"), "OnStart failure was not written to the Application log");
    }

    private static bool SourceAvailable()
    {
        try { return EventLog.SourceExists(Source); }
        catch { return false; }
    }

    // EventLogReader opens the Application channel directly (wevtapi) instead
    // of the legacy EventLog RPC enumerator, which fails on locked-down hosts.
    private static bool ApplicationLogReadable()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, "*"));
            reader.ReadEvent()?.Dispose();
            return true;
        }
        catch { return false; }
    }

    private static bool WaitForApplicationLog(string fragment, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (ApplicationLogHas(fragment)) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(200);
        }
    }

    private static bool ApplicationLogHas(string fragment)
    {
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[Provider[@Name='" + Source + "']]]");
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                if ((record.FormatDescription() ?? string.Empty)
                    .Contains(fragment, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private sealed class ElevatedTestService : TicTackService
    {
        public ElevatedTestService(string configPath) : base(configPath) { }
        public void StartForTest() => OnStart(Array.Empty<string>());
    }
}
