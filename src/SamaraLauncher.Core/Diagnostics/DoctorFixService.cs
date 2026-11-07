using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Безопасные автоисправления по отчёту <see cref="DoctorService"/>.
/// Создаёт только пустые служебные папки и удаляет файлы <c>*.tmp</c> внутри
/// versions/libraries/assets. Моды, миры, конфиги и другие файлы пользователя не трогает.
/// </summary>
public sealed class DoctorFixService
{
    private static readonly string[] RequiredDirectories = { "logs", "crash-reports", "mods", "config" };
    private static readonly string[] TempRoots = { "versions", "libraries", "assets" };

    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>
    /// Исправляет только те пункты отчёта, у которых <see cref="DoctorCheck.CanAutoFix"/> = true
    /// и статус Error/Warning. Возвращает количество исправленных пунктов (не файлов).
    /// Ошибки отдельных исправлений не прерывают остальные.
    /// </summary>
    public async Task<int> FixAsync(
        InstanceProfile instance,
        DoctorReport report,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException(
                "У инстанса не указана папка игры — автоисправление невозможно.");
        }

        string gameDir;
        try
        {
            gameDir = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(instance.GameDir.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Некорректный путь к папке игры: {instance.GameDir}",
                ex);
        }

        var fixIds = report.Checks
            .Where(check => check.CanAutoFix &&
                            check.Status is DoctorStatus.Error or DoctorStatus.Warning)
            .Select(check => check.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (fixIds.Count == 0)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                "Doctor: нечего исправлять."));
            return 0;
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            "Doctor: начало автоисправления..."));

        var fixedCount = 0;

        if (fixIds.Contains("gamedir.exists"))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                "Doctor: создание папки игры..."));

            try
            {
                var created = await Task.Run(() => EnsureDirectory(gameDir, ct), ct)
                    .ConfigureAwait(false);

                if (created)
                {
                    fixedCount++;
                    progress?.Report(new LauncherProgress(
                        ProgressStage.Verify,
                        $"Doctor: папка игры создана: {gameDir}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                progress?.Report(new LauncherProgress(
                    ProgressStage.Verify,
                    $"Doctor: не удалось создать папку игры: {ex.Message}"));
            }
        }

        if (fixIds.Contains("files.directories"))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                "Doctor: создание служебных папок..."));

            var created = await Task.Run(() =>
            {
                var count = 0;

                foreach (var name in RequiredDirectories)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        if (EnsureDirectory(Path.Combine(gameDir, name), ct))
                        {
                            count++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // Остальные папки всё равно пробуем создать.
                    }
                }

                return count;
            }, ct).ConfigureAwait(false);

            if (created > 0)
            {
                fixedCount++;
                progress?.Report(new LauncherProgress(
                    ProgressStage.Verify,
                    $"Doctor: создано служебных папок: {created}."));
            }
        }

        if (fixIds.Contains("files.temp"))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                "Doctor: удаление временных файлов *.tmp..."));

            var deleted = await Task.Run(() => DeleteTempFiles(gameDir, ct), ct)
                .ConfigureAwait(false);

            if (deleted > 0)
            {
                fixedCount++;
                progress?.Report(new LauncherProgress(
                    ProgressStage.Verify,
                    $"Doctor: удалено временных файлов: {deleted}."));
            }
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            $"Doctor: автоисправление завершено, исправлено пунктов: {fixedCount}."));

        return fixedCount;
    }

    private static bool EnsureDirectory(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(path);
        return true;
    }

    private static int DeleteTempFiles(string gameDir, CancellationToken ct)
    {
        var deleted = 0;

        foreach (var rootName in TempRoots)
        {
            var root = Path.Combine(gameDir, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.tmp", RecursiveFiles))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    // Файл занят другим процессом — пропускаем.
                }
                catch (UnauthorizedAccessException)
                {
                    // Нет прав — пропускаем.
                }
            }
        }

        return deleted;
    }
}
