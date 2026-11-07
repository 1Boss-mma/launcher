namespace SamaraLauncher.Core.Diagnostics;

/// <summary>Категория проверки Doctor.</summary>
public enum DoctorCategory
{
    Java,
    Files,
    Mods,
    Network,
    Disk,
    Config,
    Logs
}

/// <summary>Результат отдельной проверки Doctor.</summary>
public enum DoctorStatus
{
    Ok,
    Warning,
    Error,
    Skipped
}

/// <summary>
/// Один пункт диагностики: что проверяли, что нашли и можно ли исправить автоматически.
/// </summary>
public sealed class DoctorCheck
{
    /// <summary>Стабильный идентификатор проверки, например <c>java</c> или <c>gamedir.exists</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>Короткое название проверки для интерфейса.</summary>
    public string Title { get; set; } = "";

    public DoctorCategory Category { get; set; } = DoctorCategory.Files;

    public DoctorStatus Status { get; set; } = DoctorStatus.Ok;

    /// <summary>Краткое сообщение по-русски.</summary>
    public string Message { get; set; } = "";

    /// <summary>Подробности: пути, версии, список файлов. Может быть пустым.</summary>
    public string Details { get; set; } = "";

    /// <summary>Может ли <see cref="DoctorFixService"/> безопасно исправить этот пункт.</summary>
    public bool CanAutoFix { get; set; }

    /// <summary>Что сделать пользователю, если проблему нельзя исправить автоматически.</summary>
    public string FixHint { get; set; } = "";
}
