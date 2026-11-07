using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Переопределения запуска, задаваемые из CLI или UI.
/// </summary>
public sealed class LaunchOptions
{
    public string? Username { get; set; }

    public string? Uuid { get; set; }

    public int? MemoryMb { get; set; }

    public int? MinMemoryMb { get; set; }

    public string? JavaPath { get; set; }

    public string? VersionId { get; set; }

    public List<string> ExtraJvmArgs { get; } = new();
}

/// <summary>
/// Готовая команда запуска Java.
/// </summary>
public sealed class LaunchPlan
{
    public string JavaPath { get; init; } = "";

    public string WorkingDirectory { get; init; } = "";

    public string MainClass { get; init; } = "";

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Командная строка для показа пользователю (не для Process.Start).</summary>
    public string ToDisplayCommand() =>
        string.Join(' ', new[] { JavaPath }.Concat(Arguments).Select(Quote));

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }
}
