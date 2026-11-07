using System.Text;
using System.Text.Json;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Install;

/// <summary>Результат установки Fabric: id версии, версии loader и installer.</summary>
public sealed record FabricInstallResult(
    string VersionId,
    string LoaderVersion,
    string InstallerVersion);

/// <summary>
/// Устанавливает Fabric loader официальным installer.jar. Базовый ванильный
/// Minecraft должен быть установлен вызывающим кодом.
/// </summary>
public sealed class FabricInstaller
{
    public const string InstallerMetadataUrl =
        "https://meta.fabricmc.net/v2/versions/installer";

    public const string LoaderMetadataUrl =
        "https://meta.fabricmc.net/v2/versions/loader";

    private const int LogTailLength = 5000;

    private readonly DownloadService _downloads;

    public FabricInstaller(DownloadService? downloads = null)
    {
        _downloads = downloads ?? DownloadService.Shared;
    }

    /// <summary>
    /// Устанавливает Fabric для указанной версии Minecraft и возвращает id версии.
    /// Повторный вызов на готовом инстансе не запускает installer.
    /// </summary>
    public async Task<FabricInstallResult> EnsureFabricAsync(
        string gameDir,
        string javaPath,
        string minecraftVersion,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для установки Fabric.");
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new LauncherException("Не указан путь к Java для установки Fabric.");
        }

        var mcVersion = NormalizeVersionPart(minecraftVersion, "Minecraft");

