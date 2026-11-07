namespace SamaraLauncher.Core.Models;

/// <summary>Готовая команда запуска: путь к java, аргументы и рабочая папка.</summary>
public sealed record LaunchPlan(
    string JavaPath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public string MainClass { get; init; } = "";

    public string Classpath { get; init; } = "";

    public string NativesDirectory { get; init; } = "";

    /// <summary>Host сервера для быстрого входа; null — без автоматического подключения.</summary>
    public string? ServerAddress { get; init; }

    /// <summary>Порт сервера для быстрого входа; null — без автоматического подключения.</summary>
    public int? ServerPort { get; init; }
}
