namespace TicTack;

[Trait("Category", "Unit")]
public sealed class FileSnapshotTests
{
    [Fact]
    public void Equality_ComparesLengthAndTicks()
    {
        var a = new FileSnapshot(10, 100);
        var b = new FileSnapshot(10, 100);
        var shorter = new FileSnapshot(11, 100);
        var newer = new FileSnapshot(10, 101);

        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(shorter));
        Assert.False(a.Equals(newer));
    }

    [Fact]
    public void LastWriteTimeUtc_IsUtcKind()
    {
        var snapshot = new FileSnapshot(1, 100);

        Assert.Equal(DateTimeKind.Utc, snapshot.LastWriteTimeUtc.Kind);
    }
}
