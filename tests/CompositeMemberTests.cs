namespace TicTack;

public class CompositeMemberTests
{
    private static MonitorConfig Cfg(bool backstop, bool usn) =>
        new MonitorConfig { Type = "composite", PollingBackstop = backstop, Usn = usn };

    private static void DisposeAll(List<IFileMonitor> members)
    {
        foreach (var m in members) m.Dispose();
    }

    [Fact]
    public void BackstopOn_WithUsn_KeepsAllThreeMembers()
    {
        // UsnJournalMonitor ctor is pure (no OS calls until Start), so the
        // real type doubles as the fake: DescribeMembers keys off it.
        // Owned by members (DisposeAll): no separate using (double dispose).
        var usn = new UsnJournalMonitor("/fake");
        var members = Program.BuildCompositeMembers(Cfg(true, true), "/fake", () => new EventMonitor(), () => usn);
        try
        {
            Assert.Equal(3, members.Count);
            Assert.IsType<PollingMonitor>(members[2]);
            Assert.Same(usn, members[1]);
            Assert.Equal("watcher + usn + polling every 3600s",
                Program.DescribeMembers(members, Cfg(true, true)));
        }
        finally { DisposeAll(members); }
    }

    [Fact]
    public void BackstopOff_WithUsn_DropsPollingMember()
    {
        var usn = new UsnJournalMonitor("/fake");
        var members = Program.BuildCompositeMembers(Cfg(false, true), "/fake", () => new EventMonitor(), () => usn);
        try
        {
            Assert.Equal(2, members.Count);
            Assert.Same(usn, members[1]);
            Assert.DoesNotContain(members, m => m is PollingMonitor);
            Assert.Equal("watcher + usn", Program.DescribeMembers(members, Cfg(false, true)));
        }
        finally { DisposeAll(members); }
    }

    [Fact]
    public void BackstopOff_WithoutUsn_KeepsPollingAsBackstop()
    {
        var members = Program.BuildCompositeMembers(Cfg(false, true), "/fake", () => new EventMonitor(), () => null);
        try
        {
            Assert.Equal(2, members.Count);
            Assert.IsType<PollingMonitor>(members[1]);
        }
        finally { DisposeAll(members); }
    }

    [Fact]
    public void BackstopOff_UsnDisabled_KeepsPollingAsBackstop()
    {
        var members = Program.BuildCompositeMembers(Cfg(false, false), "/fake", () => new EventMonitor(), () => throw new InvalidOperationException("must not probe"));
        try
        {
            Assert.Equal(2, members.Count);
            Assert.IsType<PollingMonitor>(members[1]);
        }
        finally { DisposeAll(members); }
    }

    [Fact]
    public void BackstopOn_WithoutUsn_KeepsWatcherAndPolling()
    {
        var members = Program.BuildCompositeMembers(Cfg(true, false), "/fake", () => new EventMonitor(), () => null);
        try
        {
            Assert.Equal(2, members.Count);
            Assert.IsType<PollingMonitor>(members[1]);
        }
        finally { DisposeAll(members); }
    }
}
