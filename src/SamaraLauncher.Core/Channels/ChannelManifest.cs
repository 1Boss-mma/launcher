namespace SamaraLauncher.Core.Channels;

/// <summary>
/// Релиз лаунчера из latest.json канала обновлений.
/// </summary>
public sealed class ChannelManifest
{
    public string Version { get; set; } = "";

    public UpdateChannel Channel { get; set; }

    public string Url { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string Notes { get; set; } = "";

    public bool Mandatory { get; set; }

    /// <summary>
    /// Минимальная установленная версия лаунчера, с которой разрешён этот релиз.
    /// Пустая строка — ограничения нет.
    /// </summary>
    public string MinVersion { get; set; } = "";

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Размер exe в байтах. ChannelService требует положительное значение.</summary>
    public long Size { get; set; }

    /// <summary>
    /// Проверяет, что текущая версия не ниже <see cref="MinVersion"/>.
    /// Некорректные версии считаются несовместимыми.
    /// </summary>
    public bool IsCompatibleWith(string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(MinVersion))
        {
            return true;
        }

        if (!SemVer.TryParse(currentVersion, out var current)
            || !SemVer.TryParse(MinVersion, out var minimum))
        {
            return false;
        }

        return current.CompareTo(minimum) >= 0;
    }
}
