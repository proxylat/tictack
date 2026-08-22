namespace TicTack;

public class StateDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly StateDb _db;

    public StateDbTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TicTackTest_sdb_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
        _db = new StateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void UpsertAndLoadAll_RoundTrip()
    {
        _db.Upsert("file1.txt", 100, 1234567890);
        _db.Upsert("file2.txt", 200, 1234567891);

        var all = _db.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal((100L, 1234567890L), all["file1.txt"]);
        Assert.Equal((200L, 1234567891L), all["file2.txt"]);
    }

    [Fact]
    public void Upsert_OverwritesExisting()
    {
        _db.Upsert("file.txt", 100, 1000);
        _db.Upsert("file.txt", 200, 2000);

        var all = _db.LoadAll();
        Assert.Single(all);
        Assert.Equal((200L, 2000L), all["file.txt"]);
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        _db.Upsert("file.txt", 100, 1000);
        _db.Delete("file.txt");

        var all = _db.LoadAll();
        Assert.Empty(all);
    }

    [Fact]
    public void Delete_NonExistent_DoesNotThrow()
    {
        _db.Delete("nonexistent.txt");
    }

    [Fact]
    public void Count_ReturnsCorrectNumber()
    {
        Assert.Equal(0, _db.Count());
        _db.Upsert("a.txt", 1, 10);
        Assert.Equal(1, _db.Count());
        _db.Upsert("b.txt", 2, 20);
        Assert.Equal(2, _db.Count());
        _db.Delete("a.txt");
        Assert.Equal(1, _db.Count());
    }

    [Fact]
    public void LoadAll_ReturnsEmpty_WhenNoEntries()
    {
        var all = _db.LoadAll();
        Assert.Empty(all);
    }

    [Fact]
    public void CaseInsensitivePaths()
    {
        _db.Upsert("File.Txt", 100, 1000);
        var all = _db.LoadAll();
        Assert.True(all.ContainsKey("FILE.TXT"));
        Assert.True(all.ContainsKey("file.txt"));
    }

    [Fact]
    public void ConcurrentAccess_DoesNotCorrupt()
    {
        var tasks = new Task[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(() =>
            {
                _db.Upsert("file" + idx + ".txt", idx, idx);
            });
        }
        Task.WaitAll(tasks);

        var all = _db.LoadAll();
        Assert.Equal(10, all.Count);
    }

    [Fact]
    public void SqlInjection_DoesNotBreak()
    {
        _db.Upsert("'; DROP TABLE state; --", 999, 999);
        var all = _db.LoadAll();
        Assert.Single(all);

        _db.Delete("'; DROP TABLE state; --");
        Assert.Empty(_db.LoadAll());

        // Verify table still exists and works
        _db.Upsert("safe.txt", 1, 1);
        Assert.Equal(1, _db.Count());
    }
}
