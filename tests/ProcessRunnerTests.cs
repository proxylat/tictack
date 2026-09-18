using System.Diagnostics;

namespace TicTack;

[Trait("Category", "Unit")]
public sealed class ProcessRunnerTests
{
    private static string Echo(string text) => "echo " + text;

    private static string Exit(int code) => "exit " + code;

    private static string SleepSeconds(int seconds) =>
        OperatingSystem.IsWindows()
            ? "ping -n " + (seconds + 1) + " 127.0.0.1 > NUL"
            : "sleep " + seconds;

    [Fact]
    public void CreateStartInfo_UsesPlatformShell()
    {
        var psi = ProcessRunner.CreateStartInfo("echo hi", Path.GetTempPath(), redirect: true);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", psi.FileName);
            Assert.Contains("/c echo hi", psi.Arguments);
        }
        else
        {
            Assert.Equal("/bin/sh", psi.FileName);
            Assert.Equal(new[] { "-c", "echo hi" }, psi.ArgumentList);
        }
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    [Fact]
    public async Task RunAsync_CapturesStdoutAndExitCode()
    {
        var result = await ProcessRunner.RunAsync(Echo("hello"), Path.GetTempPath(), redirect: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Stdout.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_IsReported()
    {
        var result = await ProcessRunner.RunAsync(Exit(3), Path.GetTempPath(), redirect: true);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_WithoutRedirect_ReturnsEmptyStreams()
    {
        var result = await ProcessRunner.RunAsync(Echo("hidden"), Path.GetTempPath(), redirect: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsChildAndReportsTimedOut()
    {
        var sw = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            SleepSeconds(30), Path.GetTempPath(), redirect: true, ct: default, timeoutMs: 400);
        sw.Stop();

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "timeout did not cut the child short: " + sw.Elapsed);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_ReportsTimedOut()
    {
        using var cts = new CancellationTokenSource(400);
        var result = await ProcessRunner.RunAsync(
            SleepSeconds(30), Path.GetTempPath(), redirect: true, ct: cts.Token, timeoutMs: 60000);

        Assert.True(result.TimedOut);
    }
}
