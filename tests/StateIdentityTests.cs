using Microsoft.Data.Sqlite;

namespace TicTack;

// Schema migration (#6/#2 foundation) + file-identity round-trips.
// Old 3-column DBs must gain file_id/ctime/hash with rows intact; the
// sticky upsert must never wipe stored identity via a 3-arg write.
public class StateIdentityTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _dbPaths = new List<string>();
    private readonly List<StateDb> _open = new List<StateDb>();

    public StateIdentityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TicTackTest_sid_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var db in _open) try { db.Dispose(); } catch { }
        foreach (var p in _dbPaths) try { File.Delete(p); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewDbPath(string name)
    {
        var p = Path.Combine(_dir, name + ".db");
        _dbPaths.Add(p);
        return p;
    }

    private StateDb Open(string path)
    {
        var db = new StateDb(path);
        _open.Add(db);
        return db;
    }

    private static void CreateOldSchema(string dbPath)
    {
        using var conn = new SqliteConnection("Data Source=" + dbPath);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE state (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO state (path, size, mtime) VALUES ('old.txt', 100, 1000)";
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Migration_OldDatabase_KeepsRowsAndGainsColumns()
    {
        var path = NewDbPath("mig");
        CreateOldSchema(path);

        var db = Open(path);
        var all = db.LoadAll();
        Assert.Single(all);
        Assert.Equal((100L, 1000L), all["old.txt"]);
    }

    [Fact]
    public async Task Identity_RoundTripsThroughUpsert()
    {
        var db = Open(NewDbPath("id"));
        await db.UpsertAsync("a.txt", 100, 1000, "W0000000000000001", 2000);

        var full = await db.GetFullStateAsync("a.txt");
        Assert.NotNull(full);
        Assert.Equal((100L, 1000L, "W0000000000000001", 2000L, (string?)null), full.Value);

        var hits = await db.FindByFileIdAsync("W0000000000000001");
        Assert.Single(hits);
        Assert.Equal("a.txt", hits[0].path);
    }

    [Fact]
    public async Task PlainUpsert_NeverWipesStoredIdentity()
    {
        var db = Open(NewDbPath("sticky"));
        await db.UpsertAsync("a.txt", 100, 1000, "Wabc", 2000, "deadbeef");
        await db.UpsertAsync("a.txt", 150, 1500);

        var full = await db.GetFullStateAsync("a.txt");
        Assert.NotNull(full);
        Assert.Equal((150L, 1500L, "Wabc", 2000L, "deadbeef"), full.Value);
    }

    [Fact]
    public async Task UpdateIdentity_OverwritesUnconditionally()
    {
        var db = Open(NewDbPath("overwrite"));
        await db.UpsertAsync("a.txt", 100, 1000, "Wold", 2000);
        await db.UpdateIdentityAsync("a.txt", "Wnew", 3000);

        var full = await db.GetFullStateAsync("a.txt");
        Assert.NotNull(full);
        Assert.Equal("Wnew", full.Value.fileId);
        Assert.Equal(3000L, full.Value.ctime);
    }

    [Fact]
    public async Task UpdateHash_ThenFullState_ReturnsHash()
    {
        var db = Open(NewDbPath("hash"));
        await db.UpsertAsync("a.txt", 100, 1000);
        await db.UpdateHashAsync("a.txt", "abc123");

        var full = await db.GetFullStateAsync("a.txt");
        Assert.NotNull(full);
        Assert.Equal("abc123", full.Value.hash);
        Assert.Null((await db.GetFullStateAsync("missing.txt")));
    }

    [Fact]
    public void FileIdentity_TempFile_IsStable()
    {
        var file = Path.Combine(_dir, "ident.txt");
        File.WriteAllText(file, "identity");

        Assert.True(FileIdentity.TryGet(file, out var id1, out var ctime1));
        Assert.False(string.IsNullOrEmpty(id1));
        Assert.True(ctime1 > 0);
        Assert.True(FileIdentity.TryGet(file, out var id2, out _));
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void FileIdentity_MissingFile_ReturnsFalse()
    {
        Assert.False(FileIdentity.TryGet(Path.Combine(_dir, "nope.txt"), out _, out _));
    }
}
