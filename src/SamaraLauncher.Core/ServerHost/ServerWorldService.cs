using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Worlds;

namespace SamaraLauncher.Core.ServerHost;

/// <summary>
/// Миры и zip-бэкапы серверной папки: активный мир из server.properties,
/// создание, переключение, бэкапы и безопасное удаление.
/// </summary>
public sealed class ServerWorldService
{
    public const int DefaultRetention = 10;
    public const string DefaultWorldName = "world";

    private static readonly string[] InfrastructureFolders =
    {
        "mods",
        "logs",
        "libraries",
        "config",
        "defaultconfigs",
        "crash-reports",
        "runtime",
        "tools",
        "backups"
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        },
        StringComparer.OrdinalIgnoreCase);

    private readonly LauncherPaths _paths;
    private readonly Func<bool> _isServerRunning;
    private readonly WorldService _worlds;

    public ServerWorldService(
        LauncherPaths paths,
        Func<bool> isServerRunning,
        WorldService worlds)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _isServerRunning = isServerRunning ?? throw new ArgumentNullException(nameof(isServerRunning));
        _worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
    }

    /// <summary>Папка серверных бэкапов: backups\servers\&lt;safeServerId&gt;\worlds.</summary>
    public string GetBackupsRoot(HostedServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return GetServerBackupsDirectory(_paths, profile.Id);
    }

    public static string GetServerBackupsDirectory(LauncherPaths paths, string? serverId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.BackupsDir, "servers", SafeSegment(serverId, "server"), "worlds");
    }

    public static string GetServerRemovedWorldsDirectory(LauncherPaths paths, string? serverId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(
            paths.BackupsDir,
            "servers",
            SafeSegment(serverId, "server"),
            "removed-worlds");
    }

    /// <summary>
    /// Читает level-name и список папок с level.dat. Инфраструктурные папки
    /// сервера в список не попадают. Ошибки не бросаются — попадают в Error.
    /// </summary>
    public ServerWorldsSnapshot Inspect(HostedServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var serverDir = ServerHostService.GetServerDirectory(profile);
        var backupsRoot = GetBackupsRoot(profile);

        if (!Directory.Exists(serverDir))
        {
            return new ServerWorldsSnapshot(
                serverDir,
                DefaultWorldName,
                null,
                Array.Empty<ServerWorldInfo>(),
                backupsRoot,
                $"Папка сервера не найдена: {serverDir}");
        }

        var errors = new List<string>();
        var levelName = ReadLevelName(serverDir, errors);
        var worlds = new List<ServerWorldInfo>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(serverDir))
            {
                var folderName = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(folderName) || IsInfrastructureFolder(folderName))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(directory, "level.dat")))
                {
                    continue;
                }

                var info = _worlds.GetFromRoot(serverDir, folderName);
                if (info is null)
                {
                    continue;
                }

                worlds.Add(new ServerWorldInfo
                {
                    Name = info.Name,
                    DisplayName = info.DisplayName,
                    Path = info.Path,
                    SizeBytes = info.SizeBytes,
                    LastPlayed = info.LastPlayed,
                    Seed = info.Seed,
                    VersionName = info.VersionName,
                    IsValid = info.IsValid,
                    IsActive = string.Equals(folderName, levelName, StringComparison.OrdinalIgnoreCase),
                    Exists = true,
                    IconColor = info.IconColor
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Не удалось прочитать миры: {ex.Message}");
        }

        var active = worlds.FirstOrDefault(world => world.IsActive);

        if (active is null)
        {
            var exists = false;

            if (TryValidateWorldName(levelName, out var normalizedName, out _))
            {
                try
                {
                    exists = Directory.Exists(Path.Combine(serverDir, normalizedName));
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    exists = false;
                }
            }

            active = new ServerWorldInfo
            {
                Name = levelName,
                DisplayName = levelName,
                Path = serverDir,
                IsValid = false,
                IsActive = true,
                Exists = exists,
                IconColor = "#F5C542"
            };

            worlds.Insert(0, active);
        }

        var ordered = worlds
            .OrderByDescending(world => world.IsActive)
            .ThenByDescending(world => world.IsValid)
            .ThenByDescending(world => world.LastPlayed)
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ServerWorldsSnapshot(
            serverDir,
            levelName,
            ordered.FirstOrDefault(world => world.IsActive),
            ordered,
            backupsRoot,
            errors.Count == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>
    /// Создаёт новый мир: пишет level-name и level-seed, но папку не создаёт —
    /// её сгенерирует сервер при следующем запуске.
    /// </summary>
    public async Task<string> CreateWorldAsync(
        HostedServerProfile profile,
        string name,
        string? seed = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureServerStopped();

        var worldName = RequireWorldName(name);
        var seedValue = NormalizeSeed(seed);
        var serverDir = RequireServerDirectory(profile);

        if (IsInfrastructureFolder(worldName))
        {
            throw new LauncherException(
                $"Имя «{worldName}» зарезервировано под папку сервера. Выберите другое.");
        }

        if (Directory.Exists(Path.Combine(serverDir, worldName))
            || File.Exists(Path.Combine(serverDir, worldName)))
        {
            throw new LauncherException($"Папка с именем «{worldName}» уже существует.");
        }

        ct.ThrowIfCancellationRequested();
        await BackupActiveWorldAsync(profile, serverDir, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var propertiesPath = Path.Combine(serverDir, "server.properties");
        var properties = ServerProperties.Load(propertiesPath);
        properties.Set("level-name", worldName);
        properties.Set("level-seed", seedValue);
        properties.Save(propertiesPath);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Мир «{worldName}» будет создан при следующем запуске сервера."));

        return worldName;
    }

    /// <summary>Делает существующий мир активным через level-name.</summary>
    public async Task ActivateWorldAsync(
        HostedServerProfile profile,
        string name,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureServerStopped();

        var worldName = RequireWorldName(name);
        var serverDir = RequireServerDirectory(profile);
        var worldDir = Path.Combine(serverDir, worldName);

        if (!Directory.Exists(worldDir) || !File.Exists(Path.Combine(worldDir, "level.dat")))
        {
            throw new LauncherException($"Мир не найден: {worldDir}");
        }

        var current = ReadLevelName(serverDir, null);
        if (string.Equals(current, worldName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        await BackupActiveWorldAsync(profile, serverDir, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        WriteLevelName(serverDir, worldName);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Активный мир: {worldName}"));
    }

    /// <summary>Создаёт zip-бэкап мира и применяет retention для этого мира.</summary>
    public async Task<string> BackupAsync(
        HostedServerProfile profile,
        string name,
        int retention = DefaultRetention,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureServerStopped();

        var worldName = RequireWorldName(name);
        var serverDir = RequireServerDirectory(profile);
        var worldDir = Path.Combine(serverDir, worldName);

        if (!Directory.Exists(worldDir) || !File.Exists(Path.Combine(worldDir, "level.dat")))
        {
            throw new LauncherException($"Мир не найден: {worldDir}");
        }

        var destination = GetBackupsRoot(profile);
        var zip = await _worlds
            .CreateBackupFromFolderAsync(
                worldDir,
                destination,
                profile.Id,
                progress,
                ct)
            .ConfigureAwait(false);

        ApplyRetention(destination, worldName, retention);

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Бэкап мира «{worldName}» создан: {Path.GetFileName(zip)}"));

        return zip;
    }

    public IReadOnlyList<ServerWorldBackupInfo> ListBackups(HostedServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return _worlds
            .ListBackups(GetBackupsRoot(profile))
            .Select(backup => new ServerWorldBackupInfo
            {
                Path = backup.Path,
                WorldName = backup.WorldName,
                DisplayName = backup.DisplayName,
                CreatedUtc = backup.CreatedUtc,
                SizeBytes = backup.SizeBytes,
                ServerId = backup.ServerId
            })
            .ToList();
    }

    /// <summary>
    /// Восстанавливает архив поверх мира с тем же именем. Перед заменой
    /// делает страховочный бэкап текущего активного мира.
    /// </summary>
    public async Task<string> RestoreAsync(
        HostedServerProfile profile,
        ServerWorldBackupInfo backup,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(backup);
        EnsureServerStopped();

        if (string.IsNullOrWhiteSpace(backup.Path) || !File.Exists(backup.Path))
        {
            throw new LauncherException($"Файл бэкапа не найден: {backup.Path}");
        }

        var backupsRoot = GetBackupsRoot(profile);
        if (!IsInside(backup.Path, backupsRoot))
        {
            throw new LauncherException($"Бэкап находится вне папки серверных бэкапов: {backup.Path}");
        }

        var serverDir = RequireServerDirectory(profile);

        ct.ThrowIfCancellationRequested();
        await BackupActiveWorldAsync(profile, serverDir, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var restored = await _worlds
            .RestoreBackupToRootAsync(backup.Path, serverDir, overwrite: true, ct)
            .ConfigureAwait(false);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Мир восстановлен: {Path.GetFileName(restored)}"));

        return restored;
    }

    /// <summary>
    /// Удаляет неактивный мир: папка переносится в
    /// backups\servers\&lt;id&gt;\removed-worlds\&lt;timestamp&gt;.
    /// </summary>
    public Task<bool> DeleteWorldAsync(
        HostedServerProfile profile,
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureServerStopped();

        var worldName = RequireWorldName(name);
        var serverDir = RequireServerDirectory(profile);
        var current = ReadLevelName(serverDir, null);

        if (string.Equals(current, worldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Нельзя удалить активный мир «{worldName}». Сначала сделайте активным другой мир.");
        }

        var removedRoot = GetServerRemovedWorldsDirectory(_paths, profile.Id);
        return Task.Run(
            () => _worlds.DeleteFromRoot(serverDir, worldName, removedRoot, moveToBackups: true),
            ct);
    }

    public Task<bool> DeleteBackupAsync(
        HostedServerProfile profile,
        ServerWorldBackupInfo backup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(backup);

        return Task.Run(
            () => _worlds.DeleteBackup(GetBackupsRoot(profile), backup.Path),
            ct);
    }

    public void OpenFolder(HostedServerProfile profile, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var serverDir = RequireServerDirectory(profile);
        _worlds.OpenFolderFromRoot(serverDir, name);
    }

    public void OpenBackupsFolder(HostedServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _worlds.OpenFolderFromRoot(GetBackupsRoot(profile));
    }

    /// <summary>Валидирует имя папки мира. Возвращает нормализованное имя.</summary>
    public static string RequireWorldName(string? name)
    {
        if (!TryValidateWorldName(name, out var normalized, out var error))
        {
            throw new LauncherException(error);
        }

        return normalized;
    }

    public static bool TryValidateWorldName(
        string? name,
        out string normalized,
        out string error)
    {
        var raw = name ?? "";
        normalized = raw.Trim();
        error = "";

        if (raw.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            error = "Имя мира не должно содержать переводы строк и табуляцию.";
            return false;
        }

        if (raw.Length > 0 && (raw.EndsWith('.') || raw.EndsWith(' ')))
        {
            error = "Имя мира не должно заканчиваться точкой или пробелом.";
            return false;
        }

        if (normalized.Length == 0)
        {
            error = "Укажите имя мира.";
            return false;
        }

        if (normalized is "." or "..")
        {
            error = $"Недопустимое имя мира: {name}";
            return false;
        }

        if (normalized.Length > 128)
        {
            error = "Имя мира слишком длинное (максимум 128 символов).";
            return false;
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized.Contains('/')
            || normalized.Contains('\\')
            || Path.IsPathRooted(normalized))
        {
            error = $"Недопустимое имя мира: {name}";
            return false;
        }

        var stem = normalized.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem))
        {
            error = $"Имя «{normalized}» зарезервировано Windows. Выберите другое.";
            return false;
        }

        return true;
    }

    public static string NormalizeSeed(string? seed)
    {
        var value = (seed ?? "").Trim();

        if (value.Length > 128)
        {
            throw new LauncherException("Seed слишком длинный (максимум 128 символов).");
        }

        if (value.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            throw new LauncherException("Seed не должен содержать переводы строк и табуляцию.");
        }

        return value;
    }

    private async Task BackupActiveWorldAsync(
        HostedServerProfile profile,
        string serverDir,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        var levelName = ReadLevelName(serverDir, null);
        if (!TryValidateWorldName(levelName, out var activeName, out _))
        {
            return;
        }

        var activeDir = Path.Combine(serverDir, activeName);
        if (!Directory.Exists(activeDir) || !File.Exists(Path.Combine(activeDir, "level.dat")))
        {
            return;
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Страховочный бэкап активного мира «{activeName}»..."));

        var destination = GetBackupsRoot(profile);
        await _worlds
            .CreateBackupFromFolderAsync(
                activeDir,
                destination,
                profile.Id,
                progress,
                ct)
            .ConfigureAwait(false);

        ApplyRetention(destination, activeName, DefaultRetention);
    }

    private string RequireServerDirectory(HostedServerProfile profile)
    {
        var serverDir = ServerHostService.GetServerDirectory(profile);

        if (!Directory.Exists(serverDir))
        {
            throw new LauncherException($"Папка сервера не найдена: {serverDir}");
        }

        return serverDir;
    }

    private void EnsureServerStopped()
    {
        if (_isServerRunning())
        {
            throw new LauncherException(
                "Сначала остановите сервер: миры и бэкапы меняются только при остановленном сервере.");
        }
    }

    private static string ReadLevelName(string serverDir, List<string>? errors)
    {
        try
        {
            var value = ServerProperties
                .Load(Path.Combine(serverDir, "server.properties"))
                .Get("level-name");

            return string.IsNullOrWhiteSpace(value) ? DefaultWorldName : value.Trim();
        }
        catch (LauncherException ex)
        {
            errors?.Add(ex.Message);
            return DefaultWorldName;
        }
    }

    private static void WriteLevelName(string serverDir, string worldName)
    {
        var path = Path.Combine(serverDir, "server.properties");
        var properties = ServerProperties.Load(path);
        properties.Set("level-name", worldName);
        properties.Save(path);
    }

    private static bool IsInfrastructureFolder(string name) =>
        InfrastructureFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void ApplyRetention(string destination, string worldName, int retention)
    {
        if (!Directory.Exists(destination))
        {
            return;
        }

        var keep = Math.Clamp(retention, 1, 1000);
        var backups = new List<(string Path, DateTimeOffset CreatedUtc, DateTime LastWriteUtc)>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         destination,
                         "*.zip",
                         SearchOption.TopDirectoryOnly))
            {
                var manifest = WorldBackupManifestStore.TryRead(path);
                if (manifest is null
                    || !string.Equals(manifest.WorldName, worldName, StringComparison.OrdinalIgnoreCase))
                {
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

    private static string SafeSegment(string? value, string fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = text
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch)
            .ToArray();

        var result = new string(chars).Trim('.', ' ');
        if (result.Length == 0)
        {
            return fallback;
        }

        return ReservedWindowsNames.Contains(result) ? result + "_" : result;
    }

    private static bool IsInside(string path, string root)
    {
        string fullPath;
        string fullRoot;

        try
        {
            fullPath = Path.GetFullPath(path);
            fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Мир в папке сервера.</summary>
public sealed class ServerWorldInfo
{
    /// <summary>Имя папки мира (level-name).</summary>
    public string Name { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string Path { get; init; } = "";

    public long SizeBytes { get; init; }

    public DateTimeOffset LastPlayed { get; init; }

    public long Seed { get; init; }

    public string VersionName { get; init; } = "";

    public bool IsValid { get; init; }

    /// <summary>Совпадает с level-name в server.properties.</summary>
    public bool IsActive { get; init; }

    /// <summary>false — активный мир описан в properties, но папки ещё нет.</summary>
    public bool Exists { get; init; } = true;

    public string IconColor { get; init; } = "#5FBE4A";
}

/// <summary>Состояние миров сервера и путь к их бэкапам.</summary>
public sealed record ServerWorldsSnapshot(
    string ServerDir,
    string LevelName,
    ServerWorldInfo? ActiveWorld,
    IReadOnlyList<ServerWorldInfo> Worlds,
    string BackupsRoot,
    string? Error);

/// <summary>Zip-бэкап серверного мира.</summary>
public sealed class ServerWorldBackupInfo
{
    public string Path { get; init; } = "";

    public string FileName => System.IO.Path.GetFileName(Path);

    public string WorldName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public DateTimeOffset CreatedUtc { get; init; }

    public long SizeBytes { get; init; }

    public string? ServerId { get; init; }
}
