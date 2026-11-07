using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Logs;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Crash Analyzer 2.0: ищет и разбирает crash-report или хвост latest.log,
/// сопоставляет текст с базой шаблонов, извлекает подозрительные моды и
/// формирует рекомендации. Чтение ограничено первыми и последними ~2 МБ файла,
/// поэтому повреждённый или огромный лог не роняет лаунчер.
/// </summary>
public sealed class CrashAnalyzer
{
    private const int MaxReadBytes = 2 * 1024 * 1024;
    private const int SegmentBytes = MaxReadBytes / 2;
    private const int MaxLogTailBytes = 1024 * 1024;
    private const int MaxSuspectedMods = 10;
    private const int MaxRecommendations = 10;
    private const int MaxExcerptLines = 140;
    private const int MaxExcerptChars = 24_000;
    private const int MaxExcerptLineChars = 400;
    private const int MaxCauseChars = 600;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Encoding ReportEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true);

    private static readonly Regex JarPattern = new(
        @"([A-Za-z0-9_.+\-()]+\.jar)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex ModIdPattern = new(
        @"\bmodid\s*[=:]\s*['""]?([A-Za-z0-9_.\-]{2,80})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex ModIdInParensPattern = new(
        @"\(([^)\s]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex ModHeadingPattern = new(
        @"^--\s*Mods?\s+(.+?)\s*--$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex VersionSuffixPattern = new(
        @",?\s*Version:\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex IssueTrackerPattern = new(
        @"\s*Issue tracker URL:.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex FirstTokenPattern = new(
        @"[a-z0-9_]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex LogTimestampPattern = new(
        @"^\[(\d{2}:\d{2}:\d{2})\]",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> PlatformTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "client", "server", "forge", "neoforge", "fml", "fmlloader",
        "modlauncher", "bootstraplauncher", "mixin", "mixinloader", "java", "jdk", "jre",
        "log4j", "asm", "guava", "netty", "lwjgl", "authlib", "brigadier",
        "datafixerupper", "jna", "jarjar", "securejarhandler", "accesstransformers",
        "eventbus", "coremods", "javafmllanguage", "lowcodelanguage", "mclanguage",
        "fabricloader", "quilt_loader", "liteloader", "installer", "universal"
    };

    /// <summary>Создаёт анализатор со встроенной базой шаблонов.</summary>
    public CrashAnalyzer()
    {
    }

    /// <summary>
    /// Находит самый свежий <c>crash-reports\crash-*.txt</c> в папке игры и разбирает его.
    /// Возвращает <see langword="null"/>, если папки игры или crash-reports нет.
    /// </summary>
    /// <param name="instance">Профиль инстанса с заполненным <see cref="InstanceProfile.GameDir"/>.</param>
    /// <exception cref="LauncherException">Профиль не задан, путь некорректен или отчёт не читается.</exception>
    public CrashAnalysisResult? AnalyzeLatest(InstanceProfile instance)
    {
        var gameDir = ResolveGameDir(instance);
        if (gameDir is null)
        {
            return null;
        }

        var crashDirectory = Path.Combine(gameDir, "crash-reports");
        if (!Directory.Exists(crashDirectory))
        {
            return null;
        }

        string? latest;
        try
        {
            latest = Directory.EnumerateFiles(crashDirectory, "crash-*.txt", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            throw new LauncherException($"Не удалось просмотреть папку crash-reports: {crashDirectory}", ex);
        }

        return latest is null ? null : AnalyzeFile(latest);
    }

    /// <summary>
    /// Разбирает указанный crash-report. Поддерживает UTF-8 и cp1251,
    /// читает не более ~2 МБ (начало и конец файла).
    /// </summary>
    /// <param name="crashFilePath">Путь к файлу crash-report.</param>
    /// <exception cref="LauncherException">Путь пуст, файл не найден, является папкой или не читается.</exception>
    public CrashAnalysisResult AnalyzeFile(string crashFilePath)
    {
        if (string.IsNullOrWhiteSpace(crashFilePath))
        {
            throw new LauncherException("Не указан путь к crash-report.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(crashFilePath.Trim());
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            throw new LauncherException($"Некорректный путь к crash-report: {crashFilePath}", ex);
        }

        if (Directory.Exists(fullPath))
        {
            throw new LauncherException($"Указан путь к папке, а не к crash-report: {fullPath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new LauncherException($"Файл crash-report не найден: {fullPath}");
        }

        string text;
        try
        {
            text = ReadLimitedText(fullPath, tailOnly: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            throw new LauncherException($"Не удалось прочитать crash-report: {fullPath}", ex);
        }

        return AnalyzeText(text, fullPath, tailMode: false);
    }

    /// <summary>
    /// Разбирает хвост <c>logs\latest.log</c>. Используйте как запасной вариант,
    /// когда <see cref="AnalyzeLatest"/> вернул <see langword="null"/>.
    /// Возвращает <see langword="null"/>, если лог отсутствует или пуст.
    /// </summary>
    /// <param name="instance">Профиль инстанса с заполненным <see cref="InstanceProfile.GameDir"/>.</param>
    /// <exception cref="LauncherException">Профиль не задан, путь некорректен или лог не читается.</exception>
    public CrashAnalysisResult? AnalyzeLatestLog(InstanceProfile instance)
    {
        var gameDir = ResolveGameDir(instance);
        if (gameDir is null)
        {
            return null;
        }

        var logPath = Path.Combine(gameDir, "logs", "latest.log");
        if (!File.Exists(logPath))
        {
            return null;
        }

        string text;
        try
        {
            text = ReadLimitedText(logPath, tailOnly: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        {
            throw new LauncherException($"Не удалось прочитать latest.log: {logPath}", ex);
        }

        return text.Length == 0 ? null : AnalyzeText(text, logPath, tailMode: true);
    }

    /// <summary>
    /// Сохраняет читаемый текстовый отчёт в UTF-8 (с BOM) и возвращает полный путь файла.
    /// При необходимости создаёт родительскую папку.
    /// </summary>
    /// <param name="result">Результат анализа.</param>
    /// <param name="targetPath">Куда сохранить отчёт.</param>
    /// <exception cref="LauncherException">Результат или путь не задан, либо файл не удалось записать.</exception>
    public string ExportReport(CrashAnalysisResult result, string targetPath)
    {
        if (result is null)
        {
            throw new LauncherException("Нечего сохранять: результат анализа не задан.");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new LauncherException("Не указан путь для сохранения отчёта.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetPath.Trim());
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            throw new LauncherException($"Некорректный путь для сохранения отчёта: {targetPath}", ex);
        }

        if (Directory.Exists(fullPath))
        {
            throw new LauncherException($"Указан путь к папке, а не к файлу отчёта: {fullPath}");
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, result.ToText() + Environment.NewLine, ReportEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException or
                                   System.Security.SecurityException)
        {
            throw new LauncherException($"Не удалось сохранить отчёт анализа: {fullPath}", ex);
        }

        return fullPath;
    }

    private static CrashAnalysisResult AnalyzeText(string text, string path, bool tailMode)
    {
        var lines = text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
        var matched = MatchPatterns(text);

        var result = new CrashAnalysisResult
        {
            CrashFile = path,
            Time = FindHeader(lines, "Time:"),
            Description = FindHeader(lines, "Description:")
        };

        result.MatchedPatterns.AddRange(matched.Select(pattern => pattern.Title));
        result.Severity = matched.Count > 0
            ? matched.Max(pattern => pattern.Severity)
            : DetectSeverity(text, tailMode);

        // Любой crash-report — уже ошибка, даже если сработали только «предупреждающие» шаблоны.
        if (!tailMode &&
            result.Severity < CrashSeverity.Error &&
            text.Contains("Minecraft Crash Report", StringComparison.OrdinalIgnoreCase))
        {
            result.Severity = CrashSeverity.Error;
        }

        if (result.Time.Length == 0)
        {
            var lastWrite = GetLastWriteTime(path);
            result.Time = tailMode
                ? ResolveLogTime(lines, lastWrite)
                : lastWrite.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        result.Cause = FindCause(lines);

        if (result.Description.Length == 0)
        {
            result.Description = tailMode
                ? (result.Severity >= CrashSeverity.Error
                    ? "Игра завершилась с ошибкой (анализ latest.log)."
                    : "В хвосте latest.log явных ошибок не найдено.")
                : "Краш-репорт без описания.";
        }

        var mods = ExtractSuspectedMods(lines, text);
        result.SuspectedMods.AddRange(mods);
        result.Recommendations.AddRange(BuildRecommendations(matched, mods, tailMode));
        result.RawExcerpt = BuildExcerpt(lines, text, matched, tailMode);

        return result;
    }

    private static List<CrashPattern> MatchPatterns(string text)
    {
        var matched = new List<CrashPattern>();
        if (text.Length == 0)
        {
            return matched;
        }

        foreach (var pattern in CrashPatternCatalog.All)
        {
            if (pattern.IsMatch(text))
            {
                matched.Add(pattern);
            }
        }

        // Самые важные причины — первыми; OrderByDescending стабилен и сохраняет порядок каталога.
        return matched.OrderByDescending(pattern => pattern.Severity).ToList();
    }

    private static CrashSeverity DetectSeverity(string text, bool tailMode)
    {
        if (text.Length == 0)
        {
            return CrashSeverity.Info;
        }

        if (text.Contains("FATAL]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
        {
            return CrashSeverity.Fatal;
        }

        if (text.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ERROR]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("crash", StringComparison.OrdinalIgnoreCase))
        {
            return CrashSeverity.Error;
        }

        return tailMode ? CrashSeverity.Info : CrashSeverity.Warning;
    }

    private static string FindHeader(string[] lines, string header)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[header.Length..].Trim();
            }
        }

        return "";
    }

    private static string FindCause(string[] lines)
    {
        string? exceptionLine = null;
        string? errorLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
            {
                return Truncate(trimmed, MaxCauseChars);
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("at ", StringComparison.Ordinal) ||
                trimmed.StartsWith("...", StringComparison.Ordinal) ||
                trimmed.StartsWith("Stacktrace:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Details:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Thread:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Time:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (exceptionLine is null &&
                (trimmed.Contains("Exception", StringComparison.Ordinal) ||
                 trimmed.Contains("Error", StringComparison.Ordinal)))
            {
                exceptionLine = trimmed;
            }

            if (errorLine is null &&
                (trimmed.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Contains("/FATAL]", StringComparison.OrdinalIgnoreCase)))
            {
                errorLine = trimmed;
            }
        }

        return Truncate(exceptionLine ?? errorLine ?? "", MaxCauseChars);
    }

    private static string ResolveLogTime(string[] lines, DateTime lastWrite)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var match = LogTimestampPattern.Match(lines[i].Trim());
            if (match.Success)
            {
                return lastWrite.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                       " " + match.Groups[1].Value;
            }
        }

        return lastWrite.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static DateTime GetLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or System.Security.SecurityException)
        {
            return DateTime.Now;
        }
    }

    private static List<string> ExtractSuspectedMods(string[] lines, string text)
    {
        try
        {
            return ExtractSuspectedModsCore(lines, text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Слишком длинная или необычная строка: лучше без списка модов, чем с ошибкой анализа.
            return new List<string>();
        }
    }

    private static List<string> ExtractSuspectedModsCore(string[] lines, string text)
    {
        var mods = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Явные блоки Suspected Mod / Suspected Mods.
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!TryReadHeader(trimmed, "Suspected Mods:", out var inline) &&
                !TryReadHeader(trimmed, "Suspected Mod:", out inline))
            {
                continue;
            }

            if (inline.Length > 0)
            {
                AddSuspect(inline, mods, keys);
            }

            var consumed = 0;
            for (var j = i + 1; j < lines.Length && j <= i + 12; j++)
            {
                var next = lines[j].Trim();
                if (next.Length == 0)
                {
                    if (consumed == 0)
                    {
                        continue;
                    }

                    break;
                }

                if (next.StartsWith("Stacktrace:", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith("Details:", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith("-- ", StringComparison.Ordinal) ||
                    next.StartsWith("at ", StringComparison.Ordinal))
                {
                    break;
                }

                if (next.StartsWith("Issue tracker URL:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddSuspect(next, mods, keys);
                consumed++;
            }
        }

        // 2. Строки Mod File: и Failure message: — самый точный источник имени jar.
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (TryReadHeader(trimmed, "Mod File:", out var modFile))
            {
                AddJarFromText(modFile, mods, keys);
            }

            if (TryReadHeader(trimmed, "Failure message:", out var failure))
            {
                AddJarFromText(failure, mods, keys);
                for (var j = i + 1; j <= i + 3 && j < lines.Length; j++)
                {
                    AddJarFromText(lines[j], mods, keys);
                }
            }
        }

        // 3. Заголовки секций вида -- Mod someid --.
        foreach (var line in lines)
        {
            var heading = ModHeadingPattern.Match(line.Trim());
            if (heading.Success)
            {
                AddSuspect(heading.Groups[1].Value, mods, keys);
            }
        }

        // 4. modid из текста — только если явных имён ещё нет.
        if (mods.Count == 0)
        {
            AddModIdMatches(text, mods);
        }

        // 5. Имена jar из stacktrace — только когда других зацепок нет,
        //    иначе в список попадут все моды из стека.
        if (mods.Count == 0)
        {
            for (var i = 0; i < lines.Length && mods.Count < MaxSuspectedMods; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("at ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
                {
                    AddJarFromText(trimmed, mods, keys);
                }
            }
        }

        return mods;
    }

    private static void AddSuspect(string value, List<string> mods, HashSet<string> keys)
    {
        var normalized = NormalizeModValue(value);
        if (normalized.Length == 0)
        {
            return;
        }

        // Точное платформенное имя (minecraft, forge, client) отбрасываем,
        // но мод с названием вроде «Client Tweaks» оставляем.
        if (!normalized.Contains(' ') && IsPlatformItem(normalized))
        {
            return;
        }

        var idMatch = ModIdInParensPattern.Match(normalized);
        if (idMatch.Success)
        {
            AddKey(keys, idMatch.Groups[1].Value);
        }
        else if (!normalized.Contains(' '))
        {
            AddKey(keys, normalized);
        }

        AddUnique(mods, normalized);
    }

    private static void AddModIdMatches(string text, List<string> mods)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var count = 0;
            foreach (Match match in ModIdPattern.Matches(text))
            {
                var modId = match.Groups[1].Value;
                if (!IsPlatformItem(modId))
                {
                    AddUnique(mods, modId);
                }

                count++;
                if (count >= 20 || mods.Count >= MaxSuspectedMods)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Огромный или необычный текст: лучше без модов, чем с зависанием.
        }
    }

    private static void AddJarFromText(string value, List<string> mods, HashSet<string> keys)
    {
        if (mods.Count >= MaxSuspectedMods || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            foreach (Match match in JarPattern.Matches(value))
            {
                AddJar(match.Groups[1].Value, mods, keys);
                if (mods.Count >= MaxSuspectedMods)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Пропускаем слишком сложную строку.
        }
    }

    private static void AddJar(string jar, List<string> mods, HashSet<string> keys)
    {
        var name = CleanJarName(jar);
        if (name.Length == 0 || IsPlatformItem(name) || MatchesKnownKey(name, keys))
        {
            return;
        }

        AddUnique(mods, name);
    }

    private static string CleanJarName(string jar)
    {
        var value = jar.Trim();
        var percent = value.IndexOf('%');
        if (percent >= 0)
        {
            value = value[..percent];
        }

        var separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (separator >= 0)
        {
            value = value[(separator + 1)..];
        }

        value = value.Trim('"', '\'', '[', ']');
        return value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? value : "";
    }

    private static bool MatchesKnownKey(string jar, HashSet<string> keys)
    {
        if (keys.Count == 0)
        {
            return false;
        }

        var normalized = NormalizeKey(Path.GetFileNameWithoutExtension(jar));
        return keys.Any(key => key.Length >= 4 && normalized.Contains(key, StringComparison.Ordinal));
    }

    private static string NormalizeModValue(string value)
    {
        var normalized = VersionSuffixPattern.Replace(value.Trim(), "");
        normalized = IssueTrackerPattern.Replace(normalized, "");
        normalized = WhitespacePattern.Replace(normalized, " ").Trim();
        normalized = normalized.Trim('"', '\'', '[', ']', ' ');
        normalized = normalized.TrimEnd('.', ',', ';', ':');

        if (normalized.StartsWith("- ", StringComparison.Ordinal))
        {
            normalized = normalized[2..].Trim();
        }

        return normalized;
    }

    private static bool IsPlatformItem(string value)
    {
        var lower = value.ToLowerInvariant();
        var match = FirstTokenPattern.Match(lower);
        var token = match.Success ? match.Value : lower;
        return PlatformTokens.Contains(token);
    }

    private static void AddKey(HashSet<string> keys, string value)
    {
        var key = NormalizeKey(value);
        if (key.Length >= 3)
        {
            keys.Add(key);
        }
    }

    private static string NormalizeKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static void AddUnique(List<string> mods, string value)
    {
        if (mods.Count >= MaxSuspectedMods || value.Length == 0)
        {
            return;
        }

        if (mods.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        mods.Add(value);
    }

    private static List<string> BuildRecommendations(
        IReadOnlyList<CrashPattern> matched,
        IReadOnlyList<string> mods,
        bool tailMode)
    {
        var recommendations = new List<string>();

        if (mods.Count > 0)
        {
            var names = string.Join(", ", mods.Take(3).Select(mod => $"«{mod}»"));
            var others = mods.Count > 3 ? " и другие" : "";
            recommendations.Add($"Отключите подозрительные моды ({names}{others}) и запустите игру снова.");
            recommendations.Add("Обновите или откатите подозрительные моды до версии, совместимой " +
                                "с вашей версией Minecraft и Forge.");
        }

        foreach (var pattern in matched)
        {
            recommendations.Add(pattern.Recommendation);
        }

        if (matched.Count == 0)
        {
            recommendations.Add(tailMode
                ? "Явных известных ошибок в хвосте latest.log не найдено. Если игра закрылась, " +
                  "проверьте полный лог и выполните диагностику инстанса."
                : "Известные шаблоны не совпали. Просмотрите фрагмент отчёта и выполните диагностику " +
                  "инстанса: возможно, проблема в файлах игры или Java.");
        }

        recommendations.Add("Сохраните этот отчёт и приложите его к обращению в поддержку " +
                            "вместе с latest.log.");

        var unique = new List<string>();
        foreach (var recommendation in recommendations)
        {
            var value = recommendation.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (unique.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            unique.Add(value);
        }

        if (unique.Count > MaxRecommendations)
        {
            // Последняя рекомендация — всегда «приложите отчёт», её важно сохранить.
            var last = unique[^1];
            unique = unique.Take(MaxRecommendations - 1).ToList();
            unique.Add(last);
        }

        return unique;
    }

    private static string BuildExcerpt(
        string[] lines,
        string text,
        IReadOnlyList<CrashPattern> matched,
        bool tailMode)
    {
        if (lines.Length == 0)
        {
            return "";
        }

        var lineStarts = BuildLineStarts(text);
        var selected = new SortedSet<int>();
        var baseCount = matched.Count == 0 ? 60 : 30;

        if (tailMode)
        {
            for (var i = Math.Max(0, lines.Length - baseCount); i < lines.Length; i++)
            {
                selected.Add(i);
            }
        }
        else
        {
            for (var i = 0; i < Math.Min(baseCount, lines.Length); i++)
            {
                selected.Add(i);
            }
        }

        foreach (var pattern in matched)
        {
            foreach (var lineIndex in FindMatchLines(pattern, text, lineStarts))
            {
                var from = Math.Max(0, lineIndex - 3);
                var to = Math.Min(lines.Length - 1, lineIndex + 2);
                for (var i = from; i <= to; i++)
                {
                    selected.Add(i);
                }
            }
        }

        var ordered = selected.ToList();
        if (ordered.Count > MaxExcerptLines)
        {
            ordered = tailMode
                ? ordered.Skip(ordered.Count - MaxExcerptLines).ToList()
                : ordered.Take(MaxExcerptLines).ToList();
        }

        var builder = new StringBuilder();
        var previous = -2;

        foreach (var index in ordered)
        {
            if (index != previous + 1)
            {
                if (builder.Length > 0 || index > 0)
                {
                    builder.AppendLine("...");
                }
            }

            var line = lines[index].TrimEnd();
            if (line.Length > MaxExcerptLineChars)
            {
                line = line[..MaxExcerptLineChars] + "…";
            }

            builder.AppendLine(line);
            previous = index;

            if (builder.Length >= MaxExcerptChars)
            {
                builder.AppendLine("... (фрагмент сокращён)");
                break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static List<int> FindMatchLines(CrashPattern pattern, string text, int[] lineStarts)
    {
        var lines = new List<int>();
        try
        {
            foreach (Match match in pattern.Expression.Matches(text))
            {
                lines.Add(FindLineIndex(lineStarts, match.Index));
                if (lines.Count >= 3)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Таймаут одного шаблона не должен ломать весь отчёт.
        }

        return lines;
    }

    private static int FindLineIndex(int[] lineStarts, int position)
    {
        var index = Array.BinarySearch(lineStarts, position);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Max(0, index);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static bool TryReadHeader(string line, string header, out string value)
    {
        if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
        {
            value = line[header.Length..].Trim();
            return true;
        }

        value = "";
        return false;
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength].TrimEnd() + "…";
    }

    private static string? ResolveGameDir(InstanceProfile instance)
    {
        if (instance is null)
        {
            throw new LauncherException("Профиль не задан.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("В профиле не указана папка игры.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(instance.GameDir);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            throw new LauncherException($"Некорректный путь к папке игры: {instance.GameDir}", ex);
        }

        return Directory.Exists(fullPath) ? fullPath : null;
    }

    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException or NotSupportedException or IOException or
            System.Security.SecurityException;

    private static string ReadLimitedText(string path, bool tailOnly)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        if (length == 0)
        {
            return "";
        }

        if (tailOnly)
        {
            var count = (int)Math.Min(length, MaxLogTailBytes);
            stream.Seek(length - count, SeekOrigin.Begin);
            var tail = new byte[count];
            ReadExactly(stream, tail);

            var start = count == length ? 0 : FirstCompleteLineOffset(tail);
            var tailEncoding = LogEncoding.Detect(tail, start, tail.Length - start);
            return CleanText(LogEncoding.Decode(tail, start, tail.Length - start, tailEncoding));
        }

        if (length <= MaxReadBytes)
        {
            var all = new byte[(int)length];
            ReadExactly(stream, all);
            return CleanText(LogEncoding.Decode(all, all.Length));
        }

        var first = new byte[SegmentBytes];
        stream.Seek(0, SeekOrigin.Begin);
        ReadExactly(stream, first);

        var lastCount = (int)Math.Min(length, SegmentBytes);
        var last = new byte[lastCount];
        stream.Seek(length - lastCount, SeekOrigin.Begin);
        ReadExactly(stream, last);

        var lastStart = FirstCompleteLineOffset(last);
        var lastEncoding = LogEncoding.Detect(last, lastStart, last.Length - lastStart);

        return CleanText(
            LogEncoding.Decode(first, first.Length) +
            "\n\n... (середина файла пропущена) ...\n\n" +
            LogEncoding.Decode(last, lastStart, last.Length - lastStart, lastEncoding));
    }

    private static int FirstCompleteLineOffset(byte[] bytes)
    {
        var newLine = Array.IndexOf(bytes, (byte)'\n');
        return newLine >= 0 && newLine + 1 < bytes.Length ? newLine + 1 : 0;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }
    }

    private static string CleanText(string text) =>
        text.TrimStart('\uFEFF')
            .Replace("\0", "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
}
