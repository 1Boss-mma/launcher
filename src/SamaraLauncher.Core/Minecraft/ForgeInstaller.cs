using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Устанавливает Forge официальным installer.jar: проверенную версию 1.20.1/47.4.28
/// и произвольные версии для других релизов Minecraft.
/// </summary>
public sealed class ForgeInstaller
{
    public const string MinecraftVersion = "1.20.1";
    public const string ForgeVersion = "47.4.28";
    public const string VersionId = "1.20.1-forge-47.4.28";

    public const string InstallerUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/" +
        "forge-1.20.1-47.4.28-installer.jar";

    private const string InstallerBaseUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge";

    private const int LogTailLength = 5000;

    private readonly DownloadService _downloads;

    public ForgeInstaller(DownloadService? downloads = null)
    {
        _downloads = downloads ?? DownloadService.Shared;
    }

    /// <summary>
    /// Запускает Forge installer для проверенной версии 1.20.1/47.4.28, если она
    /// ещё не установлена. Поведение и сообщения сохранены для старых профилей.
    /// </summary>
    /// <param name="gameDir">Папка инстанса.</param>
    /// <param name="javaPath">Путь к java.exe (Java 17+).</param>
    /// <param name="progress">Прогресс установки.</param>
    /// <param name="cancellationToken">Отмена установки.</param>
    public async Task EnsureForgeAsync(
        string gameDir,
        string javaPath,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureForgeAsync(
            gameDir,
            javaPath,
            MinecraftVersion,
            ForgeVersion,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Устанавливает указанную версию Forge для релиза Minecraft. Возвращает id
    /// созданной версии (<c>{mc}-forge-{forge}</c>).
    /// </summary>
    /// <param name="gameDir">Папка инстанса.</param>
    /// <param name="javaPath">Путь к java.exe.</param>
    /// <param name="minecraftVersion">Версия Minecraft, например "1.20.1".</param>
    /// <param name="forgeVersion">Версия Forge, например "47.4.28".</param>
    /// <param name="progress">Прогресс установки.</param>
    /// <param name="cancellationToken">Отмена установки.</param>
    public async Task<string> EnsureForgeAsync(
        string gameDir,
        string javaPath,
        string minecraftVersion,
        string forgeVersion,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mcVersion = NormalizeVersionPart(minecraftVersion, "Minecraft");
        var forge = NormalizeVersionPart(forgeVersion, "Forge");
        var versionId = $"{mcVersion}-forge-{forge}";

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для установки Forge.");
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new LauncherException("Не указан путь к Java для установки Forge.");
        }

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

        var paths = new VersionPaths(gameDir);
        var versionJson = paths.VersionJson(versionId);

        if (IsForgeInstalled(versionJson))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Forge {forge} уже установлен",
                0,
                0));

            return versionId;
        }

        if (!string.Equals(javaPath, "java", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(javaPath))
        {
            throw new LauncherException($"Не найдена Java для установки Forge: {javaPath}");
        }

        EnsureLauncherProfiles(gameDir);

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

        var installerUrl = BuildInstallerUrl(mcVersion, forge);
        var installerPath = Path.Combine(
            tempDirectory,
            $"forge-{versionId}-{Guid.NewGuid():N}-installer.jar");

        try
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Скачивание Forge installer {forge}",
                0,
                0));

            var installerSha1 = await TryGetInstallerSha1Async(
                installerUrl,
                cancellationToken).ConfigureAwait(false);

            await _downloads.DownloadFileAsync(
                installerUrl,
                installerPath,
                sha1: installerSha1,
                progress: progress,
                stage: ProgressStage.Forge,
                displayName: $"Forge {forge} installer",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await RunInstallerAsync(
                javaPath,
                installerPath,
                gameDir,
                forge,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!IsForgeInstalled(versionJson))
            {
                throw new LauncherException(
                    $"Forge installer завершился успешно, но не создал корректный {versionJson}. " +
                    "Проверьте лог установки.");
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Forge {forge} установлен",
                0,
                0));

            return versionId;
        }
        finally
        {
            TryDelete(installerPath);
        }
    }

    private static string BuildInstallerUrl(string minecraftVersion, string forgeVersion) =>
        $"{InstallerBaseUrl}/{minecraftVersion}-{forgeVersion}/" +
        $"forge-{minecraftVersion}-{forgeVersion}-installer.jar";

    /// <summary>
    /// Скачивает официальный .sha1 рядом с installer.jar. Если файл недоступен,
    /// возвращает null — установка продолжится без проверки (совместимость).
    /// </summary>
    private async Task<string?> TryGetInstallerSha1Async(
        string installerUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await _downloads
                .GetStringAsync(installerUrl + ".sha1", cancellationToken)
                .ConfigureAwait(false);

            var token = text
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => value.Length == 40 && value.All(Uri.IsHexDigit));

            return token?.ToLowerInvariant();
        }
        catch (LauncherException)
        {
            return null;
        }
    }

    private static async Task RunInstallerAsync(
        string javaPath,
        string installerPath,
        string gameDir,
        string forgeVersion,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new LauncherProgress(
            ProgressStage.Forge,
            $"Установка Forge {forgeVersion} (это может занять несколько минут)",
            0,
            0));

        // ProcessHost запускает java с CREATE_NO_WINDOW. Дочерние ConsoleTool/jarsplitter
        // наследуют отсутствие консоли, поэтому свои окна не создают.
        var result = await ProcessHost.RunAsync(
            javaPath,
            new[] { "-jar", installerPath, "--installClient", gameDir },
            gameDir,
            TimeSpan.FromMinutes(30),
            cancellationToken,
            onLine: line => progress?.Report(new LauncherProgress(ProgressStage.Forge, line)),
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                "Forge installer не завершился за 30 минут и был остановлен вместе с дочерними процессами.");
        }

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"Forge installer завершился с кодом {result.ExitCode}.{Environment.NewLine}" +
                Tail(result.Output));
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Forge,
            "Forge installer завершил работу",
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

    private static bool IsForgeInstalled(string versionJsonPath)
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

    private static void EnsureLauncherProfiles(string gameDir)
    {
        var path = Path.Combine(gameDir, "launcher_profiles.json");
        if (File.Exists(path))
        {
            return;
        }

        const string json = """
            {
              "profiles": {},
              "settings": {
                "enableSnapshots": false,
                "keepLauncherOpen": false
              },
              "version": 3
            }
            """;

        try
        {
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать {path}: {ex.Message}. Проверьте права на папку игры.", ex);
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
                $"Некорректная версия {description} для установки Forge: \"{value}\".");
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
