using System.Collections.Concurrent;
using System.Diagnostics;
using SamaraLauncher.Core.Launch;
using SamaraLauncher.Core.Models;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class GameProcessLifecycleTests
{
    [Fact]
    public async Task Exited_IsRaisedAfterOutputPumps_WithExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Небольшая задержка до вывода гарантирует, что подписки успеют
        // оформиться до завершения cmd.exe.
        using var game = new GameProcess(
            CreateStartInfo(
                "/c \"ping -n 2 127.0.0.1 >nul & echo stdout-line & echo stderr-line 1>&2 & exit 3\""),
            logPath: null);

        var lines = new ConcurrentQueue<LogLine>();
        var stdoutSeenAtExit = false;
        var stderrSeenAtExit = false;
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        game.LineReceived += line => lines.Enqueue(line);
        game.Exited += code =>
        {
            stdoutSeenAtExit = lines.Any(line => !line.IsError && line.Text.Contains("stdout-line", StringComparison.Ordinal));
            stderrSeenAtExit = lines.Any(line => line.IsError && line.Text.Contains("stderr-line", StringComparison.Ordinal));
            exited.TrySetResult(code);
        };

        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, exitCode);
        Assert.Equal(3, game.ExitCode);
        Assert.True(stdoutSeenAtExit, "Строки stdout должны быть доставлены до Exited.");
        Assert.True(stderrSeenAtExit, "Строки stderr должны быть доставлены до Exited.");
    }

    [Fact]
    public void Dispose_WhileRunning_IsIdempotent_AndSuppressesLateExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var game = new GameProcess(
            CreateStartInfo("/c \"ping -n 30 127.0.0.1 >nul\""),
            logPath: null);

        var exitRaised = 0;
        game.Exited += _ => Interlocked.Exchange(ref exitRaised, 1);

        Assert.True(game.IsRunning);

        game.Dispose();
        game.Dispose();

        Thread.Sleep(500);

        Assert.False(game.IsRunning);
        Assert.Equal(0, Volatile.Read(ref exitRaised));
    }

    private static ProcessStartInfo CreateStartInfo(string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }
}
