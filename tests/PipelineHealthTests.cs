namespace TicTack;

// Pure health-rule tests for the service watchdog: EvaluateHealth decides,
// CheckHealth only gathers live state. No pipeline, no timers, Linux-safe.
public sealed class PipelineHealthTests
{
    private static readonly DateTime Now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HealthyIdle_ReturnsNull()
    {
        Assert.Null(SyncPipeline.EvaluateHealth(0, Now.AddMinutes(-60), Now,
            TimeSpan.FromMinutes(60), null));
    }

    [Fact]
    public void FaultedProcessor_ReturnsFaultWithCause()
    {
        var fault = SyncPipeline.EvaluateHealth(0, Now, Now,
            TimeSpan.FromMinutes(60), new InvalidOperationException("boom"));
        Assert.NotNull(fault);
        Assert.Contains("boom", fault);
    }

    [Fact]
    public void StalledQueue_ReturnsFaultWithCounts()
    {
        var fault = SyncPipeline.EvaluateHealth(5, Now.AddHours(-2), Now,
            TimeSpan.FromHours(1), null);
        Assert.NotNull(fault);
        Assert.Contains("5", fault);
    }

    [Fact]
    public void FreshProgress_ReturnsNull()
    {
        Assert.Null(SyncPipeline.EvaluateHealth(3, Now.AddSeconds(-10), Now,
            TimeSpan.FromHours(1), null));
    }

    [Fact]
    public void ExactlyAtStallThreshold_ReturnsNull()
    {
        // Strictly-overdue only: a queue that just hit the boundary is the
        // debounce window settling, not a wedge.
        Assert.Null(SyncPipeline.EvaluateHealth(2, Now.AddHours(-1), Now,
            TimeSpan.FromHours(1), null));
    }

    [Fact]
    public void FaultBeatsStall_Ordering()
    {
        // A dead processor with a stale queue reports the death, not the stall.
        var fault = SyncPipeline.EvaluateHealth(9, Now.AddHours(-5), Now,
            TimeSpan.FromHours(1), new InvalidOperationException("dead"));
        Assert.NotNull(fault);
        Assert.Contains("dead", fault);
    }
}
