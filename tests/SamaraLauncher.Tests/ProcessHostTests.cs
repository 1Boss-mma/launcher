using System.Diagnostics;
using SamaraLauncher.Core.Tools;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ProcessHostTests
{
    [Fact]
    public void CreateHidden_ConfiguresHiddenProcessStartInfo()
    {
        var startInfo = ProcessHost.CreateHidden("cmd.exe", new[] { "/c", "echo hi" });

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(new[] { "/c", "echo hi" }, startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void CreateHidden_ForcesHiddenModeOnExistingStartInfo()
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        var result = ProcessHost.CreateHidden(startInfo);

        Assert.Same(startInfo, result);
        Assert.False(result.UseShellExecute);
        Assert.True(result.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, result.WindowStyle);
    }

    [Fact]
    public async Task RunAsync_CapturesStandardOutputAndError_AndReturnsExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessHost.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo out-line & echo err-line 1>&2 & exit 7" });

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("out-line", result.StandardOutput);
        Assert.Contains("err-line", result.StandardError);
        Assert.DoesNotContain("err-line", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsProcessAndReturnsTimedOut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessHost.RunAsync(
            "cmd.exe",
            new[] { "/c", "ping -n 10 127.0.0.1 >nul" },
            timeout: TimeSpan.FromMilliseconds(300));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }
}
