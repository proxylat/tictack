using Xunit;

namespace Corpus.Tests;

public sealed class VssHealthTests
{
    [Fact]
    public void Advice_BadState_NamesRestart()
    {
        var advice = VssHealth.AdviceForHr(unchecked((int)0x80042302));
        Assert.NotNull(advice);
        Assert.Contains("restart", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vssadmin", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Advice_AccessDenied_NamesElevation()
    {
        var advice = VssHealth.AdviceForHr(unchecked((int)0x80070005));
        Assert.NotNull(advice);
        Assert.Contains("elevat", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x80042301))]
    [InlineData(-1)]
    public void Advice_UnknownHr_Null(int hr)
    {
        Assert.Null(VssHealth.AdviceForHr(hr));
    }

    [Fact]
    public void Parse_Running()
    {
        const string sc = "SERVICE_NAME: VSS\r\n        TYPE               : 20  WIN32_SHARE_PROCESS\r\n        STATE              : 4  RUNNING";
        Assert.Equal(VssHealth.StateRunning, VssHealth.ParseScState(sc, 0, false));
    }

    [Fact]
    public void Parse_Stopped()
    {
        const string sc = "SERVICE_NAME: EventSystem\r\n        STATE              : 1  STOPPED";
        Assert.Equal(VssHealth.StateStopped, VssHealth.ParseScState(sc, 0, false));
    }

    [Fact]
    public void Parse_NotInstalled_Missing()
    {
        Assert.Equal(VssHealth.StateMissing, VssHealth.ParseScState("", 1060, false));
        Assert.Equal(VssHealth.StateMissing, VssHealth.ParseScState("OpenService FAILED 1060: The specified service does not exist as an installed service.", 1060, false));
    }

    [Fact]
    public void Parse_TimeoutOrError_Unknown()
    {
        Assert.Equal(VssHealth.StateUnknown, VssHealth.ParseScState("whatever", 0, true));
        Assert.Equal(VssHealth.StateUnknown, VssHealth.ParseScState("", 1, false));
        Assert.Equal(VssHealth.StateUnknown, VssHealth.ParseScState("", 0, false));
    }

    [Fact]
    public void BitLocker_FvevolMissing_Absent()
    {
        var env = new VssHealth.BitLockerEnv(VssHealth.StateRunning, VssHealth.StateMissing, false);
        Assert.True(VssHealth.IsAbsentOrDisabled(env));
    }

    [Fact]
    public void BitLocker_BdeSvcMissing_Absent()
    {
        var env = new VssHealth.BitLockerEnv(VssHealth.StateMissing, VssHealth.StateRunning, false);
        Assert.True(VssHealth.IsAbsentOrDisabled(env));
    }

    [Fact]
    public void BitLocker_PolicyPlusStoppedService_Disabled()
    {
        var env = new VssHealth.BitLockerEnv(VssHealth.StateStopped, VssHealth.StateRunning, true);
        Assert.True(VssHealth.IsAbsentOrDisabled(env));
    }

    [Fact]
    public void BitLocker_StoppedServiceAlone_Indeterminate()
    {
        // Driver present + no policy: the volume may still be encrypted
        // while the service is down, so this must keep warning.
        var env = new VssHealth.BitLockerEnv(VssHealth.StateStopped, VssHealth.StateRunning, false);
        Assert.False(VssHealth.IsAbsentOrDisabled(env));
    }

    [Fact]
    public void BitLocker_AllHealthy_Indeterminate()
    {
        var env = new VssHealth.BitLockerEnv(VssHealth.StateRunning, VssHealth.StateRunning, false);
        Assert.False(VssHealth.IsAbsentOrDisabled(env));
    }

    [Fact]
    public void TryGetHr_RoundTrips()
    {
        var ex = new IOException("GatherWriterMetadata failed (0x80042302)");
        ex.Data["VssHr"] = unchecked((int)0x80042302);
        Assert.Equal(unchecked((int)0x80042302), VssHealth.TryGetHr(ex));
    }

    [Fact]
    public void TryGetHr_PlainOrNull_Zero()
    {
        Assert.Equal(0, VssHealth.TryGetHr(new IOException("boom")));
        Assert.Equal(0, VssHealth.TryGetHr(null));
    }

    [Theory]
    [InlineData(unchecked((int)0x80042302), true, "vss")]
    [InlineData(0, true, "managed")]
    [InlineData(0, false, "aot")]
    [InlineData(unchecked((int)0x80042302), false, "aot")]
    public void FailureKind_Routes(int hr, bool comSupported, string expected)
    {
        Assert.Equal(expected, VssHealth.FailureKind(hr, comSupported));
    }

    [Fact]
    public void ParseScDisabled_Disabled_True()
    {
        const string sc = "SERVICE_NAME: swprv\r\n        START_TYPE         : 4   DISABLED";
        Assert.True(VssHealth.ParseScDisabled(sc, 0, false));
    }

    [Theory]
    [InlineData("START_TYPE         : 3   DEMAND_START")]
    [InlineData("START_TYPE         : 2   AUTO_START")]
    public void ParseScDisabled_EnabledStart_False(string line)
    {
        Assert.False(VssHealth.ParseScDisabled("SERVICE_NAME: swprv\r\n        " + line, 0, false));
    }

    [Fact]
    public void ParseScDisabled_Inconclusive_Null()
    {
        Assert.Null(VssHealth.ParseScDisabled("whatever", 0, true));
        Assert.Null(VssHealth.ParseScDisabled("", 1060, false));
        Assert.Null(VssHealth.ParseScDisabled("", 0, false));
    }
}