        gameDir = Path.GetFullPath(gameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Поиск Fabric loader для Minecraft {mcVersion}",
            0,
            0));

        var loaderVersion = NormalizeVersionPart(
            await GetLoaderVersionAsync(mcVersion, cancellationToken).ConfigureAwait(false),
            "Fabric loader");
        var versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
        var paths = new VersionPaths(gameDir);
        var versionJson = paths.VersionJson(versionId);

        if (IsFabricInstalled(versionJson))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Fabric {loaderVersion} уже установлен",
                0,
                0));

            return new FabricInstallResult(versionId, loaderVersion, "");
        }

        if (!string.Equals(javaPath, "java", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(javaPath))
        {
            throw new LauncherException($"Не найдена Java для установки Fabric: {javaPath}");
        }

        var installer = await GetInstallerAsync(cancellationToken).ConfigureAwait(false);
        var installerVersion = NormalizeVersionPart(installer.Version, "Fabric installer");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "SamaraLauncher");
        try
        {
            Directory.CreateDirectory(tempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать временную папку {tempDirectory}: {ex.Message}", ex);
        }

        var installerPath = Path.Combine(
            tempDirectory,
            $"fabric-installer-{installerVersion}-{Guid.NewGuid():N}.jar");

        try
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Скачивание Fabric installer {installerVersion}",
                0,
                0));

            await _downloads.DownloadFileAsync(
                installer.Url,
                installerPath,
                progress: progress,
                stage: ProgressStage.Minecraft,
                displayName: $"Fabric installer {installerVersion}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await RunInstallerAsync(
                javaPath,
                installerPath,
                gameDir,
                mcVersion,
                loaderVersion,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!IsFabricInstalled(versionJson))
            {
                throw new LauncherException(
                    $"Fabric installer завершился успешно, но не создал корректный {versionJson}. " +
                    "Проверьте лог установки.");
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Fabric {loaderVersion} установлен",
                0,
                0));

            return new FabricInstallResult(versionId, loaderVersion, installerVersion);
        }
        finally
        {
            TryDelete(installerPath);
        }
    }

    private async Task<string> GetLoaderVersionAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"{LoaderMetadataUrl}/{Uri.EscapeDataString(minecraftVersion)}";

        using var document = await _downloads
            .GetJsonAsync(url, cancellationToken)
            .ConfigureAwait(false);

        var version = SelectStableVersion(
            document.RootElement,
            "loader",
            "Fabric loader");

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new LauncherException(
                $"В каталоге Fabric не найден loader для Minecraft {minecraftVersion}. " +
                "Проверьте версию Minecraft и повторите попытку.");
        }

        return version;
    }

    private async Task<(string Version, string Url)> GetInstallerAsync(
        CancellationToken cancellationToken)
    {
        using var document = await _downloads
            .GetJsonAsync(InstallerMetadataUrl, cancellationToken)
            .ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new LauncherException(
                "Каталог Fabric installer вернул неожиданный формат.");
        }

        (string Version, string Url)? fallback = null;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = versionElement.GetString()?.Trim() ?? "";
            var url = item.TryGetProperty("url", out var urlElement) &&
                      urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()?.Trim() ?? ""
                : "";

            if (version.Length == 0 || url.Length == 0)
            {
                continue;
            }

            fallback ??= (version, url);

            if (item.TryGetProperty("stable", out var stable) &&
                stable.ValueKind == JsonValueKind.True)
            {
                return (version, url);
            }
        }

        if (fallback is null)
        {
            throw new LauncherException(
                "В каталоге Fabric не найден installer. Проверьте интернет и повторите попытку.");
        }

        return fallback.Value;
    }

    /// <summary>
    /// Выбирает первый stable-элемент; если stable нет — первый подходящий.
    /// Для loader версия лежит во вложенном объекте <c>loader</c>.
    /// </summary>
    private static string? SelectStableVersion(
        JsonElement root,
        string? nestedProperty,
        string description)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new LauncherException(
                $"Каталог {description} вернул неожиданный формат.");
        }

        string? fallback = null;

        foreach (var item in root.EnumerateArray())
        {
            var source = item;

            if (nestedProperty is not null)
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty(nestedProperty, out source) ||
                    source.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
            }

            if (!source.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = versionElement.GetString()?.Trim() ?? "";
            if (version.Length == 0)
            {
                continue;
            }

            fallback ??= version;

            if (source.TryGetProperty("stable", out var stable) &&
                stable.ValueKind == JsonValueKind.True)
            {
                return version;
            }
        }

        return fallback;
    }

    private static async Task RunInstallerAsync(
        string javaPath,
        string installerPath,
        string gameDir,
        string minecraftVersion,
        string loaderVersion,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Установка Fabric {loaderVersion} (это может занять несколько минут)",
            0,
            0));

        var result = await ProcessHost.RunAsync(
            javaPath,
            new[]
            {
                "-jar", installerPath,
                "client",
                "-dir", gameDir,
                "-mcversion", minecraftVersion,
                "-loader", loaderVersion
            },
            gameDir,
            TimeSpan.FromMinutes(30),
            cancellationToken,
            onLine: line => progress?.Report(new LauncherProgress(ProgressStage.Minecraft, line)),
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                "Fabric installer не завершился за 30 минут и был остановлен вместе с дочерними процессами.");
        }

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"Fabric installer завершился с кодом {result.ExitCode}.{Environment.NewLine}" +
                Tail(result.Output));
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            "Fabric installer завершил работу",
            0,
            0));
    }

    private static string Tail(string log)
    {
        var normalized = log
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length == 0)
        {
            return "Лог установщика пуст.";
        }

        return normalized.Length <= LogTailLength
            ? normalized
            : "...\n" + normalized[^LogTailLength..];
    }

    private static bool IsFabricInstalled(string versionJsonPath)
    {
        if (!File.Exists(versionJsonPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("mainClass", out var mainClass) &&
                   mainClass.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(mainClass.GetString()) &&
                   document.RootElement.TryGetProperty("libraries", out _);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeVersionPart(string? value, string description)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 ||
            text is "." or ".." ||
            text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            text.Contains(Path.DirectorySeparatorChar) ||
            text.Contains(Path.AltDirectorySeparatorChar) ||
            text.Contains(':'))
        {
            throw new LauncherException(
                $"Некорректная версия {description} для установки Fabric: \"{value}\".");
        }

        return text;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный установщик не критичен.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }
}
