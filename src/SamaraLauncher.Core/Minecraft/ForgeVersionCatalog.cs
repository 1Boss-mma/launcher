using System.Xml;
using System.Xml.Linq;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Каталог версий Forge из maven-metadata.xml. Позволяет выбрать последнюю
/// версию Forge для конкретного релиза Minecraft.
/// </summary>
public sealed class ForgeVersionCatalog
{
    public const string MetadataUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    private readonly DownloadService _downloads;

    public ForgeVersionCatalog(DownloadService? downloads = null)
    {
        _downloads = downloads ?? DownloadService.Shared;
    }

    /// <summary>
    /// Возвращает последнюю версию Forge для указанного Minecraft (например, "47.4.28").
    /// </summary>
    public async Task<string> GetLatestAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var mcVersion = minecraftVersion?.Trim() ?? "";
        if (mcVersion.Length == 0)
        {
            throw new LauncherException("Не указана версия Minecraft для поиска Forge.");
        }

        var xml = await _downloads
            .GetStringAsync(MetadataUrl, cancellationToken)
            .ConfigureAwait(false);

        var latest = ParseLatest(xml, mcVersion);
        if (string.IsNullOrWhiteSpace(latest))
        {
            throw new LauncherException(
                $"В каталоге Forge не найдена версия для Minecraft {mcVersion}. " +
                "Проверьте версию Minecraft и повторите попытку.");
        }

        return latest;
    }

    /// <summary>
    /// Разбирает maven-metadata.xml и возвращает максимальную версию Forge
    /// для точного префикса <c>{minecraftVersion}-</c>. null — подходящих версий нет.
    /// </summary>
    public static string? ParseLatest(string xml, string minecraftVersion)
    {
        var mcVersion = minecraftVersion?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(xml) || mcVersion.Length == 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new LauncherException(
                $"Не удалось разобрать каталог Forge: {ex.Message}", ex);
        }

        var prefix = mcVersion + "-";
        string? best = null;

        foreach (var element in document.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = element.Value.Trim();
            if (value.Length <= prefix.Length ||
                !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = value[prefix.Length..];
            if (candidate.Length == 0)
            {
                continue;
            }

            if (best is null || CompareVersions(candidate, best) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) &&
            Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return VersionText.Compare(left, right);
    }
}
