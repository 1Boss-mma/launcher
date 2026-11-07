namespace SamaraLauncher.Core.Models;

/// <summary>Человеческое представление crash-report.</summary>
public sealed class CrashSummary
{
    public string CrashFile { get; set; } = "";

    public string Time { get; set; } = "";

    public string Description { get; set; } = "";

    public string SuspectedMod { get; set; } = "";

    public string Cause { get; set; } = "";

    public string Recommendation { get; set; } = "";

    public string RawExcerpt { get; set; } = "";

    public override string ToString()
    {
        var lines = new List<string> { "Игра вылетела." };
        if (!string.IsNullOrEmpty(Description))
        {
            lines.Add($"Причина: {Description}");
        }

        if (!string.IsNullOrEmpty(SuspectedMod))
        {
            lines.Add($"Подозрительный мод: {SuspectedMod}");
        }

        if (!string.IsNullOrEmpty(Cause))
        {
            lines.Add($"Ошибка: {Cause}");
        }

        if (!string.IsNullOrEmpty(Recommendation))
        {
            lines.Add(Recommendation);
        }

        lines.Add($"Отчёт: {CrashFile}");
        return string.Join(Environment.NewLine, lines);
    }
}
