using System.Text.Json;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Загрузка version json из папки versions инстанса.
/// </summary>
public static class VersionManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static VersionManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new LauncherException($"Не найден файл версии: {path}");
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<VersionManifest>(json, Options)
                   ?? throw new LauncherException($"Пустой файл версии: {path}");
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный JSON версии {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ищет Forge-версию в инстансе, если в профиле не указана явно.
    /// </summary>
    public static string DetectVersionId(string gameDir, string? preferredMinecraftVersion = null)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            throw new LauncherException($"В инстансе нет папки versions: {versionsDir}");
        }

        var candidates = Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => File.Exists(Path.Combine(versionsDir, name!, name + ".json")))
            .Where(name => name!.Contains("forge", StringComparison.OrdinalIgnoreCase))
            .Where(name => string.IsNullOrWhiteSpace(preferredMinecraftVersion)
                           || name!.StartsWith(preferredMinecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name!, Comparer<string>.Create(VersionText.Compare))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new LauncherException(
                $"Не удалось определить Forge-версию в {versionsDir}. Укажите versionId в профиле.");
        }

        return candidates[0]!;
    }
}
