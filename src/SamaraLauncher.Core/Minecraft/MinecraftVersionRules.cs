namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Небольшие правила, зависящие от версии Minecraft: legacy-ассеты и Forge-суффикс.
/// </summary>
public static class MinecraftVersionRules
{
    /// <summary>Является ли id версии профилем Forge (например, 1.20.1-forge-47.4.28).</summary>
    public static bool IsForgeVersionId(string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId) &&
        versionId.Contains("-forge-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Убирает суффикс -forge-... из id версии.</summary>
    public static string StripForgeSuffix(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return "";
        }

        var text = versionId.Trim();
        var marker = text.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? text[..marker] : text;
    }

    /// <summary>
    /// Старые версии (до 1.7) используют legacy asset index и виртуальную папку
    /// <c>assets/virtual/&lt;index&gt;</c>.
    /// </summary>
    public static bool IsLegacyAssets(string? versionId, string? assets, string? assetIndexId)
    {
        if (IsLegacyAssetsValue(assets) || IsLegacyAssetsValue(assetIndexId))
        {
            return true;
        }

        var id = StripForgeSuffix(versionId).Trim();
        if (id.Length == 0)
        {
            return false;
        }

        if ((id[0] is 'a' or 'A' or 'b' or 'B' or 'c' or 'C') &&
            id.Length > 1 &&
            char.IsDigit(id[1]))
        {
            return true;
        }

        var parts = id.Split('.');
        return parts.Length >= 2 &&
               parts[0] == "1" &&
               int.TryParse(parts[1], out var minor) &&
               minor <= 6;
    }

    private static bool IsLegacyAssetsValue(string? value) =>
        string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "pre-1.6", StringComparison.OrdinalIgnoreCase);
}
