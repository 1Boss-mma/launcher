using System.Text;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Важность найденной причины краша. Значения упорядочены от информации к фатальной ошибке.
/// </summary>
public enum CrashSeverity
{
    /// <summary>Полезная информация без явной ошибки.</summary>
    Info,

    /// <summary>Предупреждение: игра может работать, но есть риск сбоя.</summary>
    Warning,

    /// <summary>Ошибка, из-за которой игра, скорее всего, завершилась.</summary>
    Error,

    /// <summary>Критическая ошибка: памяти, графики или зависание серверного тика.</summary>
    Fatal
}

/// <summary>
/// Результат анализа crash-report или хвоста latest.log: заголовок отчёта,
/// найденные шаблоны, подозрительные моды и готовые рекомендации.
/// </summary>
public sealed class CrashAnalysisResult
{
    /// <summary>Создаёт пустой результат; заполняется <see cref="CrashAnalyzer"/>.</summary>
    public CrashAnalysisResult()
    {
    }

    /// <summary>Полный путь к проанализированному файлу.</summary>
    public string CrashFile { get; set; } = "";

    /// <summary>Время краша из заголовка отчёта или время изменения файла.</summary>
    public string Time { get; set; } = "";

    /// <summary>Описание из заголовка crash-report (например, «Unexpected error»).</summary>
    public string Description { get; set; } = "";

    /// <summary>Наиболее вероятная причина: строка <c>Caused by:</c> или первое исключение.</summary>
    public string Cause { get; set; } = "";

    /// <summary>Подозрительные моды, извлечённые из отчёта. Не более десяти, без дублей.</summary>
    public List<string> SuspectedMods { get; set; } = new();

    /// <summary>Рекомендации пользователю. Готовые фразы без выдуманных имён модов.</summary>
    public List<string> Recommendations { get; set; } = new();

    /// <summary>Названия совпавших шаблонов, отсортированные по важности.</summary>
    public List<string> MatchedPatterns { get; set; } = new();

    /// <summary>Общая важность краша — максимум по совпавшим шаблонам.</summary>
    public CrashSeverity Severity { get; set; } = CrashSeverity.Info;

    /// <summary>Короткий фрагмент отчёта с заголовком и строками вокруг совпадений.</summary>
    public string RawExcerpt { get; set; } = "";

    /// <summary>
    /// Человекочитаемый отчёт для текстового поля, буфера обмена или файла.
    /// </summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Анализ краша лаунчера");
        builder.AppendLine("================================");
        builder.AppendLine($"Файл: {Display(CrashFile)}");
        builder.AppendLine($"Время: {Display(Time)}");
        builder.AppendLine($"Уровень: {SeverityLabel(Severity)}");
        builder.AppendLine($"Описание: {Display(Description)}");
        builder.AppendLine($"Причина: {Display(Cause)}");
        builder.AppendLine();

        if (SuspectedMods.Count == 0)
        {
            builder.AppendLine("Подозрительные моды: не найдены.");
        }
        else
        {
            builder.AppendLine($"Подозрительные моды ({SuspectedMods.Count}):");
            foreach (var mod in SuspectedMods)
            {
                builder.AppendLine("  - " + mod);
            }
        }

        builder.AppendLine();

        if (MatchedPatterns.Count == 0)
        {
            builder.AppendLine("Совпавшие шаблоны: не найдены.");
        }
        else
        {
            builder.AppendLine($"Совпавшие шаблоны ({MatchedPatterns.Count}):");
            foreach (var pattern in MatchedPatterns)
            {
                builder.AppendLine("  - " + pattern);
            }
        }

        builder.AppendLine();

        if (Recommendations.Count == 0)
        {
            builder.AppendLine("Рекомендации: нет.");
        }
        else
        {
            builder.AppendLine("Рекомендации:");
            for (var i = 0; i < Recommendations.Count; i++)
            {
                builder.AppendLine($"{i + 1}. {Recommendations[i]}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Фрагмент отчёта:");
        builder.AppendLine(string.IsNullOrWhiteSpace(RawExcerpt) ? "Фрагмент недоступен." : RawExcerpt);

        return builder.ToString().TrimEnd();
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "не указано" : value;

    private static string SeverityLabel(CrashSeverity severity) => severity switch
    {
        CrashSeverity.Info => "Информация (Info)",
        CrashSeverity.Warning => "Предупреждение (Warning)",
        CrashSeverity.Error => "Ошибка (Error)",
        CrashSeverity.Fatal => "Критическая ошибка (Fatal)",
        _ => severity.ToString()
    };
}
