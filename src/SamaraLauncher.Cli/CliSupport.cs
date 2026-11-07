using System.Globalization;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Cli;

/// <summary>Печатает прогресс Core-сервисов в консоль: этап, проценты и сообщение.</summary>
internal sealed class ConsoleProgress : IProgress<LauncherProgress>
{
    private readonly object _gate = new();

    public void Report(LauncherProgress value)
    {
        if (value is null)
        {
            return;
        }

        var message = (value.Message ?? "").Trim();
        var percent = value.Percent;

        lock (_gate)
        {
            if (message.Length == 0 && percent is null)
            {
                return;
            }

            var prefix = percent is null
                ? $"[{StageName(value.Stage)}]"
                : $"[{StageName(value.Stage)}] {percent.Value.ToString("0", CultureInfo.InvariantCulture)}%";

            Console.WriteLine(message.Length == 0 ? prefix : $"{prefix} {message}");
        }
    }

    private static string StageName(ProgressStage stage) => stage switch
    {
        ProgressStage.Preparing => "подготовка",
        ProgressStage.Java => "Java",
        ProgressStage.Minecraft => "Minecraft",
        ProgressStage.Forge => "Forge",
        ProgressStage.Pack => "сборка",
        ProgressStage.Backup => "бэкап",
        ProgressStage.Verify => "проверка",
        ProgressStage.Launch => "запуск",
        ProgressStage.Update => "обновление",
        ProgressStage.Done => "готово",
        ProgressStage.Error => "ошибка",
        _ => stage.ToString()
    };
}

/// <summary>Ctrl+C без мгновенного завершения процесса: отменяет текущую операцию.</summary>
internal sealed class CliCancellation : IDisposable
{
    private readonly ConsoleCancelEventHandler _handler;

    public CliCancellation(bool announce = true)
    {
        Source = new CancellationTokenSource();
        _handler = (_, e) =>
        {
            e.Cancel = true;

            if (announce)
            {
                Console.Error.WriteLine("Остановка...");
            }

            try
            {
                Source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        };

        Console.CancelKeyPress += _handler;
    }

    public CancellationTokenSource Source { get; }

    public CancellationToken Token => Source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
        Source.Dispose();
    }
}

internal static class CliText
{
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }

        var value = (double)bytes;
        var units = new[] { "КБ", "МБ", "ГБ", "ТБ" };
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
