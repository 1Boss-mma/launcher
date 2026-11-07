using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Backups;

/// <summary>
/// Zip-бэкапы mods/config/defaultconfigs и хранилище удалённых модов.
/// </summary>
public sealed class BackupService
{
    public const int DefaultRetention = 5;

    private const string BackupManifestEntry = "samara-backup.json";
    private const long MaxRestoreTotalBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxRestoreEntries = 200_000;
    private static readonly TimeSpan StalePartAge = TimeSpan.FromHours(1);

    private static readonly string[] BackupFolders = { "mods", "config", "defaultconfigs" };

    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BackupService() : this(new LauncherPaths())
    {
    }

    public BackupService(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        BackupsRoot = paths.BackupsDir;
    }

    public BackupService(string backupsRoot)
    {
        if (string.IsNullOrWhiteSpace(backupsRoot))
        {
            throw new LauncherException("Не указана папка для бэкапов.");
        }

        BackupsRoot = Path.GetFullPath(backupsRoot);
    }

    public string BackupsRoot { get; }

    /// <summary>Сюда ModService переносит удалённые моды.</summary>
    public string RemovedModsRoot => Path.Combine(BackupsRoot, "removed");

    /// <summary>
    /// Создаёт zip с mods, config и defaultconfigs. Если таких папок нет — возвращает null.
    /// Тяжёлое сжатие выполняется в пуле потоков, zip сначала пишется в <c>.part</c>.
    /// </summary>
    public async Task<string?> CreateBackupAsync(
        string gameDir,
        string instanceId,
        int retention = DefaultRetention,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для бэкапа.");
        }

        var gameRoot = Path.GetFullPath(gameDir);
        if (!Directory.Exists(gameRoot))
        {
            throw new LauncherException($"Папка игры не найдена: {gameRoot}");
        }

        var sourceFolders = BackupFolders
            .Select(folder => Path.Combine(gameRoot, folder))
            .Where(Directory.Exists)
            .ToList();

