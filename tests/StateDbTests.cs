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
    public void UsnCheckpoint_RoundTrip()
    {
        Assert.Null(_db.GetUsnCheckpoint());
        _db.SetUsnCheckpoint(0xABCDEF0123456789UL, 9876543210L);
        var cp = _db.GetUsnCheckpoint();
        Assert.NotNull(cp);
        Assert.Equal(0xABCDEF0123456789UL, cp.Value.journalId);
        Assert.Equal(9876543210L, cp.Value.lastUsn);
        // Single row: overwrite, never a second row.
        _db.SetUsnCheckpoint(42UL, 7L);
        Assert.Equal((42UL, 7L), _db.GetUsnCheckpoint());
    }

    [Fact]
    public async Task AsyncUpsertLoadDelete_RoundTrip()
    {
        await _db.UpsertAsync("file.txt", 100, 1000);
        await _db.UpsertAsync("file2.txt", 200, 2000);

        var all = await _db.GetStatesAsync(new[] { "file.txt", "file2.txt" });
        Assert.Equal((100L, 1000L), all["file.txt"]);
        Assert.Equal((200L, 2000L), all["file2.txt"]);
        Assert.Equal(100L, await _db.GetSizeAsync("file.txt"));

        await _db.DeleteAsync("missing.txt");
        await _db.DeleteAsync("file.txt");
        await _db.DeleteAsync("file2.txt");

        Assert.Equal(0L, await _db.CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_RoundTripsAndOverwrites()
    {
        await _db.UpsertBatchAsync(new[]
        {
            (path: "file1.txt", size: 100L, mtime: 1000L),
            (path: "file2.txt", size: 200L, mtime: 2000L)
        });
        await _db.UpsertBatchAsync(new[]
        {
            (path: "file1.txt", size: 300L, mtime: 3000L)
        });

        var all = await _db.GetStatesAsync(new[] { "file1.txt", "file2.txt" });
        Assert.Equal(2, all.Count);
        Assert.Equal((300L, 3000L), all["file1.txt"]);
        Assert.Equal((200L, 2000L), all["file2.txt"]);
    }

    [Fact]
    public async Task Count_ReturnsCorrectNumber()
    {
        Assert.Equal(0L, await _db.CountAsync());
        await _db.UpsertAsync("a.txt", 1, 10);
        Assert.Equal(1L, await _db.CountAsync());
        await _db.UpsertAsync("b.txt", 2, 20);
        Assert.Equal(2L, await _db.CountAsync());
        await _db.DeleteAsync("a.txt");
        Assert.Equal(1L, await _db.CountAsync());
    }

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        await _db.UpsertAsync("a.txt", 1, 10);
        await _db.UpsertAsync("b.txt", 2, 20);

        _db.Clear();

        Assert.Equal(0L, await _db.CountAsync());
    }

    [Fact]
    public async Task CaseInsensitivePaths()
    {
        await _db.UpsertAsync("File.Txt", 100, 1000);
        await _db.UpsertAsync("FILE.txt", 200, 2000);
        // The table itself is case-sensitive (BINARY collation, correct for
        // Linux where these are two distinct files); the case-insensitivity
        // lives in the reader cache, which must still collapse to one key.
        Assert.Equal(2L, await _db.CountAsync());
        var all = await _db.GetStatesAsync(new[] { "File.Txt", "FILE.txt" });
        Assert.Single(all);
        Assert.True(all.ContainsKey("file.txt"));
    }

    [Fact]
    public async Task SqlInjection_DoesNotBreak()
    {
        await _db.UpsertAsync("'; DROP TABLE state; --", 999, 999);
        var all = await _db.GetStatesAsync(new[] { "'; DROP TABLE state; --" });
        Assert.Single(all);

        await _db.DeleteAsync("'; DROP TABLE state; --");
        Assert.Equal(0L, await _db.CountAsync());

        // Verify table still exists and works
        await _db.UpsertAsync("safe.txt", 1, 1);
        Assert.Equal(1L, await _db.CountAsync());
    }

    [Fact]
    public async Task UpdatePrefixAsync_RenamesSubtreeOnly()
    {
        var S = Path.DirectorySeparatorChar;
        await _db.UpsertAsync($"old{S}a.txt", 1, 10);
        await _db.UpsertAsync($"old{S}sub{S}b.txt", 2, 20);
        await _db.UpsertAsync($"other{S}c.txt", 3, 30);

        await _db.UpdatePrefixAsync("old", "new");

        var all = await _db.GetStatesAsync(new[] { $"new{S}a.txt", $"new{S}sub{S}b.txt", $"other{S}c.txt" });
        Assert.Equal(3, all.Count);
        Assert.Equal((1L, 10L), all[$"new{S}a.txt"]);
        Assert.Equal((2L, 20L), all[$"new{S}sub{S}b.txt"]);
        Assert.Equal((3L, 30L), all[$"other{S}c.txt"]);

        // Empty/null prefixes are a no-op, never a rewrite.
        await _db.UpdatePrefixAsync("", "new");
        await _db.UpdatePrefixAsync(null!, "new");
        Assert.Equal((1L, 10L), (await _db.GetStatesAsync(new[] { $"new{S}a.txt" }))[$"new{S}a.txt"]);
    }

    [Fact]
    public async Task UpdatePrefixAsync_WildcardChars_DoNotMatchUnrelatedRows()
    {
        var S = Path.DirectorySeparatorChar;
        await _db.UpsertAsync($"a_b{S}keep.txt", 1, 10);
        await _db.UpsertAsync($"aXb{S}keep.txt", 2, 20);
        await _db.UpsertAsync($"a%b{S}keep.txt", 3, 30);
        await _db.UpsertAsync($"aZZb{S}keep.txt", 4, 40);

        await _db.UpdatePrefixAsync("a_b", "new_under");
        await _db.UpdatePrefixAsync("a%b", "new_pct");

        var all = await _db.GetStatesAsync(new[] { $"new_under{S}keep.txt", $"new_pct{S}keep.txt", $"aXb{S}keep.txt", $"aZZb{S}keep.txt" });
        // '_' and '%' are LIKE metacharacters: without ESCAPE this rewrites
        // aXb/aZZb too (and can collide into a UNIQUE constraint failure).
        Assert.Equal((1L, 10L), all[$"new_under{S}keep.txt"]);
        Assert.Equal((3L, 30L), all[$"new_pct{S}keep.txt"]);
        Assert.Equal((2L, 20L), all[$"aXb{S}keep.txt"]);
        Assert.Equal((4L, 40L), all[$"aZZb{S}keep.txt"]);
    }

    [Fact]
    public async Task UpdatePrefixAsync_OntoExistingRows_ReplacesWithoutThrow()
    {
        var S = Path.DirectorySeparatorChar;
        await _db.UpsertAsync($"old{S}f.txt", 1, 10);
        await _db.UpsertAsync($"new{S}f.txt", 2, 20);

        await _db.UpdatePrefixAsync("old", "new");

        Assert.Equal(1L, await _db.CountAsync());
        Assert.Equal((1L, 10L), (await _db.GetStatesAsync(new[] { $"new{S}f.txt" }))[$"new{S}f.txt"]);
    }

    private void BreakConnection()
    {
        var field = typeof(StateDb).GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        ((IDisposable?)field.GetValue(_db))?.Dispose();
    }

    [Fact]
    public async Task Reconnect_AfterConnectionBreak_RoundTrips()
    {
        await _db.UpsertBatchAsync(new[] { ("keep.txt", 5L, 50L) });
        BreakConnection();

        await _db.UpsertBatchAsync(new[] { ("after.txt", 6L, 60L) });

        var all = await _db.GetStatesAsync(new[] { "keep.txt", "after.txt" });
        Assert.Equal((5L, 50L), all["keep.txt"]);
        Assert.Equal((6L, 60L), all["after.txt"]);
    }

    [Fact]
    public async Task ReconnectAsync_AfterConnectionBreak_RoundTrips()
    {
        await _db.UpsertAsync("keep.txt", 5, 50);
        BreakConnection();

        await _db.UpsertAsync("after.txt", 6, 60);

        var all = await _db.GetStatesAsync(new[] { "keep.txt", "after.txt" });
        Assert.Equal((5L, 50L), all["keep.txt"]);
        Assert.Equal((6L, 60L), all["after.txt"]);
    }

    [Fact]
    public async Task AfterDispose_OperationsThrowObjectDisposed()
    {
        _db.Dispose();
        _db.Dispose();

        var clearEx = Assert.Throws<ObjectDisposedException>(() => _db.Clear());
        Assert.Equal(nameof(StateDb), clearEx.ObjectName);
        var batchEx = await Assert.ThrowsAsync<ObjectDisposedException>(() => _db.UpsertBatchAsync(new[] { ("a.txt", 1L, 10L) }));
        Assert.Equal(nameof(StateDb), batchEx.ObjectName);
    }

    [Fact]
    public async Task AfterDispose_AsyncOperationThrowsObjectDisposed()
    {
        _db.Dispose();

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => _db.GetStatesAsync(new[] { "a.txt" }));
        Assert.Equal(nameof(StateDb), ex.ObjectName);
    }
}
