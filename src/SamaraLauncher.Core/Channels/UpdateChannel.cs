namespace SamaraLauncher.Core.Channels;

/// <summary>Канал обновлений лаунчера.</summary>
public enum UpdateChannel
{
    Stable = 0,
    Beta = 1,
    Dev = 2
}

/// <summary>Разбор каналов и их имена в latest.json и rollback.json.</summary>
public static class UpdateChannels
{
    /// <summary>Разбирает stable/beta/dev без учёта регистра.</summary>
    public static UpdateChannel Parse(string? value)
    {
        if (TryParse(value, out var channel))
        {
            return channel;
        }

        throw new LauncherException(
            $"Неизвестный канал обновления: \"{value}\". Допустимо: stable, beta, dev.");
    }

    /// <summary>Пробует разобрать stable/beta/dev без учёта регистра.</summary>
    public static bool TryParse(string? value, out UpdateChannel channel)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "stable":
                channel = UpdateChannel.Stable;
                return true;
            case "beta":
                channel = UpdateChannel.Beta;
                return true;
            case "dev":
                channel = UpdateChannel.Dev;
                return true;
            default:
                channel = UpdateChannel.Stable;
                return false;
        }
    }

    /// <summary>Строчное имя канала для JSON и имён файлов: stable/beta/dev.</summary>
    public static string ToString(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Dev => "dev",
        _ => throw new LauncherException($"Неизвестный канал обновления: {channel}.")
    };

    /// <summary>Строчное имя канала — псевдоним <see cref="ToString(UpdateChannel)"/>.</summary>
    public static string ToWireName(this UpdateChannel channel) => ToString(channel);

    /// <summary>Имя канала для интерфейса.</summary>
    public static string ToDisplayName(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "Stable",
        UpdateChannel.Beta => "Beta",
        UpdateChannel.Dev => "Dev",
        _ => channel.ToString()
    };
}
