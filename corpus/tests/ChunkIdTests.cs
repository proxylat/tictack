namespace Corpus;

public sealed class ChunkIdTests : IDisposable
{
    private readonly string _dir;

    public ChunkIdTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_id_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ComputeId_SameInputAndKey_IsDeterministic()
    {
        var plain = new byte[] { 1, 2, 3, 4 };
        var key = new byte[32];
        var a = ChunkId.ComputeId(plain, key);
        var b = ChunkId.ComputeId(plain, key);
        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeId_DifferentKey_Differs()
    {
        var plain = new byte[] { 1, 2, 3, 4 };
        var a = ChunkId.ComputeId(plain, new byte[32]);
        var otherKey = new byte[32];
        otherKey[0] = 1;
        var b = ChunkId.ComputeId(plain, otherKey);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToHex_Is64LowercaseHexChars()
    {
        var hex = ChunkId.ToHex(ChunkId.ComputeId(new byte[] { 9 }, new byte[32]));
        Assert.Equal(64, hex.Length);
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void EnsureKey_Creates32ByteFile_StableAcrossCalls()
    {
        var root = Path.Combine(_dir, "store");
        var first = ChunkId.EnsureKey(root);
        Assert.Equal(32, first.Length);
        Assert.Equal(32, new FileInfo(Path.Combine(root, "id.key")).Length);
        var second = ChunkId.EnsureKey(root);
        Assert.Equal(first, second);
    }
}
