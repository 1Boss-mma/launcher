using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Worlds;

/// <summary>
/// Автобэкапы миров: запуск по интервалу, ротация старых архивов и настройки.
/// </summary>
public sealed class WorldBackupScheduler
{
    private readonly LauncherPaths _paths;
    private readonly WorldService _worlds;

    public WorldBackupScheduler() : this(new LauncherPaths())
    {
    }

    public WorldBackupScheduler(LauncherPaths paths) : this(paths, null)
    {
    }

    public WorldBackupScheduler(LauncherPaths paths, WorldService? worlds)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _worlds = worlds ?? new WorldService(paths);
    }

    public string SettingsPath => WorldBackupOptions.GetSettingsPath(_paths);

    /// <summary>Папка бэкапов по умолчанию: backups\worlds.</summary>
    public string DefaultDestination => WorldService.GetWorldBackupsDirectory(_paths);

    /// <summary>Загружает настройки автобэкапов (битый файл не мешает запуску).</summary>
    public WorldBackupOptions LoadOptions() => WorldBackupOptions.LoadOrDefault(_paths);

    /// <summary>Нормализует и атомарно сохраняет настройки автобэкапов.</summary>
    public void SaveOptions(WorldBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Save(_paths);
    }

    /// <summary>
    /// Делает бэкапы всех валидных миров, если автобэкапы включены и интервал истёк.
    /// Возвращает пути созданных архивов (пустой список — запуск не требовался).
    /// </summary>
    public async Task<IReadOnlyList<string>> RunIfDueAsync(
        InstanceProfile instance,
        WorldBackupOptions options,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return Array.Empty<string>();
        }

        var interval = TimeSpan.FromHours(Math.Max(1, options.IntervalHours));
        if (options.LastRunUtc is { } lastRun
            && DateTimeOffset.UtcNow - lastRun < interval)
        {
            return Array.Empty<string>();
        }

        return await RunNowAsync(instance, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Принудительно делает бэкапы всех валидных миров (флаг Enabled не проверяется),
    /// обновляет LastRunUtc и применяет retention.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunNowAsync(
        InstanceProfile instance,
        WorldBackupOptions options,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();

        var worlds = _worlds.List(instance)
            .Where(world => world.IsValid)
            .ToList();

        var destination = ResolveDestination(options);

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку автобэкапов миров {destination}: {ex.Message}", ex);
        }

        var created = new List<string>();

        if (worlds.Count > 0)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Backup,
                $"Автобэкап миров: {worlds.Count}",
                0,
                worlds.Count));

            foreach (var world in worlds)
            {
                ct.ThrowIfCancellationRequested();

                var zipPath = await _worlds
                    .CreateBackupAsync(instance, world.Name, destination, progress, ct)
                    .ConfigureAwait(false);

                created.Add(zipPath);
            }
        }

        options.LastRunUtc = DateTimeOffset.UtcNow;
        SaveOptions(options);
        ApplyRetention(destination, options.Retention);

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            worlds.Count == 0
                ? "Автобэкап миров: нет валидных миров"
                : $"Автобэкап миров завершён: {created.Count}",
            worlds.Count,
            worlds.Count));

        return created;
    }

    /// <summary>Удаляет старые zip сверх retention, только если внутри есть world-backup.json.</summary>
    private static void ApplyRetention(string destination, int retention)
    {
        if (!Directory.Exists(destination))
        {
            return;
        }

        var keep = Math.Max(1, retention);

        try
        {
            var backups = new List<(string Path, DateTimeOffset CreatedUtc, DateTime LastWriteUtc)>();

            foreach (var path in Directory.EnumerateFiles(destination, "*.zip", SearchOption.TopDirectoryOnly))
            {
                var manifest = WorldBackupManifestStore.TryRead(path);
                if (manifest is null)
                {
                    // Чужой zip без манифеста не трогаем.
                    continue;
                }

                backups.Add((path, manifest.CreatedUtc, File.GetLastWriteTimeUtc(path)));
            }

            foreach (var backup in backups
                         .OrderByDescending(item => item.CreatedUtc)
                         .ThenByDescending(item => item.LastWriteUtc)
                         .Skip(keep))
            {
                try
                {
                    File.Delete(backup.Path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Чистка не должна ломать уже созданные бэкапы.
        }
    }

    private string ResolveDestination(WorldBackupOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DestinationDirectory))
        {
            return DefaultDestination;
        }

        try
        {
            return Path.GetFullPath(options.DestinationDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Недопустимая папка автобэкапов миров: {options.DestinationDirectory}", ex);
        }
    }
}
