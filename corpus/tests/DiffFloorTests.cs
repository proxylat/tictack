namespace Corpus;

public sealed class DiffFloorTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Required_100MBToy_Is32MiBMin()
    {
        Assert.Equal(33554432L, DiffFloor.RequiredHeadroomBytes(100 * MiB));
    }

    [Fact]
    public void MeetsFloor_100MBToyMostlyFree_Passes()
    {
        // VM regression: 100 MB toy with ~20 MB data must image.
        Assert.True(DiffFloor.MeetsFloor(100 * MiB, 80 * MiB));
    }

    [Fact]
    public void MeetsFloor_100MBToy_ExactBoundary()
    {
        Assert.True(DiffFloor.MeetsFloor(100 * MiB, 33554432L));
        Assert.False(DiffFloor.MeetsFloor(100 * MiB, 33554431L));
    }

    [Fact]
    public void Required_TinyVolume_PinsAt32MiBMin()
    {
        // 20 MiB total: 10% would be 2 MiB, but VSS cannot allocate a diff
        // area below its documented minimum — required exceeds the volume,
        // so such volumes fail loud instead of dying inside VSS.
        Assert.Equal(33554432L, DiffFloor.RequiredHeadroomBytes(20 * MiB));
        Assert.False(DiffFloor.MeetsFloor(20 * MiB, 20 * MiB - 1));
    }

    [Fact]
    public void Required_TierEdge500MB_Steps32To320MiB()
    {
        // Just below the edge the 10% rule (~50 MB) already exceeds the
        // 32 MiB small-tier floor; at the edge the 320 MiB large-tier floor
        // takes over.
        Assert.Equal(52428799L, DiffFloor.RequiredHeadroomBytes(500 * MiB - 1));
        Assert.Equal(335544320L, DiffFloor.RequiredHeadroomBytes(500 * MiB));
    }

    [Fact]
    public void Required_1GB_PinsAt320MiBMin()
    {
        // 10% of 1 GiB truncates to 107374182 < 320 MiB floor.
        Assert.Equal(335544320L, DiffFloor.RequiredHeadroomBytes(GiB));
    }

    [Fact]
    public void Required_PercentZone_IsExactlyTenPercent()
    {
        Assert.Equal(536870912L, DiffFloor.RequiredHeadroomBytes(5 * GiB));
    }

    [Fact]
    public void Required_PercentMinJoin_CrossesAt3200MiB()
    {
        Assert.Equal(335544320L, DiffFloor.RequiredHeadroomBytes(3200 * MiB - 10));
        Assert.Equal(335544320L, DiffFloor.RequiredHeadroomBytes(3200 * MiB));
        Assert.Equal(335544321L, DiffFloor.RequiredHeadroomBytes(3200 * MiB + 10));
    }

    [Fact]
    public void Required_CapZone_PinsAt1GiB()
    {
        Assert.Equal(GiB, DiffFloor.RequiredHeadroomBytes(10 * GiB));
        Assert.Equal(GiB, DiffFloor.RequiredHeadroomBytes(10 * GiB + 10));
    }

    [Fact]
    public void Required_HugeVolume_PinsAt1GiB()
    {
        Assert.Equal(GiB, DiffFloor.RequiredHeadroomBytes(2L * 1024 * GiB));
    }

    [Fact]
    public void MeetsFloor_FullLargeVolume_Fails()
    {
        Assert.False(DiffFloor.MeetsFloor(20 * GiB, 100 * MiB));
        Assert.True(DiffFloor.MeetsFloor(20 * GiB, GiB));
    }

    [Fact]
    public void Required_NonPositiveTotal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiffFloor.RequiredHeadroomBytes(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiffFloor.RequiredHeadroomBytes(-1));
    }
}
