namespace TicTack;

public class ChurnTests : IDisposable
{
    private readonly string _dir;
    private readonly string _marker;
    private readonly RecordingLogger _log = new();
    private DateTime _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public ChurnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TicTackTest_churn_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _marker = Path.Combine(_dir, ".tictack-churn-hold");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    ChurnMonitor Make(long bytes = 0, int files = 0, int renames = 0, Func<bool>? exempt = null) =>
        new ChurnMonitor(_marker, TimeSpan.FromHours(1), bytes, files, renames, _log,
            clock: () => _now, isExempt: exempt);

    [Fact]
    public void TripOnFiles_WritesMarkerAndHolds()
    {
        var churn = Make(files: 3);
        churn.ReportCopy(10);
        churn.ReportCopy(10);
        Assert.False(churn.IsHeld());
        churn.ReportCopy(10);
        Assert.True(churn.IsHeld());
        Assert.True(File.Exists(_marker));
        Assert.Contains(_log.Messages, m => m.Contains("Spike guard tripped"));
    }

    [Fact]
    public void OldEvents_PruneOutOfWindow()
    {
        var churn = Make(files: 3);
        churn.ReportCopy(10);
        churn.ReportCopy(10);
        _now += TimeSpan.FromHours(2);
        churn.ReportCopy(10);
        Assert.False(churn.IsHeld());
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void MarkerDeletedExternally_ClearsAndResumes()
    {
        var churn = Make(files: 2);
        churn.ReportCopy(10);
        churn.ReportCopy(10);
        Assert.True(churn.IsHeld());
        File.Delete(_marker);
        Assert.False(churn.IsHeld());
        Assert.Contains(_log.Messages, m => m.Contains("Spike hold cleared, resuming"));
        // Window was reset on clear: two more reports must not re-trip.
        churn.ReportCopy(10);
        Assert.False(churn.IsHeld());
    }

    [Fact]
    public void Exempt_ReportsAreNoops()
    {
        var churn = Make(files: 1, exempt: () => true);
        churn.ReportCopy(10);
        Assert.False(churn.IsHeld());
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void DisabledWires_NeverTrip()
    {
        var churn = Make();
        for (int i = 0; i < 100; i++) churn.ReportCopy(1_000_000);
        Assert.False(churn.IsHeld());
    }
}
