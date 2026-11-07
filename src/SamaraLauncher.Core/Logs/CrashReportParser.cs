using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Logs;

/// <summary>
/// Разбирает crash-reports\crash-*.txt в человеческий CrashSummary.
/// </summary>
public sealed class CrashReportParser
{
    private const string SuspectedModHeader = "Suspected Mod:";
    private const string SuspectedModsHeader = "Suspected Mods:";
    private const string ModListHeader = "Mod List:";

    /// <summary>Самый свежий crash-*.txt в crash-reports или null.</summary>
    public string? FindLatest(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return null;
        }

        var directory = Path.Combine(gameDir, "crash-reports");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "crash-*.txt", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Разбирает самый свежий краш. null — если отчётов нет или файл не читается.</summary>
    public CrashSummary? TryParseLatest(string gameDir)
    {
        try
        {
            var latest = FindLatest(gameDir);
            return latest is null ? null : Parse(latest);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Разбирает конкретный crash-report.</summary>
    public CrashSummary Parse(string crashFile)
    {
        if (string.IsNullOrWhiteSpace(crashFile))
        {
            throw new ArgumentException("Не указан файл crash-report.", nameof(crashFile));
        }

        var fullPath = Path.GetFullPath(crashFile);
        if (!File.Exists(fullPath))
        {
            throw new LauncherException($"Файл crash-report не найден: {fullPath}");
        }

        string[] lines;
        try
        {
            lines = ReadAllLinesShared(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось прочитать crash-report: {fullPath}", ex);
        }

        var summary = new CrashSummary { CrashFile = fullPath };
        var causeIndex = -1;
        var suspectedIndex = -1;
        var modListIndex = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (summary.Time.Length == 0 && trimmed.StartsWith("Time:", StringComparison.OrdinalIgnoreCase))
            {
                summary.Time = trimmed["Time:".Length..].Trim();
                continue;
            }

            if (summary.Description.Length == 0 &&
                trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
            {
                summary.Description = trimmed["Description:".Length..].Trim();
                continue;
            }

            if (causeIndex < 0 &&
                trimmed.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
            {
                causeIndex = i;
                summary.Cause = trimmed["Caused by:".Length..].Trim();
                continue;
            }

            if (suspectedIndex < 0 &&
                (trimmed.StartsWith(SuspectedModsHeader, StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith(SuspectedModHeader, StringComparison.OrdinalIgnoreCase)))
            {
                suspectedIndex = i;
                var headerLength = trimmed.StartsWith(SuspectedModsHeader, StringComparison.OrdinalIgnoreCase)
                    ? SuspectedModsHeader.Length
                    : SuspectedModHeader.Length;

                summary.SuspectedMod = trimmed[headerLength..].Trim();
                continue;
            }

            if (modListIndex < 0 &&
                trimmed.StartsWith(ModListHeader, StringComparison.OrdinalIgnoreCase))
            {
                modListIndex = i;
            }
        }

        if (summary.SuspectedMod.Length == 0 && suspectedIndex >= 0)
        {
            summary.SuspectedMod = ReadSuspectedMod(lines, suspectedIndex);
        }

        if (summary.Cause.Length == 0)
        {
            summary.Cause = FindFirstExceptionLine(lines);
        }

        var mods = ParseMods(lines, modListIndex);
        summary.RawExcerpt = BuildExcerpt(lines, causeIndex, mods);
        summary.Recommendation = BuildRecommendation(summary, string.Join('\n', lines));

        return summary;
    }

    private static string ReadSuspectedMod(string[] lines, int headerIndex)
    {
        for (var i = headerIndex + 1; i < lines.Length && i <= headerIndex + 8; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("--", StringComparison.Ordinal) ||
                line.StartsWith("Stacktrace:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Details:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("at ", StringComparison.Ordinal))
            {
                break;
            }

            return line;
        }

        return "";
    }

    private static string FindFirstExceptionLine(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Time:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return "";
    }

    private static List<string> ParseMods(string[] lines, int modListIndex)
    {
        var mods = new List<string>();
        if (modListIndex < 0)
        {
            return mods;
        }

        for (var i = modListIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                if (mods.Count > 0)
                {
                    break;
                }

                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.Contains('|'))
            {
                if (mods.Count > 0)
                {
                    break;
                }

                continue;
            }

            var columns = trimmed.Split('|');
            if (columns.Length < 4)
            {
                continue;
            }

            var modId = columns[2].Trim();
            var name = columns[1].Trim();

            var value = modId.Length > 0 ? modId : name;
            if (value.Length > 0 && !mods.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                mods.Add(value);
            }
        }

        return mods;
    }

    private static string BuildExcerpt(string[] lines, int causeIndex, IReadOnlyList<string> mods)
    {
        const int firstLines = 40;
        const int causeLines = 8;

        var excerpt = new List<string>();
        excerpt.AddRange(lines.Take(Math.Min(firstLines, lines.Length)));

        if (causeIndex >= firstLines)
        {
            excerpt.Add("");
            excerpt.Add("...");

            var end = Math.Min(lines.Length, causeIndex + causeLines);
            for (var i = causeIndex; i < end; i++)
            {
                excerpt.Add(lines[i]);
            }
        }

        if (mods.Count > 0)
        {
            excerpt.Add("");
            excerpt.Add(
                $"Моды ({mods.Count}): {string.Join(", ", mods.Take(12))}" +
                (mods.Count > 12 ? $", ... ещё {mods.Count - 12}" : ""));
        }

        return string.Join(Environment.NewLine, excerpt);
    }

    private static string BuildRecommendation(CrashSummary summary, string fullText)
    {
        if (!string.IsNullOrWhiteSpace(summary.SuspectedMod))
        {
            return $"Похоже, проблема в моде: {summary.SuspectedMod}. " +
                   "Отключите его в списке модов и запустите игру снова.";
        }

        if (fullText.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
        {
            return "Игре не хватило памяти. Увеличьте объём памяти в настройках профиля " +
                   "(например, 6144–8192 МБ) и закройте лишние программы.";
        }

        if (fullText.Contains("mixin", StringComparison.OrdinalIgnoreCase) ||
            fullText.Contains("NoSuchMethod", StringComparison.OrdinalIgnoreCase))
        {
            return "Похоже на несовместимость модов (mixin/NoSuchMethodError). " +
                   "Проверьте, что все моды поддерживают Minecraft 1.20.1 и Forge 47.4.28, " +
                   "и отключите недавно добавленные.";
        }

        return "Откройте latest.log и crash-report, чтобы увидеть полный стек. " +
               "Если ошибка повторяется — отключите недавно добавленные моды и проверьте логи.";
    }

    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }
}
