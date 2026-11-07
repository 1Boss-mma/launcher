using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Worlds;

/// <summary>
/// Список миров, zip-бэкапы, восстановление, копирование и удаление папок в saves.
/// </summary>
public sealed class WorldService
{
    public const string ManifestFileName = "world-backup.json";

    private const string SavesFolderName = "saves";
    private const string RemovedWorldsFolderName = "removed-worlds";
    private const long MaxRestoreTotalBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxRestoreEntries = 200_000;
    private static readonly TimeSpan StalePartAge = TimeSpan.FromHours(1);

    private static readonly string[] IconPalette =
    {
        "#5FBE4A",
        "#4A90D9",
        "#D98C4A",
        "#9B59B6",
        "#E05C5C",
        "#3AAFA9",
        "#C9A227",
        "#7F8C8D"
    };

    private static readonly Regex BackupNamePattern = new(
        @"^(?<name>.+)-\d{8}-\d{6}(?:-\d+)?$",
        RegexOptions.CultureInvariant);

    private static readonly EnumerationOptions RecursiveFileOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private readonly LauncherPaths _paths;

    public WorldService() : this(new LauncherPaths())
    {
    }

    public WorldService(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Папка для автобэкапов миров: %LOCALAPPDATA%\SamaraLauncher\backups\worlds.</summary>
    public string BackupsRoot => GetWorldBackupsDirectory(_paths);

    public static string GetWorldBackupsDirectory(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.BackupsDir, "worlds");
    }

    /// <summary>Полный путь к папке saves инстанса.</summary>
    public string GetSavesDirectory(InstanceProfile instance) => RequireSavesRoot(instance);

