using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Результат полной диагностики инстанса: список проверок, итоговые счётчики
/// и представление в текстовом или JSON-виде.
/// </summary>
public sealed class DoctorReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DateTimeOffset StartedAt { get; set; }

    public TimeSpan Duration { get; set; }

    public List<DoctorCheck> Checks { get; set; } = new();

    public int Errors => Checks.Count(check => check.Status == DoctorStatus.Error);

    public int Warnings => Checks.Count(check => check.Status == DoctorStatus.Warning);

    /// <summary>Исправен ли инстанс: предупреждения допустимы, ошибки — нет.</summary>
    public bool IsHealthy => Errors == 0;

    /// <summary>Краткий итог по-русски. Формируется из списка проверок.</summary>
    public string Summary
    {
        get
        {
            if (Errors == 0 && Warnings == 0)
            {
                return $"Проверено {Checks.Count} пунктов: проблем не найдено.";
            }

            return Errors == 0
                ? $"Проверено {Checks.Count} пунктов: ошибок нет, предупреждений {Warnings}."
                : $"Проверено {Checks.Count} пунктов: ошибок {Errors}, предупреждений {Warnings}.";
        }
    }

    /// <summary>Человекочитаемый отчёт для лога, консоли или текстового поля UI.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Диагностика лаунчера");
        builder.AppendLine($"Начато: {StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Длительность: {FormatDuration(Duration)}");
        builder.AppendLine($"Итог: {Summary}");
        builder.AppendLine();

        if (Checks.Count == 0)
        {
            builder.AppendLine("Проверки не выполнялись.");
            return builder.ToString().TrimEnd();
        }

        foreach (var check in Checks)
        {
            builder.AppendLine(
                $"[{StatusLabel(check.Status)}] {CategoryLabel(check.Category)} / " +
                $"{check.Title}: {check.Message}");

            if (!string.IsNullOrWhiteSpace(check.Details))
            {
                foreach (var line in check.Details.Split(
                             new[] { "\r\n", "\n" },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    builder.AppendLine("    " + line.TrimEnd());
                }
            }

            if (check.CanAutoFix)
            {
                builder.AppendLine("    Можно исправить автоматически.");
            }

            if (!string.IsNullOrWhiteSpace(check.FixHint))
            {
                builder.AppendLine("    Подсказка: " + check.FixHint);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>JSON с camelCase и отступами — для сохранения в файл или передачи в UI.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " мс";
        }

        return duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " с";
    }

    private static string StatusLabel(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "OK",
        DoctorStatus.Warning => "ВНИМАНИЕ",
        DoctorStatus.Error => "ОШИБКА",
        DoctorStatus.Skipped => "ПРОПУЩЕНО",
        _ => status.ToString()
    };

    private static string CategoryLabel(DoctorCategory category) => category switch
    {
        DoctorCategory.Java => "Java",
        DoctorCategory.Files => "Файлы",
        DoctorCategory.Mods => "Моды",
        DoctorCategory.Network => "Сеть",
        DoctorCategory.Disk => "Диск",
        DoctorCategory.Config => "Настройки",
        DoctorCategory.Logs => "Логи",
        _ => category.ToString()
    };
}
