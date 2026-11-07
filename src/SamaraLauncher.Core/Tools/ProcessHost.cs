using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SamaraLauncher.Core.Tools;

/// <summary>
/// Результат запуска внешнего процесса: код выхода и объединённый вывод.
/// </summary>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>stdout и stderr вместе — в том порядке, в котором их вернул процесс.</summary>
    public string Output
    {
        get
        {
            if (StandardError.Length == 0)
            {
                return StandardOutput;
            }

            if (StandardOutput.Length == 0)
            {
                return StandardError;
            }

            return StandardOutput + Environment.NewLine + StandardError;
        }
    }
}

/// <summary>
/// Единая точка запуска внешних процессов. Все консольные процессы запускаются
/// скрыто (UseShellExecute=false + CreateNoWindow + Hidden), stdout/stderr читаются
/// одновременно, поэтому дочерний процесс не может зависнуть на заполненном pipe.
/// </summary>
public static class ProcessHost
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Создаёт скрытый <see cref="ProcessStartInfo"/> с ArgumentList.
    /// </summary>
    public static ProcessStartInfo CreateHidden(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        bool redirectOutput = true,
        Encoding? outputEncoding = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new LauncherException("Не указан файл для запуска процесса.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (outputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = outputEncoding;
            startInfo.StandardErrorEncoding = outputEncoding;
        }

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Применяет скрытый режим к уже заполненному <see cref="ProcessStartInfo"/>.
    /// </summary>
    public static ProcessStartInfo CreateHidden(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        return startInfo;
    }

    /// <summary>Псевдоним <see cref="CreateHidden"/> для читаемости в местах запуска.</summary>
    public static ProcessStartInfo StartInfo(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        bool redirectOutput = true,
        Encoding? outputEncoding = null) =>
        CreateHidden(fileName, arguments, workingDirectory, redirectOutput, outputEncoding);

    /// <summary>
    /// Запускает процесс скрыто, одновременно вычитывает stdout/stderr, ждёт завершения
    /// с необязательным таймаутом и убивает всё дерево процессов при отмене/таймауте.
    /// </summary>
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<string>? onLine = null,
        Action<int>? onStarted = null,
        Encoding? outputEncoding = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = CreateHidden(
            fileName,
            arguments,
            workingDirectory,
            redirectOutput: true,
            outputEncoding);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new LauncherException($"Не удалось запустить процесс: {fileName}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new LauncherException($"Не удалось запустить {fileName}: {ex.Message}", ex);
        }

        try
        {
            onStarted?.Invoke(process.Id);
        }
        catch
        {
            // Ошибка подписчика не должна мешать запуску.
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, output, onLine);
        var stderrTask = PumpAsync(process.StandardError, error, onLine);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutCts.CancelAfter(timeout.Value);
        }

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var alreadyExited = false;

            try
            {
                alreadyExited = process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                alreadyExited = true;
            }

            if (!alreadyExited)
            {
                TryKill(process);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await DrainBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            timedOut = !alreadyExited;
        }

        await DrainBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);

        var exitCode = -1;
        if (!timedOut)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                exitCode = -1;
            }
        }

        return new ProcessRunResult(exitCode, output.ToString(), error.ToString(), timedOut);
    }

    /// <summary>
    /// Аварийный вариант для сырой командной строки (совместимость со старым API).
    /// Везде, где возможно, используйте перегрузку с IEnumerable аргументов.
    /// </summary>
    public static Task<ProcessRunResult> RunRawAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<string>? onLine = null,
        Action<int>? onStarted = null,
        Encoding? outputEncoding = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new LauncherException("Не указан файл для запуска процесса.");
        }

        var startInfo = CreateHidden(
            fileName,
            arguments: null,
            workingDirectory,
            redirectOutput: true,
            outputEncoding);

        startInfo.Arguments = arguments ?? "";

        return RunStartInfoAsync(startInfo, timeout, cancellationToken, onLine, onStarted);
    }

    /// <summary>
    /// Запускает процесс отсоединённым (не ждёт завершения), без окна консоли.
    /// Подходит для updater и fallback-скриптов.
    /// </summary>
    public static Process StartDetachedHidden(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = CreateHidden(
            fileName,
            arguments,
            workingDirectory,
            redirectOutput: false);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        try
        {
            return Process.Start(startInfo)
                   ?? throw new LauncherException($"Не удалось запустить {fileName}: Process.Start вернул null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new LauncherException($"Не удалось запустить {fileName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Открывает папку или известный GUI-файл через оболочку Windows.
    /// Не использовать для консольных исполняемых файлов.
    /// </summary>
    public static void ShellOpen(string path, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LauncherException("Не указан путь для открытия.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new LauncherException($"Не удалось открыть {path}: {ex.Message}", ex);
        }
    }

    private static async Task<ProcessRunResult> RunStartInfoAsync(
        ProcessStartInfo startInfo,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        Action<string>? onLine,
        Action<int>? onStarted)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new LauncherException($"Не удалось запустить процесс: {startInfo.FileName}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new LauncherException(
                $"Не удалось запустить {startInfo.FileName}: {ex.Message}",
                ex);
        }

        try
        {
            onStarted?.Invoke(process.Id);
        }
        catch
        {
            // Ошибка подписчика не должна мешать запуску.
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, output, onLine);
        var stderrTask = PumpAsync(process.StandardError, error, onLine);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutCts.CancelAfter(timeout.Value);
        }

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var alreadyExited = false;

            try
            {
                alreadyExited = process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                alreadyExited = true;
            }

            if (!alreadyExited)
            {
                TryKill(process);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await DrainBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            timedOut = !alreadyExited;
        }

        await DrainBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);

        var exitCode = -1;
        if (!timedOut)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                exitCode = -1;
            }
        }

        return new ProcessRunResult(exitCode, output.ToString(), error.ToString(), timedOut);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder output,
        Action<string>? onLine)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                output.AppendLine(line);

                if (onLine is not null)
                {
                    try
                    {
                        onLine(line);
                    }
                    catch
                    {
                        // Ошибка подписчика не должна ломать чтение вывода.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Поток закрылся вместе с процессом.
        }
        catch (OperationCanceledException)
        {
            // Процесс убит — читать больше нечего.
        }
    }

    private static async Task DrainBoundedAsync(params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);

        try
        {
            await Task.WhenAny(all, Task.Delay(DrainTimeout)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Дочерний процесс уже закрыл потоки.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or Win32Exception
                or NotSupportedException
                or ObjectDisposedException)
        {
            // Процесс уже завершился или нет прав — ждать больше нечего.
        }
    }
}