    /// <summary>
    /// Миры в gameDir\saves, у которых есть level.dat. Битый level.dat не роняет список:
    /// такой мир возвращается с IsValid=false. Новые по LastPlayed — первыми.
    /// Размер папки считается только при <paramref name="includeSize"/>.
    /// </summary>
    public IReadOnlyList<WorldInfo> List(InstanceProfile instance, bool includeSize = true)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var savesRoot = TryGetSavesRoot(instance);
        return savesRoot is null ? Array.Empty<WorldInfo>() : ListFromRoot(savesRoot, includeSize);
    }

    /// <summary>Мир по имени папки. null — мир не найден; недопустимое имя — LauncherException.</summary>
    public WorldInfo? Get(InstanceProfile instance, string folderName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var savesRoot = TryGetSavesRoot(instance);
        return savesRoot is null ? null : GetFromRoot(savesRoot, folderName);
    }

    /// <summary>
    /// Миры в произвольной папке (у каждого есть level.dat). Битый level.dat
    /// не роняет список: такой мир возвращается с IsValid=false.
    /// </summary>
    public IReadOnlyList<WorldInfo> ListFromRoot(string worldsRoot, bool includeSize = true)
    {
        var root = RequireWorldsRoot(worldsRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorldInfo>();
        }

        var result = new List<WorldInfo>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var folderName = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(folderName))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(directory, "level.dat")))
                {
                    continue;
                }

                result.Add(ReadWorldInfo(directory, includeSize));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Возвращаем то, что успели прочитать.
        }

        return result
            .OrderByDescending(world => world.LastPlayed)
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Мир по имени папки внутри произвольного корня.</summary>
    public WorldInfo? GetFromRoot(string worldsRoot, string folderName)
    {
        var root = RequireWorldsRoot(worldsRoot);
        if (!Directory.Exists(root))
        {
            return null;
        }

        var worldDir = ResolveWorldDirectory(root, folderName);
        if (!Directory.Exists(worldDir))
        {
            return null;
        }

        var levelDat = Path.Combine(worldDir, "level.dat");
        return File.Exists(levelDat) ? ReadWorldInfo(worldDir) : null;
    }

    /// <summary>
    /// Создаёт zip папки мира и world-backup.json внутри архива.
    /// По умолчанию кладёт в <see cref="BackupsRoot"/>.
    /// </summary>
    public Task<string> CreateBackupAsync(
        InstanceProfile instance,
        string worldFolderName,
        string? destinationDirectory = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var savesRoot = RequireSavesRoot(instance);
        var worldDir = ResolveWorldDirectory(savesRoot, worldFolderName);
        return CreateBackupCoreAsync(
            worldDir,
            destinationDirectory,
            instance.Id,
            serverId: null,
            progress,
            ct);
    }

    /// <summary>
    /// Создаёт zip произвольной папки мира и world-backup.json внутри архива.
    /// <paramref name="serverId"/> записывается в манифест для серверных бэкапов.
    /// </summary>
    public Task<string> CreateBackupFromFolderAsync(
        string worldDirectory,
        string? destinationDirectory = null,
        string? serverId = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default) =>
        CreateBackupCoreAsync(
            worldDirectory,
            destinationDirectory,
            instanceId: null,
            serverId,
            progress,
            ct);

    private async Task<string> CreateBackupCoreAsync(
        string worldDirectory,
        string? destinationDirectory,
        string? instanceId,
        string? serverId,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(worldDirectory))
        {
            throw new LauncherException("Не указана папка мира для бэкапа.");
        }

        string worldDir;
        try
        {
            worldDir = Path.GetFullPath(worldDirectory.Trim().Trim('"'));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Недопустимая папка мира: {worldDirectory}", ex);
        }

        if (!Directory.Exists(worldDir))
        {
            throw new LauncherException($"Папка мира не найдена: {worldDir}");
        }

        var levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat))
        {
            throw new LauncherException(
                $"В папке мира нет level.dat, бэкап невозможен: {worldDir}");
        }

        var worldFolderName = Path.GetFileName(worldDir);
        if (string.IsNullOrWhiteSpace(worldFolderName))
        {
            throw new LauncherException($"Не удалось определить имя папки мира: {worldDir}");
        }

        var destination = ResolveDestination(destinationDirectory);
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку для бэкапов миров {destination}: {ex.Message}", ex);
        }

        var (displayName, files) = await Task.Run(
            () =>
            {
                var name = TryReadDisplayName(levelDat, worldFolderName);
                var worldFiles = EnumerateWorldFiles(worldDir);
                return (name, worldFiles);
            },
            ct).ConfigureAwait(false);

        var totalBytes = files.Sum(file => file.Size);
        var zipPath = BuildBackupPath(destination, worldFolderName);
        var partPath = zipPath + ".part";

        CleanupStaleParts(destination);

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Бэкап мира {displayName}: {files.Count} файлов...",
            0,
            files.Count));

        try
        {
            // Сжатие jars/region-файлов синхронное; уводим его с UI-потока.
            await Task.Run(
                () => CreateBackupArchive(
                    partPath,
                    worldFolderName,
                    displayName,
                    totalBytes,
                    instanceId,
                    serverId,
                    files,
                    progress,
                    ct),
                ct).ConfigureAwait(false);

            File.Move(partPath, zipPath);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            throw;
        }
        catch (LauncherException)
        {
            TryDeleteFile(partPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDeleteFile(partPath);
            throw new LauncherException(
                $"Не удалось создать бэкап мира {displayName}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Бэкап мира создан: {Path.GetFileName(zipPath)}",
            files.Count,
            files.Count));

        return zipPath;
    }

    private static void CreateBackupArchive(
        string partPath,
        string worldFolderName,
        string displayName,
        long totalBytes,
        string? instanceId,
        string? serverId,
        List<(string FullPath, string EntryName, long Size, DateTime LastWriteTime)> files,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        using var stream = new FileStream(
            partPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WorldBackupManifestStore.Write(
            archive,
            new WorldBackupManifest
            {
                FormatVersion = 2,
                WorldName = worldFolderName,
                DisplayName = displayName,
                CreatedUtc = DateTimeOffset.UtcNow,
                SizeBytes = totalBytes,
                LauncherVersion = LauncherVersion.Current,
                InstanceId = instanceId,
                ServerId = serverId
            });

        long completed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var level = file.EntryName.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.Fastest
                : CompressionLevel.Optimal;

            var entry = archive.CreateEntry(file.EntryName, level);
            entry.LastWriteTime = ClampZipTimestamp(file.LastWriteTime);

            using var source = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var target = entry.Open();
            source.CopyTo(target, 81920);

            completed++;
            if (completed % 25 == 0 || completed == files.Count)
            {
                progress?.Report(new LauncherProgress(
                    ProgressStage.Backup,
                    $"Бэкап мира {displayName}: {completed} из {files.Count}",
                    completed,
                    files.Count));
            }
        }
    }

    /// <summary>
    /// Безопасно распаковывает мир из zip в saves инстанса.
    /// overwrite=false и существующая папка мира — ошибка.
    /// </summary>
    public async Task RestoreBackupAsync(
        string zipPath,
        InstanceProfile instance,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await RestoreBackupToRootAsync(zipPath, RequireSavesRoot(instance), overwrite, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Безопасно распаковывает мир из zip в произвольную папку миров.
    /// overwrite=false и существующая папка мира — ошибка.
    /// </summary>
    public Task<string> RestoreBackupToRootAsync(
        string zipPath,
        string worldsRoot,
        bool overwrite = false,
        CancellationToken ct = default) =>
        RestoreCoreAsync(zipPath, worldsRoot, overwrite, ct);

    /// <summary>Копия папки мира с уникальным именем («Name - Copy», «Name - Copy (2)»).</summary>
    public async Task<string> DuplicateAsync(
        InstanceProfile instance,
        string worldFolderName,
        string? newName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var savesRoot = RequireSavesRoot(instance);
        var source = ResolveWorldDirectory(savesRoot, worldFolderName);

        if (!Directory.Exists(source))
        {
            throw new LauncherException($"Папка мира не найдена: {source}");
        }

        var baseName = string.IsNullOrWhiteSpace(newName)
            ? Path.GetFileName(source) + " - Copy"
            : SanitizeFolderName(newName);

        var targetName = MakeUniqueDirectoryName(savesRoot, baseName);
        var target = Path.GetFullPath(Path.Combine(savesRoot, targetName));

        if (!IsInside(target, savesRoot))
        {
            throw new LauncherException($"Недопустимое имя папки мира: {newName}");
        }

        try
        {
            await Task.Run(
                () =>
                {
                    try
                    {
                        CopyDirectory(source, target, ct);
                    }
                    catch
                    {
                        TryDeleteDirectory(target);
                        throw;
                    }
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось скопировать мир {worldFolderName}: {ex.Message}", ex);
        }

        return target;
    }

    /// <summary>Экспорт мира — тот же zip-бэкап, можно указать папку назначения.</summary>
    public Task<string> ExportAsync(
        InstanceProfile instance,
        string worldFolderName,
        string? destinationDirectory = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default) =>
        CreateBackupAsync(instance, worldFolderName, destinationDirectory, progress, ct);

    /// <summary>Импорт zip-бэкапа. Возвращает путь к созданной папке мира.</summary>
    public Task<string> ImportAsync(
        string zipPath,
        InstanceProfile instance,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return RestoreCoreAsync(zipPath, RequireSavesRoot(instance), overwrite, ct);
    }

    /// <summary>
    /// Удаляет мир. По умолчанию переносит папку в backups\removed-worlds\&lt;timestamp&gt;.
    /// moveToBackups=false удаляет безвозвратно — вызывать только после подтверждения пользователя.
    /// </summary>
    public bool Delete(InstanceProfile instance, string worldFolderName, bool moveToBackups = true)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var savesRoot = RequireSavesRoot(instance);
        return DeleteFromRoot(
            savesRoot,
            worldFolderName,
            Path.Combine(_paths.BackupsDir, RemovedWorldsFolderName),
            moveToBackups);
    }

    /// <summary>
    /// Удаляет папку мира из произвольного корня. По умолчанию переносит её
    /// в <paramref name="removedRoot"/>\&lt;timestamp&gt;.
    /// </summary>
    public bool DeleteFromRoot(
        string worldsRoot,
        string worldFolderName,
        string? removedRoot = null,
        bool moveToBackups = true)
    {
        var root = RequireWorldsRoot(worldsRoot);
        var worldDir = ResolveWorldDirectory(root, worldFolderName);

        if (!Directory.Exists(worldDir))
        {
            return false;
        }

        if (!moveToBackups)
        {
            try
            {
                Directory.Delete(worldDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось удалить мир {worldFolderName}: {ex.Message}", ex);
            }

            return true;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var targetRoot = ResolveRemovedRoot(removedRoot);
        var removedPath = Path.Combine(targetRoot, stamp);

        try
        {
            Directory.CreateDirectory(removedPath);
            var target = MakeUniqueDirectoryPath(removedPath, Path.GetFileName(worldDir));
            MoveDirectory(worldDir, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось перенести мир {worldFolderName} в {removedPath}: {ex.Message}", ex);
        }

        return true;
    }

    /// <summary>Открывает папку мира или общую папку saves в проводнике.</summary>
    public void OpenFolder(InstanceProfile instance, string? worldFolderName = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        OpenFolderFromRoot(RequireSavesRoot(instance), worldFolderName);
    }

    /// <summary>Открывает папку мира или корень миров в проводнике.</summary>
    public void OpenFolderFromRoot(string worldsRoot, string? worldFolderName = null)
    {
        var root = RequireWorldsRoot(worldsRoot);
        string path;

        if (string.IsNullOrWhiteSpace(worldFolderName))
        {
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку миров {root}: {ex.Message}", ex);
            }

            path = root;
        }
        else
        {
            path = ResolveWorldDirectory(root, worldFolderName);
            if (!Directory.Exists(path))
            {
                throw new LauncherException($"Папка мира не найдена: {path}");
            }
        }

        try
        {
            ProcessHost.ShellOpen(path);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new LauncherException($"Не удалось открыть папку {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Архивы бэкапов в папке, новые — первыми.</summary>
    public IReadOnlyList<WorldBackupArchiveInfo> ListBackups(string backupsRoot)
    {
        var root = RequireWorldsRoot(backupsRoot, "папки бэкапов");
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorldBackupArchiveInfo>();
        }

        var result = new List<WorldBackupArchiveInfo>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
            {
                FileInfo file;
                try
                {
                    file = new FileInfo(path);
                    if (!file.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var manifest = WorldBackupManifestStore.TryRead(file.FullName);
                result.Add(new WorldBackupArchiveInfo
                {
                    Path = file.FullName,
                    WorldName = manifest?.WorldName ?? "",
                    DisplayName = string.IsNullOrWhiteSpace(manifest?.DisplayName)
                        ? Path.GetFileNameWithoutExtension(file.Name)
                        : manifest!.DisplayName,
                    CreatedUtc = manifest?.CreatedUtc
                        ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    SizeBytes = file.Length,
                    ServerId = manifest?.ServerId
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Возвращаем то, что успели прочитать.
        }

        return result
            .OrderByDescending(item => item.CreatedUtc)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Удаляет архив бэкапа внутри указанной папки.</summary>
    public bool DeleteBackup(string backupsRoot, string zipPath)
    {
        var root = RequireWorldsRoot(backupsRoot, "папки бэкапов");
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new LauncherException("Не указан архив бэкапа мира.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(zipPath.Trim().Trim('"'));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Недопустимый путь к бэкапу мира: {zipPath}", ex);
        }

        if (!IsInside(fullPath, root)
            || string.Equals(fullPath, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Бэкап находится вне папки бэкапов: {zipPath}");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Файл не является zip-архивом бэкапа: {zipPath}");
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        try
        {
            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось удалить бэкап {Path.GetFileName(fullPath)}: {ex.Message}", ex);
        }
    }

    private async Task<string> RestoreCoreAsync(
        string zipPath,
        string worldsRoot,
        bool overwrite,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new LauncherException($"Файл бэкапа мира не найден: {zipPath}");
        }

        var savesRoot = RequireWorldsRoot(worldsRoot);
        try
        {
            Directory.CreateDirectory(savesRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку миров {savesRoot}: {ex.Message}", ex);
        }

        var staging = Path.Combine(savesRoot, ".world-restore-" + Guid.NewGuid().ToString("N"));
        var committed = false;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var rawEntries = new List<(ZipArchiveEntry Entry, string RelativePath)>();
            long totalBytes = 0;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = NormalizeArchivePath(entry.FullName);
                if (relativePath.Length == 0 || IsDirectoryEntry(entry))
                {
                    continue;
                }

                if (string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Length > 0)
                {
                    totalBytes += entry.Length;
                }

                if (rawEntries.Count >= MaxRestoreEntries || totalBytes > MaxRestoreTotalBytes)
                {
                    throw new LauncherException(
                        "Бэкап мира слишком большой для безопасного восстановления.");
                }

                rawEntries.Add((entry, relativePath));
            }

            // Поддерживаем оба формата: level.dat в корне zip и
            // zip, созданный с includeBaseDirectory=true (папка мира верхним уровнем).
            var topFolder = FindWorldArchiveRoot(rawEntries);
            var prefix = topFolder is null ? "" : topFolder + "/";
            var entries = new List<(ZipArchiveEntry Entry, string RelativePath)>();
            var hasLevelDat = false;

            foreach (var (entry, rawPath) in rawEntries)
            {
                var relativePath = rawPath;

                if (prefix.Length > 0)
                {
                    if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LauncherException(
                            $"Бэкап содержит файлы вне папки мира: {entry.FullName}");
                    }

                    relativePath = relativePath[prefix.Length..];
                    if (relativePath.Length == 0)
                    {
                        continue;
                    }
                }

                if (string.Equals(relativePath, "level.dat", StringComparison.OrdinalIgnoreCase))
                {
                    hasLevelDat = true;
                }

                entries.Add((entry, relativePath));
            }

            if (!hasLevelDat)
            {
                throw new LauncherException(
                    $"Бэкап мира не содержит level.dat: {Path.GetFileName(zipPath)}");
            }

            var manifest = WorldBackupManifestStore.TryRead(archive);
            var folderName = ResolveRestoreFolderName(manifest?.WorldName, zipPath, topFolder);
            var target = Path.GetFullPath(Path.Combine(savesRoot, folderName));

            if (!IsInside(target, savesRoot) || string.Equals(target, savesRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException($"Недопустимое имя папки мира в бэкапе: {folderName}");
            }

            if (File.Exists(target))
            {
                throw new LauncherException(
                    $"Нельзя восстановить мир: по пути {target} находится файл.");
            }

            if (Directory.Exists(target) && !overwrite)
            {
                throw new LauncherException(
                    $"Мир «{folderName}» уже существует. Включите перезапись или переименуйте папку.");
            }

            Directory.CreateDirectory(staging);

            long totalExtracted = 0;

            foreach (var (entry, relativePath) in entries)
            {
                ct.ThrowIfCancellationRequested();

                var targetPath = Path.GetFullPath(
                    Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                if (!IsInside(targetPath, staging))
                {
                    throw new LauncherException(
                        $"Бэкап содержит недопустимый путь: {entry.FullName}");
                }

                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var source = entry.Open();
                await using var destination = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    totalExtracted += read;

                    if (totalExtracted > MaxRestoreTotalBytes)
                    {
                        throw new LauncherException(
                            "Бэкап мира слишком большой для безопасного восстановления.");
                    }

                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), ct)
                        .ConfigureAwait(false);
                }
            }

            if (!File.Exists(Path.Combine(staging, "level.dat")))
            {
                throw new LauncherException(
                    $"После распаковки бэкапа не найден level.dat: {Path.GetFileName(zipPath)}");
            }

            string? displaced = null;
            try
            {
                if (Directory.Exists(target))
                {
                    displaced = target + ".replaced-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, displaced);
                }

                Directory.Move(staging, target);
                committed = true;

                if (displaced is not null)
                {
                    TryDeleteDirectory(displaced);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (displaced is not null && !Directory.Exists(target))
                {
                    try
                    {
                        Directory.Move(displaced, target);
                    }
                    catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                    {
                        // Старую папку вернуть не удалось; сообщим об этом ниже.
                    }
                }

                throw new LauncherException(
                    $"Не удалось заменить папку мира {target}: {ex.Message}", ex);
            }

            return target;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(
                $"Бэкап мира повреждён: {Path.GetFileName(zipPath)}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось восстановить мир из {Path.GetFileName(zipPath)}: {ex.Message}", ex);
        }
        finally
        {
            if (!committed)
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private static WorldInfo ReadWorldInfo(string worldDir, bool includeSize = true)
    {
        var folderName = Path.GetFileName(worldDir);
        var levelDat = Path.Combine(worldDir, "level.dat");
        var size = includeSize ? ComputeSizeSafe(worldDir) : 0L;
        var fallbackTime = GetFolderTimestamp(worldDir, levelDat);

        try
        {
            var nbt = NbtReader.ReadFile(levelDat);
            var data = nbt.GetCompound("Data") ?? nbt;

            var displayName = data.GetString("LevelName");
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = folderName;
            }

            var versionName = data.GetCompound("Version")?.GetString("Name")
                ?? data.GetString("VersionName")
                ?? "";

            return new WorldInfo
            {
                Name = folderName,
                DisplayName = displayName.Trim(),
                Path = worldDir,
                SizeBytes = size,
                LastPlayed = ReadLastPlayed(data, fallbackTime),
                Seed = ReadSeed(data),
                GameType = data.GetInt("GameType"),
                Difficulty = data.GetInt("Difficulty", 2),
                Hardcore = data.GetBool("hardcore") || data.GetBool("Hardcore"),
                VersionName = versionName.Trim(),
                IsValid = true,
                IconColor = IconColorFor(folderName)
            };
        }
        catch (Exception ex) when (ex is LauncherException or IOException or UnauthorizedAccessException)
        {
            return new WorldInfo
            {
                Name = folderName,
                DisplayName = folderName,
                Path = worldDir,
                SizeBytes = size,
                LastPlayed = fallbackTime,
                IsValid = false,
                IconColor = IconColorFor(folderName)
            };
        }
    }

    private static DateTimeOffset ReadLastPlayed(NbtCompound data, DateTimeOffset fallback)
    {
        if (!data.TryGetLong("LastPlayed", out var milliseconds) || milliseconds <= 0)
        {
            return fallback;
        }

        if (milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return fallback;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    private static long ReadSeed(NbtCompound data)
    {
        if (data.TryGetLong("RandomSeed", out var seed))
        {
            return seed;
        }

        var worldGen = data.GetCompound("WorldGenSettings");
        return worldGen is not null && worldGen.TryGetLong("seed", out var worldGenSeed)
            ? worldGenSeed
            : 0;
    }

    private static string TryReadDisplayName(string levelDat, string folderName)
    {
        try
        {
            var data = NbtReader.ReadFile(levelDat).GetCompound("Data");
            var name = data?.GetString("LevelName");
            return string.IsNullOrWhiteSpace(name) ? folderName : name.Trim();
        }
        catch (LauncherException)
        {
            return folderName;
        }
    }

    private static string IconColorFor(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return IconPalette[hash % (uint)IconPalette.Length];
    }

    private string ResolveDestination(string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return BackupsRoot;
        }

        try
        {
            return Path.GetFullPath(destinationDirectory.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Недопустимая папка для бэкапов миров: {destinationDirectory}", ex);
        }
    }

    private static string BuildBackupPath(string destination, string worldFolderName)
    {
        var safeName = SanitizeFolderName(worldFolderName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(destination, $"{safeName}-{stamp}.zip");

        if (!File.Exists(path) && !File.Exists(path + ".part"))
        {
            return path;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(destination, $"{safeName}-{stamp}-{index}.zip");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
            {
                return candidate;
            }
        }

        throw new LauncherException("Не удалось подобрать имя для бэкапа мира.");
    }

    private static string? FindWorldArchiveRoot(
        List<(ZipArchiveEntry Entry, string RelativePath)> entries)
    {
        if (entries.Any(entry =>
                string.Equals(entry.RelativePath, "level.dat", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string? root = null;
        foreach (var (_, relativePath) in entries)
        {
            var parts = relativePath.Split('/');
            if (parts.Length != 2
                || !string.Equals(parts[1], "level.dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (root is null)
            {
                root = parts[0];
            }
            else if (!string.Equals(root, parts[0], StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(
                    "Бэкап содержит несколько папок миров — неясно, какую восстанавливать.");
            }
        }

        return root;
    }

    private static string ResolveRestoreFolderName(
        string? manifestName,
        string zipPath,
        string? topFolder)
    {
        if (!string.IsNullOrWhiteSpace(manifestName))
        {
            return SanitizeFolderName(manifestName);
        }

        if (!string.IsNullOrWhiteSpace(topFolder))
        {
            return SanitizeFolderName(topFolder);
        }

        var stem = Path.GetFileNameWithoutExtension(zipPath);
        var match = BackupNamePattern.Match(stem);
        if (match.Success)
        {
            stem = match.Groups["name"].Value;
        }

        return SanitizeFolderName(stem);
    }

    private static string SanitizeFolderName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length == 0)
        {
            return "world";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch)
            .ToArray();

        var result = new string(chars).Trim('.', ' ');
        return result.Length == 0 ? "world" : result;
    }

    private static string MakeUniqueDirectoryName(string root, string baseName)
    {
        var candidate = baseName;
        for (var index = 2; index < 10000; index++)
        {
            var path = Path.Combine(root, candidate);
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return candidate;
            }

            candidate = $"{baseName} ({index})";
        }

        throw new LauncherException($"Не удалось подобрать свободное имя для папки {baseName}.");
    }

    private static string MakeUniqueDirectoryPath(string root, string baseName)
    {
        var safeName = SanitizeFolderName(baseName);
        var candidate = safeName;

        for (var index = 2; index < 10000; index++)
        {
            var path = Path.Combine(root, candidate);
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }

            candidate = $"{safeName} ({index})";
        }

        throw new LauncherException($"Не удалось подобрать свободное имя для папки {safeName}.");
    }

    private static string ResolveWorldDirectory(string savesRoot, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new LauncherException("Не указано имя папки мира.");
        }

        var name = folderName.Trim();
        if (name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.IndexOf('/') >= 0
            || name.IndexOf('\\') >= 0
            || Path.IsPathRooted(name))
        {
            throw new LauncherException($"Недопустимое имя папки мира: {folderName}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(savesRoot, name));
        if (!IsInside(fullPath, savesRoot)
            || string.Equals(fullPath, Path.GetFullPath(savesRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Недопустимое имя папки мира: {folderName}");
        }

        return fullPath;
    }

    private static string RequireSavesRoot(InstanceProfile instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("Не указана папка игры инстанса.");
        }

        try
        {
            return Path.Combine(Path.GetFullPath(instance.GameDir), SavesFolderName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Недопустимая папка игры инстанса: {instance.GameDir}", ex);
        }
    }

    private static string RequireWorldsRoot(string worldsRoot, string label = "папки миров")
    {
        if (string.IsNullOrWhiteSpace(worldsRoot))
        {
            throw new LauncherException($"Не указан путь {label}.");
        }

        try
        {
            return Path.GetFullPath(worldsRoot.Trim().Trim('"'));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Недопустимый путь {label}: {worldsRoot}", ex);
        }
    }

    private string ResolveRemovedRoot(string? removedRoot)
    {
        if (string.IsNullOrWhiteSpace(removedRoot))
        {
            return Path.Combine(_paths.BackupsDir, RemovedWorldsFolderName);
        }

        return RequireWorldsRoot(removedRoot, "папки удалённых миров");
    }

    private static string? TryGetSavesRoot(InstanceProfile instance)
    {
        if (instance is null || string.IsNullOrWhiteSpace(instance.GameDir))
        {
            return null;
        }

        try
        {
            return Path.Combine(Path.GetFullPath(instance.GameDir), SavesFolderName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static List<(string FullPath, string EntryName, long Size, DateTime LastWriteTime)> EnumerateWorldFiles(
        string worldDir)
    {
        var result = new List<(string FullPath, string EntryName, long Size, DateTime LastWriteTime)>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(worldDir, "*", RecursiveFileOptions))
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                var relativePath = Path
                    .GetRelativePath(worldDir, info.FullName)
                    .Replace('\\', '/');

                if (relativePath.Length == 0)
                {
                    continue;
                }

                result.Add((info.FullName, relativePath, info.Length, info.LastWriteTime));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось прочитать файлы мира {worldDir}: {ex.Message}", ex);
        }

        return result;
    }

    private static long ComputeSizeSafe(string directory)
    {
        try
        {
            return EnumerateWorldFiles(directory).Sum(file => file.Size);
        }
        catch (LauncherException)
        {
            return 0;
        }
    }

    private static DateTimeOffset GetFolderTimestamp(string worldDir, string levelDat)
    {
        try
        {
            var utc = File.GetLastWriteTimeUtc(levelDat);
            if (utc.Year < 1980)
            {
                utc = Directory.GetLastWriteTimeUtc(worldDir);
            }

            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", RecursiveFileOptions))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan);

            input.CopyTo(output, 81920);
            File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(file));
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Другой диск или занятый файл: пробуем копирование.
        }

        try
        {
            CopyDirectory(source, destination, CancellationToken.None);
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new LauncherException($"Бэкап содержит недопустимый путь: {path}");
        }

        var parts = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == ".."
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.EndsWith('.')
                || part.EndsWith(' '))
            {
                throw new LauncherException($"Бэкап содержит недопустимый путь: {path}");
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static bool IsInside(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ClampZipTimestamp(DateTime value)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, value.Kind);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, value.Kind);

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static void CleanupStaleParts(string destination)
    {
        try
        {
            var threshold = DateTime.UtcNow - StalePartAge;

            foreach (var part in Directory.EnumerateFiles(
                         destination,
                         "*.zip.part",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(part) < threshold)
                {
                    TryDeleteFile(part);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Уборка не критична.
        }
    }

    private static void TryDeleteFile(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Содержимое world-backup.json внутри zip (только для служебных нужд лаунчера).</summary>
internal sealed class WorldBackupManifest
{
    public int FormatVersion { get; set; } = 1;

    public string WorldName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public long SizeBytes { get; set; }

    public string LauncherVersion { get; set; } = "";

    public string? InstanceId { get; set; }

    /// <summary>Id серверного профиля; null для бэкапов клиентских миров.</summary>
    public string? ServerId { get; set; }
}

/// <summary>Чтение и запись world-backup.json внутрь zip-бэкапа мира.</summary>
internal static class WorldBackupManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(ZipArchive archive, WorldBackupManifest manifest)
    {
        var entry = archive.CreateEntry(WorldService.ManifestFileName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.Now;

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static WorldBackupManifest? TryRead(ZipArchive archive)
    {
        try
        {
            var entry = archive.GetEntry(WorldService.ManifestFileName);
            if (entry is null || entry.Length > 256 * 1024)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<WorldBackupManifest>(json, JsonOptions);
        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or JsonException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return null;
        }
    }

    public static WorldBackupManifest? TryRead(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return TryRead(archive);
        }
        catch (Exception ex) when (
            ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
