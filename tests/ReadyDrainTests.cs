using System;
using System.Collections.Concurrent;
using Xunit;

namespace TicTack;

public class ReadyDrainTests
{
    static FileChangedEventArgs Ev(string path) => new FileChangedEventArgs(ChangeType.Modified, path);

    static (ConcurrentDictionary<string, FileChangedEventArgs> pending, ConcurrentDictionary<string, DateTime> debounce) Dicts()
    {
        return (new ConcurrentDictionary<string, FileChangedEventArgs>(StringComparer.Ordinal),
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
    }

    [Fact]
    public void DisabledMode_ScanTakesReadyEntry()
    {
        var drain = new ReadyDrain(false);
        var (pending, debounce) = Dicts();
        pending["a"] = Ev("a");
        Assert.True(drain.TryTake(DateTime.UtcNow, pending, debounce, out var result, out _));
        Assert.Equal("a", result.FullPath);
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void Enabled_HeapTakesExpiredEntry()
    {
        var drain = new ReadyDrain(true);
        var (pending, debounce) = Dicts();
        pending["a"] = Ev("a");
        drain.Signal("a", DateTime.UtcNow.AddSeconds(-1));
        Assert.True(drain.TryTake(DateTime.UtcNow, pending, debounce, out var result, out _));
        Assert.Equal("a", result.FullPath);
    }

    [Fact]
    public void Enabled_UnexpiredEntryReturnsWait()
    {
        var drain = new ReadyDrain(true);
        var (pending, debounce) = Dicts();
        pending["a"] = Ev("a");
        var until = DateTime.UtcNow.AddSeconds(30);
        debounce["a"] = until;
        drain.Signal("a", until);
        Assert.False(drain.TryTake(DateTime.UtcNow, pending, debounce, out _, out var wait));
        Assert.True(wait.HasValue);
        Assert.True(pending.ContainsKey("a"));
    }

    [Fact]
    public void Enabled_StaleHeapEntrySkipped()
    {
        var drain = new ReadyDrain(true);
        var (pending, debounce) = Dicts();
        drain.Signal("gone", DateTime.UtcNow.AddSeconds(-1));
        Assert.False(drain.TryTake(DateTime.UtcNow, pending, debounce, out _, out _));
    }
}
