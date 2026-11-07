using System.IO.Compression;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Java;

/// <summary>
/// Скачивает и распаковывает Eclipse Temurin JRE для Windows x64 в runtime инстанса.
/// Поддерживаются Java 8, 17 и 21.
/// </summary>
public sealed class JavaRuntimeInstaller
{
    /// <summary>Официальный редирект Adoptium на последнюю сборку Temurin 17 JRE.</summary>
    public const string Temurin17Url =
        "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jre/hotspot/normal/eclipse";

    private const string AdoptiumUrlTemplate =
        "https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/x64/jre/hotspot/normal/eclipse";

    private readonly DownloadService _downloads;

    public JavaRuntimeInstaller(DownloadService? downloads = null)
    {
        _downloads = downloads ?? DownloadService.Shared;
    }

    /// <summary>Адрес последнего Temurin JRE указанной major-версии для Windows x64.</summary>
    public static string GetTemurinUrl(int major)
    {
        if (!IsSupportedMajor(major))
        {
            throw new LauncherException(
                $"Java {major} не поддерживается: доступны Java 8, 17 и 21.");
        }

        return string.Format(AdoptiumUrlTemplate, major);
    }

    /// <summary>
    /// Гарантирует наличие Java 17 в <c>&lt;gameDir&gt;\runtime\jre-17</c>.
    /// Оставлено для совместимости; новый код может использовать перегрузку с major.
    /// </summary>
    public Task<JavaInfo> EnsureInstalledAsync(
        string gameDir,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return EnsureInstalledAsync(gameDir, 17, progress, cancellationToken);
    }

    /// <summary>
    /// Гарантирует наличие Java указанной major-версии в
    /// <c>&lt;gameDir&gt;\runtime\jre-{major}</c>. Если java.exe уже есть,
    /// повторно ничего не скачивается.
    /// </summary>
    public async Task<JavaInfo> EnsureInstalledAsync(
        string gameDir,
        int major,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedMajor(major))
        {
            throw new LauncherException(
                $"Java {major} не поддерживается: доступны Java 8, 17 и 21.");
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для установки Java.");
        }

        gameDir = Path.GetFullPath(gameDir);
        var runtimeDir = Path.Combine(gameDir, "runtime", $"jre-{major}");

        var existing = FindJavaExe(runtimeDir);
        if (existing is not null)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Java {major} уже установлена",
                0,
                0));

            var existingInfo = JavaLocator.TryProbe(existing);
            if (existingInfo is null || !IsCompatible(existingInfo, major))
            {
                throw new LauncherException(
                    $"В {runtimeDir} найден java.exe, но он не запускается или не подходит для Java {major}: {existing}. " +
                    $"Удалите папку runtime\\jre-{major} и повторите установку.");
            }

            return existingInfo;
        }

        try
        {
            Directory.CreateDirectory(runtimeDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку Java {runtimeDir}: {ex.Message}", ex);
        }

        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"samara-temurin{major}-{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Скачивание Java {major} (Eclipse Temurin JRE)",
                0,
                0));

            await _downloads.DownloadFileAsync(
                GetTemurinUrl(major),
                archivePath,
                progress: progress,
                stage: ProgressStage.Java,
                displayName: $"Java {major} (Temurin JRE)",
                cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Распаковка Java {major}",
                0,
                0));

            try
            {
                ZipFile.ExtractToDirectory(archivePath, runtimeDir, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                throw new LauncherException(
                    $"Скачанный архив Java повреждён. Удалите runtime\\jre-{major} и повторите установку.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось распаковать Java в {runtimeDir}: {ex.Message}. " +
                    "Проверьте свободное место и права на запись.", ex);
            }

            var javaPath = FindJavaExe(runtimeDir);
            if (javaPath is null)
            {
                throw new LauncherException(
                    $"После распаковки Java в {runtimeDir} не найден bin\\java.exe. " +
                    "Архив Temurin имеет неожиданную структуру.");
            }

            var info = JavaLocator.TryProbe(javaPath);
            if (info is null || !IsCompatible(info, major))
            {
                throw new LauncherException(
                    $"Java распакована в {javaPath}, но не запускается или не подходит для Java {major}. " +
                    $"Удалите runtime\\jre-{major} и повторите установку.");
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Java {info.Version} готова",
                0,
                0));

            return info;
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch (IOException)
            {
                // Временный архив не критичен.
            }
            catch (UnauthorizedAccessException)
            {
                // То же.
            }
        }
    }

    private static bool IsSupportedMajor(int major) => major is 8 or 17 or 21;

    private static bool IsCompatible(JavaInfo info, int major) =>
        major == 8 ? info.MajorVersion == 8 : info.MajorVersion >= major;

    private static string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
