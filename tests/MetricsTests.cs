using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace TicTack;

public class MetricsTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _dstDir;
    private readonly FileAccessor _accessor = new();

    public MetricsTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "TicTackTest_metrics_" + Guid.NewGuid());
        _dstDir = Path.Combine(Path.GetTempPath(), "TicTackTest_metrics_dst_" + Guid.NewGuid());
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_srcDir, true); } catch { }
        try { Directory.Delete(_dstDir, true); } catch { }
    }

    private sealed class CounterCatcher : EventListener
    {
        private readonly ManualResetEventSlim _hit = new(false);
        private readonly string _want;
        private readonly Func<IDictionary<string, object>, bool> _predicate;

        public CounterCatcher(string want, Func<IDictionary<string, object>, bool> predicate)
        {
            _want = want;
            _predicate = predicate;
        }

        public IDictionary<string, object>? Payload { get; private set; }

        public bool Wait(int milliseconds = 15000) => _hit.Wait(milliseconds);

        public static bool AtLeast(IDictionary<string, object> d, string key, double min) =>
            d.TryGetValue(key, out var v) && v is IConvertible c && c.ToDouble(null) >= min;

        public string Dump() =>
            Payload == null ? "<no payload>" : string.Join(", ", Payload.Select(kv => kv.Key + "=" + kv.Value));

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "TicTack")
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "EventCounters" || eventData.Payload == null) return;
            foreach (var p in eventData.Payload)
            {
                if (p is IDictionary<string, object> d &&
                    d.TryGetValue("Name", out var n) && n?.ToString() == _want &&
                    _predicate(d))
                {
                    Payload = d;
                    _hit.Set();
                }
            }
        }
    }

    [Fact]
    public async Task Metrics_FileCopied_SurfacesAsCounter()
    {
        using var catcher = new CounterCatcher("files-copied", d => CounterCatcher.AtLeast(d, "Increment", 3));
        // The enable-time poll captures the counter's baseline; write after
        // that so the next interval reports our three increments.
        await Task.Delay(1200);
        for (int i = 0; i < 3; i++)
            TicTackEventSource.Log.FileCopied(1024);
        Assert.True(catcher.Wait(), "no files-copied increment >= 3 within 15s: " + catcher.Dump());
    }

    [Fact]
    public async Task Metrics_CopyAction_EmitsCopyTime()
    {
        using var catcher = new CounterCatcher("copy-time-ms", d => CounterCatcher.AtLeast(d, "Count", 1));
        File.WriteAllText(Path.Combine(_srcDir, "a.txt"), "metrics copy test");
        var action = new CopyAction(_accessor);
        var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, Path.Combine(_srcDir, "a.txt")), _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(catcher.Wait(), "no copy-time-ms sample within 15s: " + catcher.Dump());
    }

    [Fact]
    public async Task Metrics_CopyAction_EmitsFsyncTime()
    {
        using var catcher = new CounterCatcher("fsync-time-ms", d => CounterCatcher.AtLeast(d, "Count", 1));
        File.WriteAllText(Path.Combine(_srcDir, "b.txt"), "metrics fsync test");
        var action = new CopyAction(_accessor);
        var args = new FileActionArgs(new FileChangedEventArgs(ChangeType.Created, Path.Combine(_srcDir, "b.txt")), _srcDir, _dstDir);
        var result = await action.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(catcher.Wait(), "no fsync-time-ms sample within 15s: " + catcher.Dump());
    }

    // NoInlining so `owner` is unreachable once this returns; the only thing
    // that could keep it alive is the counter callback.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Token, WeakReference Owner) RegisterWithTemporaryOwner()
    {
        var owner = new object();
        var token = TicTackEventSource.Log.RegisterQueueCounter(() => owner.GetHashCode());
        return (token, new WeakReference(owner));
    }

    [Fact]
    public void Metrics_QueueCounterToken_ReleasesOwnerAfterDispose()
    {
        var (token, weak) = RegisterWithTemporaryOwner();
        token.Dispose();

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        Assert.False(weak.IsAlive, "queue-counter callback still roots its owner after token dispose");
    }
}
