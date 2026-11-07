using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Запуск java-процесса с перенаправлением stdout/stderr в переданный writer.
/// </summary>
public sealed class GameProcessLauncher
{
    public async Task<int> RunAsync(
        LaunchPlan plan,
        TextWriter output,
        Action<int>? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        var gate = new object();

        var result = await ProcessHost.RunAsync(
            plan.JavaPath,
            plan.Arguments,
            workingDirectory: plan.WorkingDirectory,
            timeout: null,
            cancellationToken: cancellationToken,
            onLine: line =>
            {
                lock (gate)
                {
                    output.WriteLine(line);
                }
            },
            onStarted: onStarted,
            outputEncoding: System.Text.Encoding.UTF8).ConfigureAwait(false);

        return result.ExitCode;
    }
}