        if (sourceFolders.Count == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(BackupsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку бэкапов {BackupsRoot}: {ex.Message}", ex);
        }

        var files = await Task.Run(
            () => EnumerateBackupFiles(sourceFolders, gameRoot, ct),
            ct).ConfigureAwait(false);

        CleanupStaleParts(BackupsRoot);

        var zipPath = BuildBackupPath(instanceId);
        var partPath = zipPath + ".part";

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Создание бэкапа сборки ({files.Count} файлов)...",
            0,
            files.Count));

        try
        {
            await Task.Run(
                () => CreateArchive(partPath, instanceId, files, progress, ct),
                ct).ConfigureAwait(false);

            File.Move(partPath, zipPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (LauncherException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(partPath);
            throw new LauncherException(
                $"Не удалось создать бэкап сборки: {ex.Message}",
                ex);
        }

        ApplyRetention(instanceId, retention);

        progress?.Report(new LauncherProgress(
            ProgressStage.Backup,
            $"Бэкап создан: {Path.GetFileName(zipPath)}",
            files.Count,
            files.Count));

        return zipPath;
    }

    private static List<(string FullPath, string EntryName)> EnumerateBackupFiles(
        List<string> sourceFolders,
        string gameRoot,
        CancellationToken ct)
    {
        var files = new List<(string FullPath, string EntryName)>();

        foreach (var folder in sourceFolders)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", RecursiveFiles))
                {
                    ct.ThrowIfCancellationRequested();

                    var entryName = Path
                        .GetRelativePath(gameRoot, file)
                        .Replace('\\', '/');

                    files.Add((file, entryName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось прочитать файлы для бэкапа в {folder}: {ex.Message}", ex);
            }
        }

        return files;
    }

    private static void CreateArchive(
        string partPath,
        string instanceId,
        List<(string FullPath, string EntryName)> files,
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

        WriteManifest(archive, instanceId);

        long completed = 0;

        foreach (var (file, entryName) in files)
        {
            ct.ThrowIfCancellationRequested();

            // Jar'ы уже сжаты; повторное Optimal-сжатие тратит процессор впустую.
            var level = entryName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.Fastest
                : CompressionLevel.Optimal;

            var entry = archive.CreateEntry(entryName, level);
            entry.LastWriteTime = ClampZipTimestamp(File.GetLastWriteTime(file));

            using var source = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var destination = entry.Open();
            source.CopyTo(destination, 81920);

            completed++;
            if (completed % 25 == 0 || completed == files.Count)
            {
                progress?.Report(new LauncherProgress(
                    ProgressStage.Backup,
                    $"Бэкап: {completed} из {files.Count}",
                    completed,
                    files.Count));
            }
        }
    }

    private static void WriteManifest(ZipArchive archive, string instanceId)
    {
        var manifest = new BackupManifest
        {
            InstanceId = instanceId ?? "",
            CreatedUtc = DateTimeOffset.UtcNow,
            LauncherVersion = LauncherVersion.Current
        };

        var entry = archive.CreateEntry(BackupManifestEntry, CompressionLevel.NoCompression);
        entry.LastWriteTime = DateTimeOffset.Now;

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    /// <summary>Возвращает бэкапы инстанса, новые первыми.</summary>
    public IReadOnlyList<string> ListBackups(string instanceId)
    {
        if (!Directory.Exists(BackupsRoot))
        {
            return Array.Empty<string>();
        }

        var safeId = SanitizeInstanceId(instanceId);
        var pattern = new Regex(
            "^" + Regex.Escape(safeId) + @"-\d{8}-\d{6}(?:-\d+)?\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var result = new List<string>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         BackupsRoot,
                         "*.zip",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!pattern.IsMatch(name))
                {
                    continue;
                }

                if (!ManifestMatchesInstance(path, instanceId))
                {
                    continue;
                }

                result.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Возвращаем то, что успели перечислить.
        }

        return result
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Распаковывает zip поверх gameDir. Существующие mods/config/defaultconfigs
    /// заменяются целиком; при ошибке выполняется откат.
    /// Вызывать только после подтверждения пользователя.
    /// </summary>
    public Task RestoreBackupAsync(
        string zipPath,
        string gameDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new LauncherException($"Файл бэкапа не найден: {zipPath}");
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для восстановления бэкапа.");
        }

        var gameRoot = Path.GetFullPath(gameDir);
        var source = Path.GetFullPath(zipPath);

        return Task.Run(() => RestoreBackupCore(source, gameRoot, ct), ct);
    }

    private static void RestoreBackupCore(string zipPath, string gameRoot, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(gameRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameRoot}: {ex.Message}", ex);
        }

        CleanupStaleRestoreDirectories(gameRoot);

        var staging = Path.Combine(gameRoot, ".samara-restore-" + Guid.NewGuid().ToString("N"));
        var committed = new List<(string Target, string? Displaced)>();

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var entries = new List<(ZipArchiveEntry Entry, string RelativePath)>();
            long totalBytes = 0;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var relative = NormalizeArchivePath(entry.FullName);
                if (string.Equals(relative, BackupManifestEntry, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsAllowedBackupEntry(relative))
                {
                    throw new LauncherException(
                        $"Бэкап содержит недопустимый путь: {entry.FullName}");
                }

                if (entry.Length > 0)
                {
                    totalBytes += entry.Length;
                }

                if (entries.Count >= MaxRestoreEntries || totalBytes > MaxRestoreTotalBytes)
                {
                    throw new LauncherException(
                        "Бэкап слишком большой для безопасного восстановления.");
                }

                entries.Add((entry, relative));
            }

            if (entries.Count == 0)
            {
                throw new LauncherException(
                    $"Бэкап не содержит mods/config/defaultconfigs: {Path.GetFileName(zipPath)}");
            }

            Directory.CreateDirectory(staging);

            long totalExtracted = 0;

            foreach (var (entry, relative) in entries)
            {
                ct.ThrowIfCancellationRequested();

                var targetPath = Path.GetFullPath(
                    Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));

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

                using var input = entry.Open();
                using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.SequentialScan);

                CopyWithLimit(input, output, ref totalExtracted, MaxRestoreTotalBytes);
            }

            foreach (var folder in BackupFolders)
            {
                var staged = Path.Combine(staging, folder);
                if (!Directory.Exists(staged))
                {
                    continue;
                }

                var target = Path.Combine(gameRoot, folder);

                if (File.Exists(target) && !Directory.Exists(target))
                {
                    throw new LauncherException(
                        $"Нельзя заменить {target}: по этому пути находится файл.");
                }

                string? displaced = null;

                if (Directory.Exists(target))
                {
                    displaced = target + ".replaced-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, displaced);
                }

                try
                {
                    Directory.Move(staged, target);
                    committed.Add((target, displaced));
                }
                catch
                {
                    if (displaced is not null && !Directory.Exists(target))
                    {
                        TryMoveDirectory(displaced, target);
                    }

                    throw;
                }
            }

            foreach (var (_, displaced) in committed)
            {
                if (displaced is not null)
                {
                    TryDeleteDirectory(displaced);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Rollback(committed);
            throw;
        }
        catch (LauncherException)
        {
            Rollback(committed);
            throw;
        }
        catch (InvalidDataException ex)
        {
            Rollback(committed);
            throw new LauncherException($"Бэкап повреждён: {Path.GetFileName(zipPath)}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(committed);
            throw new LauncherException(
                $"Не удалось восстановить бэкап: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void Rollback(List<(string Target, string? Displaced)> committed)
    {
        for (var index = committed.Count - 1; index >= 0; index--)
        {
            var (target, displaced) = committed[index];

            if (Directory.Exists(target))
            {
                TryDeleteDirectory(target);
            }

            if (displaced is not null && Directory.Exists(displaced))
            {
                TryMoveDirectory(displaced, target);
            }
        }
    }

    private static bool ManifestMatchesInstance(string zipPath, string instanceId)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(BackupManifestEntry);
            if (entry is null || entry.Length > 64 * 1024)
            {
                // Старые бэкапы без манифеста считаем своими: имя уже проверено regex.
                return true;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), ManifestJsonOptions);

            return manifest is null
                   || string.IsNullOrWhiteSpace(manifest.InstanceId)
                   || string.Equals(manifest.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return true;
        }
    }

    private string BuildBackupPath(string instanceId)
    {
        var safeId = SanitizeInstanceId(instanceId);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(BackupsRoot, $"{safeId}-{stamp}.zip");

        if (!File.Exists(path) && !File.Exists(path + ".part"))
        {
            return path;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(BackupsRoot, $"{safeId}-{stamp}-{index}.zip");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
            {
                return candidate;
            }
        }

        throw new LauncherException("Не удалось подобрать имя для нового бэкапа.");
    }

    private void ApplyRetention(string instanceId, int retention)
    {
        var keep = Math.Max(1, retention);

        try
        {
            foreach (var old in ListBackups(instanceId).Skip(keep))
            {
                TryDelete(old);
            }
        }
        catch (IOException)
        {
            // Чистка старых бэкапов не должна ломать уже созданный бэкап.
        }
        catch (UnauthorizedAccessException)
        {
            // То же самое: старый файл может быть занят.
        }
    }

    public static string SanitizeInstanceId(string? instanceId)
    {
        var value = string.IsNullOrWhiteSpace(instanceId) ? "instance" : instanceId.Trim();
        var invalid = Path.GetInvalidFileNameChars();

        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();

        var result = new string(chars).Trim('.', ' ');
        return string.IsNullOrEmpty(result) ? "instance" : result;
    }

    private static void CleanupStaleParts(string backupsRoot)
    {
        try
        {
            var threshold = DateTime.UtcNow - StalePartAge;

            foreach (var part in Directory.EnumerateFiles(
                         backupsRoot,
                         "*.zip.part",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(part) < threshold)
                {
                    TryDelete(part);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Уборка не критична.
        }
    }

    private static void CleanupStaleRestoreDirectories(string gameRoot)
    {
        try
        {
            var threshold = DateTime.UtcNow - TimeSpan.FromDays(1);

            foreach (var directory in Directory.EnumerateDirectories(
                         gameRoot,
                         ".samara-restore-*",
                         SearchOption.TopDirectoryOnly))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < threshold)
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Уборка не критична.
        }
    }

    private static bool IsAllowedBackupEntry(string relativePath)
    {
        foreach (var folder in BackupFolders)
        {
            if (relativePath.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':'))
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

        if (parts.Count == 0)
        {
            throw new LauncherException($"Бэкап содержит недопустимый путь: {path}");
        }

        return string.Join('/', parts);
    }

    private static bool IsInside(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

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

    private static void CopyWithLimit(Stream source, Stream destination, ref long total, long limit)
    {
        var buffer = new byte[81920];
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;

            if (total > limit)
            {
                throw new LauncherException(
                    "Бэкап слишком большой для безопасного восстановления.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static void TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Откат не удался — исходная ошибка важнее.
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BackupManifest
    {
        public int FormatVersion { get; set; } = 1;

        public string InstanceId { get; set; } = "";

        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

        public string LauncherVersion { get; set; } = "";
    }
}
