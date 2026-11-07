namespace SamaraLauncher.Core.Models;

/// <summary>
/// Ключи и подписи загрузчиков версии Minecraft. В профиле хранится ключ
/// (<c>vanilla</c>, <c>forge</c>, <c>fabric</c>, <c>optifine</c>), а в UI — подпись.
/// </summary>
public static class MinecraftLoader
{
    public const string Vanilla = "vanilla";
    public const string Forge = "forge";
    public const string Fabric = "fabric";
    public const string OptiFine = "optifine";

    /// <summary>Приводит ключ загрузчика к нижнему регистру; неизвестное значение — пустая строка.</summary>
    public static string Normalize(string? loader)
    {
        var value = loader?.Trim() ?? "";
        return value.ToLowerInvariant() switch
        {
            Vanilla => Vanilla,
            Forge => Forge,
            Fabric => Fabric,
            OptiFine => OptiFine,
            _ => ""
        };
    }

    /// <summary>Подпись загрузчика для интерфейса.</summary>
    public static string Label(string? loader) => Normalize(loader) switch
    {
        Forge => "Forge",
        Fabric => "Fabric",
        OptiFine => "OptiFine",
        _ => "Обычная"
    };

    /// <summary>
    /// Пытается определить базовую версию Minecraft по id загрузчика:
    /// <c>1.20.1-forge-47.4.28</c> → <c>1.20.1</c>,
    /// <c>fabric-loader-0.16.10-1.21.1</c> → <c>1.21.1</c>.
    /// </summary>
    public static string GuessBaseVersion(string? versionId)
    {
        var id = versionId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return "";
        }

        var forgeMarker = id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        if (forgeMarker > 0)
        {
            return id[..forgeMarker];
        }

        if (id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var lastDash = id.LastIndexOf('-');
            return lastDash > 0 && lastDash < id.Length - 1
                ? id[(lastDash + 1)..]
                : id;
        }

        var optifineMarker = id.IndexOf("optifine", StringComparison.OrdinalIgnoreCase);
        if (optifineMarker > 0)
        {
            var prefix = id[..optifineMarker].TrimEnd('-', '_', ' ');
            if (prefix.Length > 0)
            {
                return prefix;
            }
        }

        return id;
    }
}
