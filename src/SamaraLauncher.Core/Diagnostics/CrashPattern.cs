using System.Text.RegularExpressions;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Категория шаблона краша — помогает группировать причины в интерфейсе.
/// </summary>
public enum CrashPatternCategory
{
    /// <summary>Нехватка памяти JVM.</summary>
    Memory,

    /// <summary>Java, байткод и версия JVM.</summary>
    Java,

    /// <summary>Моды, загрузчик, зависимости и миксины.</summary>
    Mods,

    /// <summary>Графика, OpenGL, драйверы и шейдеры.</summary>
    Graphics,

    /// <summary>Файлы, ресурсы и права доступа.</summary>
    Files,

    /// <summary>Зависания и серверный тик.</summary>
    Performance,

    /// <summary>Прочие причины.</summary>
    Other
}

/// <summary>
/// Один шаблон поиска причины краша: регулярное выражение, категория,
/// важность и готовая рекомендация пользователю.
/// </summary>
public sealed class CrashPattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Создаёт шаблон. Регулярное выражение компилируется с игнорированием регистра
    /// и таймаутом, чтобы битый или огромный лог не подвесил лаунчер.
    /// </summary>
    /// <param name="id">Стабильный идентификатор шаблона.</param>
    /// <param name="title">Человекочитаемое название для отчёта.</param>
    /// <param name="category">Категория причины.</param>
    /// <param name="pattern">Регулярное выражение (регистр не важен).</param>
    /// <param name="recommendation">Что посоветовать пользователю.</param>
    /// <param name="severity">Важность найденной причины.</param>
    public CrashPattern(
        string id,
        string title,
        CrashPatternCategory category,
        string pattern,
        string recommendation,
        CrashSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Не указан идентификатор шаблона.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Не указано название шаблона.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Не указано регулярное выражение шаблона.", nameof(pattern));
        }

        if (string.IsNullOrWhiteSpace(recommendation))
        {
            throw new ArgumentException("Не указана рекомендация шаблона.", nameof(recommendation));
        }

        try
        {
            Expression = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Некорректное регулярное выражение шаблона «{id}»: {ex.Message}",
                nameof(pattern),
                ex);
        }

        Id = id;
        Title = title;
        Category = category;
        Pattern = pattern;
        Recommendation = recommendation;
        Severity = severity;
    }

    /// <summary>Стабильный идентификатор шаблона, например <c>out-of-memory</c>.</summary>
    public string Id { get; }

    /// <summary>Название шаблона для списка совпадений.</summary>
    public string Title { get; }

    /// <summary>Категория причины.</summary>
    public CrashPatternCategory Category { get; }

    /// <summary>Исходное регулярное выражение.</summary>
    public string Pattern { get; }

    /// <summary>Рекомендация, которую нужно показать при совпадении.</summary>
    public string Recommendation { get; }

    /// <summary>Важность причины.</summary>
    public CrashSeverity Severity { get; }

    /// <summary>Скомпилированное выражение для внутреннего поиска по тексту.</summary>
    internal Regex Expression { get; }

    /// <summary>
    /// Проверяет, встречается ли шаблон в тексте. Таймаут регулярного выражения
    /// считается отсутствием совпадения и не роняет анализ.
    /// </summary>
    public bool IsMatch(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return Expression.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
