using System.Reflection;

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
    public async Task AsyncUpsertLoadDelete_RoundTrip()
    {
        await _db.UpsertAsync("file.txt", 100, 1000);

        var all = await _db.LoadAllAsync();
        Assert.Equal((100L, 1000L), all["file.txt"]);
        Assert.Equal(100L, await _db.GetSizeAsync("file.txt"));

        await _db.DeleteAsync("file.txt");

        Assert.Empty(await _db.LoadAllAsync());
    }

    [Fact]
    public void UpsertBatch_RoundTripsAndOverwrites()
    {
        _db.UpsertBatch(new[]
        {
            (path: "file1.txt", size: 100L, mtime: 1000L),
            (path: "file2.txt", size: 200L, mtime: 2000L)
        });
        _db.UpsertBatch(new[]
        {
            (path: "file1.txt", size: 300L, mtime: 3000L)
        });

        var all = _db.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal((300L, 3000L), all["file1.txt"]);
        Assert.Equal((200L, 2000L), all["file2.txt"]);
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
    public void Clear_RemovesAllEntries()
    {
        _db.Upsert("a.txt", 1, 10);
        _db.Upsert("b.txt", 2, 20);

        _db.Clear();

        Assert.Empty(_db.LoadAll());
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
        _db.Upsert("FILE.txt", 200, 2000);
        // The table itself is case-sensitive (BINARY collation, correct for
        // Linux where these are two distinct files); the case-insensitivity
        // lives in the LoadAll cache, which must still collapse to one key.
        Assert.Equal(2, _db.Count());
        var all = _db.LoadAll();
        Assert.Single(all);
        Assert.True(all.ContainsKey("file.txt"));
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

    [Fact]
    public void UpdatePrefix_RenamesSubtreeOnly()
    {
        _db.Upsert("old/a.txt", 1, 10);
        _db.Upsert("old/sub/b.txt", 2, 20);
        _db.Upsert("other/c.txt", 3, 30);

        _db.UpdatePrefix("old", "new");

        var all = _db.LoadAll();
        Assert.Equal(3, all.Count);
        Assert.Equal((1L, 10L), all["new/a.txt"]);
        Assert.Equal((2L, 20L), all["new/sub/b.txt"]);
        Assert.Equal((3L, 30L), all["other/c.txt"]);
    }

    [Fact]
    public void UpdatePrefix_EmptyPrefix_IsNoOp()
    {
        _db.Upsert("a.txt", 1, 10);

        _db.UpdatePrefix("", "new");
        _db.UpdatePrefix(null!, "new");

        Assert.Equal((1L, 10L), _db.LoadAll()["a.txt"]);
    }

    [Fact]
    public async Task UpdatePrefixAsync_RenamesSubtreeOnly()
    {
        await _db.UpsertAsync("old/a.txt", 1, 10);
        await _db.UpsertAsync("old/sub/b.txt", 2, 20);
        await _db.UpsertAsync("other/c.txt", 3, 30);

        await _db.UpdatePrefixAsync("old", "new");

        var all = await _db.LoadAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal((1L, 10L), all["new/a.txt"]);
        Assert.Equal((2L, 20L), all["new/sub/b.txt"]);
        Assert.Equal((3L, 30L), all["other/c.txt"]);
    }

    private void BreakConnection()
    {
        var field = typeof(StateDb).GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        ((IDisposable?)field.GetValue(_db))?.Dispose();
    }

    [Fact]
    public void Reconnect_AfterConnectionBreak_RoundTrips()
    {
        _db.Upsert("keep.txt", 5, 50);
        BreakConnection();

        _db.Upsert("after.txt", 6, 60);

        var all = _db.LoadAll();
        Assert.Equal((5L, 50L), all["keep.txt"]);
        Assert.Equal((6L, 60L), all["after.txt"]);
    }

    [Fact]
    public async Task ReconnectAsync_AfterConnectionBreak_RoundTrips()
    {
        await _db.UpsertAsync("keep.txt", 5, 50);
        BreakConnection();

        await _db.UpsertAsync("after.txt", 6, 60);

        var all = await _db.LoadAllAsync();
        Assert.Equal((5L, 50L), all["keep.txt"]);
        Assert.Equal((6L, 60L), all["after.txt"]);
    }
}
