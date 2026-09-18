using Microsoft.Data.Sqlite;

namespace TicTack;

[Trait("Category", "Unit")]
public sealed class SqliteBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tictack-sqlite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private string DbPath(string name) => Path.Combine(_root, "nested", name + ".db");

    [Fact]
    public void Open_CreatesMissingDirectoryAndSchema()
    {
        var path = DbPath("state");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        using var conn = SqliteBootstrap.Open(path, SqliteSchema.State);

        Assert.True(File.Exists(path));
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state'"));
        Assert.Equal("wal", Scalar(conn, "PRAGMA journal_mode") as string, ignoreCase: true);
    }

    [Fact]
    public async Task OpenAsync_CreatesMissingDirectoryAndJobRunsSchema()
    {
        var path = DbPath("jobs");

        using var conn = await SqliteBootstrap.OpenAsync(path, SqliteSchema.JobRuns);

        Assert.True(File.Exists(path));
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='job_runs'"));
    }

    [Fact]
    public void Open_UnknownSchema_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqliteBootstrap.Open(DbPath("bad"), (SqliteSchema)999));
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
