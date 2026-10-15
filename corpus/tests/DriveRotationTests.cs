namespace Corpus;

// Item 8 tests: offline-drive rotation. Drive ids are file/VHD stand-in paths
// (the store never touches the drives themselves — registration + dates only).
public sealed class DriveRotationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storeRoot;

    public DriveRotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_rotation_" + Guid.NewGuid());
        _storeRoot = Path.Combine(_dir, "store");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Standin(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0x56, 0x48, 0x44 }); // VHD stand-in bytes
        return path;
    }

    [Fact]
    public void Check_HealthyRotation_Returns0()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new DriveRotationStore(_storeRoot);
        var a = Standin("a.vhdx");
        var b = Standin("b.vhdx");
        var c = Standin("c.vhdx");
        store.Register(a, "OFFSITE-A");
        store.Register(b, "OFFSITE-B");
        store.Register(c, "OFFSITE-C");
        store.MarkSeen(a, backedUp: true, now); // fresh
        store.MarkSeen(b, backedUp: true, now.AddDays(-100)); // deep (predates dwell)
        store.MarkSeen(b, backedUp: false, now); // seen recently, old backup kept
        store.MarkSeen(c, backedUp: true, now.AddDays(-3));

        Assert.Equal(0, DriveRotationCheck.Check(_storeRoot, new TestLogger(), now));
    }

    [Fact]
    public void Check_FewerThanThree_Nags()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new DriveRotationStore(_storeRoot);
        var a = Standin("a.vhdx");
        var b = Standin("b.vhdx");
        store.Register(a, "A");
        store.Register(b, "B");
        store.MarkSeen(a, backedUp: true, now);
        store.MarkSeen(b, backedUp: true, now.AddDays(-100));

        var log = new TestLogger();
        Assert.Equal(1, DriveRotationCheck.Check(_storeRoot, log, now));
        Assert.Contains("3+", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Check_Overdue_Nags()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new DriveRotationStore(_storeRoot);
        var a = Standin("a.vhdx");
        var b = Standin("b.vhdx");
        var c = Standin("c.vhdx");
        store.Register(a, "A");
        store.Register(b, "B");
        store.Register(c, "C");
        store.MarkSeen(a, backedUp: true, now);
        store.MarkSeen(b, backedUp: true, now.AddDays(-100));
        store.MarkSeen(c, backedUp: false, now.AddDays(-30)); // stale, no backup

        var log = new TestLogger();
        Assert.Equal(1, DriveRotationCheck.Check(_storeRoot, log, now));
        Assert.Contains("unseen", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Check_NoDeepCopy_Nags()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var store = new DriveRotationStore(_storeRoot);
        foreach (var name in new[] { "a.vhdx", "b.vhdx", "c.vhdx" })
        {
            var id = Standin(name);
            store.Register(id, name);
            store.MarkSeen(id, backedUp: true, now.AddDays(-1)); // all fresh, none deep
        }

        var log = new TestLogger();
        Assert.Equal(1, DriveRotationCheck.Check(_storeRoot, log, now));
        Assert.Contains("90d", string.Join("\n", log.Messages));
    }

    [Fact]
    public void Check_MissingState_Nags()
    {
        var log = new TestLogger();
        Assert.Equal(1, DriveRotationCheck.Check(_storeRoot, log));
        Assert.Contains("no drives registered", string.Join("\n", log.Messages));
    }

    [Fact]
    public void MarkSeen_UnknownDrive_Throws()
    {
        var store = new DriveRotationStore(_storeRoot);
        Assert.Throws<InvalidDataException>(() => store.MarkSeen("ghost", backedUp: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Register_RoundTrips_WithoutTempLeftovers()
    {
        var store = new DriveRotationStore(_storeRoot);
        var id = Standin("a.vhdx");
        store.Register(id, "A");
        store.Register(id, "A-relabeled"); // re-register keeps one row, updates label

        var state = store.Load();
        Assert.Single(state.Drives);
        Assert.Equal("A-relabeled", state.Drives[0].Label);
        Assert.False(File.Exists(Path.Combine(_storeRoot, "rotation.json.tmp")));
    }
}
