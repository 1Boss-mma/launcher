using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Instances;

/// <summary>
/// Операции над списком инстансов: создание, переименование, удаление,
/// дублирование, экспорт и импорт.
/// </summary>
public sealed class InstanceManager
{
    private const string ManifestEntryName = "instance.json";
    private const long MaxImportTotalBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxImportEntries = 200_000;

    private static readonly string[] GameDataFolders = { "mods", "config", "defaultconfigs" };

    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static readonly Dictionary<char, string> Transliteration = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya", ['і'] = "i", ['ї'] = "i",
        ['є'] = "e", ['ґ'] = "g"
    };

    private readonly InstanceStore _store;
    private readonly LauncherPaths _paths;

    public InstanceManager() : this(null, null)
    {
    }

    public InstanceManager(InstanceStore? store) : this(store, null)
    {
    }

    public InstanceManager(LauncherPaths? paths) : this(null, paths)
    {
    }

    public InstanceManager(InstanceStore? store, LauncherPaths? paths)
    {
        _store = store ?? new InstanceStore();
        _paths = paths ?? new LauncherPaths();
    }

    /// <summary>Инстансы, отсортированные по SortOrder, затем по имени.</summary>
    public IReadOnlyList<InstanceProfile> List(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Instances
            .OrderBy(instance => instance.SortOrder)
            .ThenBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(instance => instance.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Создаёт инстанс с уникальными id и именем. Из шаблона копируются только
    /// настройки (Java, память, версия, pack.toml и цвет), без модов и миров.
    /// </summary>
    public InstanceProfile Create(
        LauncherConfig config,
        string name,
        string? gameDir = null,
        string? templateInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cleanName = RequireName(name);

        InstanceProfile? template = null;
        if (!string.IsNullOrWhiteSpace(templateInstanceId))
        {
            template = config.Find(templateInstanceId)
                ?? throw new LauncherException($"Шаблон инстанса не найден: {templateInstanceId}");
        }

        var id = BuildUniqueId(config, cleanName, gameDir is null);
        var uniqueName = BuildUniqueName(config, cleanName);
        var directory = ResolveNewGameDir(id, gameDir);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {directory}: {ex.Message}", ex);
        }

        var instance = new InstanceProfile
        {
            Id = id,
            Name = uniqueName,
            GameDir = directory,
            SortOrder = NextSortOrder(config)
        };

        if (template is not null)
        {
            instance.JavaPath = template.JavaPath;
            instance.MemoryMb = template.MemoryMb;
            instance.MinMemoryMb = template.MinMemoryMb;
            instance.JvmArgs = template.JvmArgs is null
                ? new List<string> { "-XX:+UseG1GC" }
                : new List<string>(template.JvmArgs);
            instance.VersionId = template.VersionId;
            instance.MinecraftVersion = template.MinecraftVersion;
            instance.ForgeVersion = template.ForgeVersion;
            instance.Loader = template.Loader;
            instance.FabricVersion = template.FabricVersion;
            instance.OptiFineVersion = template.OptiFineVersion;
            instance.PackUrl = template.PackUrl;
            instance.IconColor = string.IsNullOrWhiteSpace(template.IconColor)
                ? "#5FBE4A"
                : template.IconColor;
        }

        config.Instances.Add(instance);
        Save(config);
        return instance;
    }

    /// <summary>Переименовывает инстанс. false — если инстанс не найден или имя занято.</summary>
    public bool Rename(LauncherConfig config, string id, string newName)
    {
        ArgumentNullException.ThrowIfNull(config);

        var instance = config.Find(id);
        if (instance is null)
        {
            return false;
        }

        var cleanName = newName?.Trim() ?? "";
        if (string.IsNullOrEmpty(cleanName))
        {
            return false;
        }

        var duplicate = config.Instances.Any(other =>
            !ReferenceEquals(other, instance) &&
            string.Equals(other.Name, cleanName, StringComparison.CurrentCultureIgnoreCase));

        if (duplicate)
        {
            return false;
        }

        instance.Name = cleanName;
        Save(config);
        return true;
    }

    /// <summary>
    /// Удаляет инстанс из конфига. Последний инстанс удалить нельзя.
    /// <paramref name="deleteGameDir"/> удаляет папку игры только внутри InstancesDir.
    /// </summary>
    public bool Remove(LauncherConfig config, string id, bool deleteGameDir = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var instance = config.Find(id);
        if (instance is null)
        {
            return false;
        }

        if (config.Instances.Count <= 1)
        {
            throw new LauncherException("Нельзя удалить последний инстанс: в лаунчере должен остаться хотя бы один.");
        }

        config.Instances.Remove(instance);

        if (string.Equals(config.LastInstanceId, id, StringComparison.OrdinalIgnoreCase))
        {
            config.LastInstanceId = config.Instances
                .OrderBy(other => other.SortOrder)
                .ThenBy(other => other.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault()?.Id;
        }

        // Сначала фиксируем конфиг: если удаление папки сорвётся, конфиг уже не
        // ссылается на несуществующий инстанс.
        Save(config);

        if (deleteGameDir)
        {
            DeleteGameDirectory(instance);
        }

        return true;
    }

    /// <summary>
    /// Дублирует инстанс. При <paramref name="copyGameData"/> копируются только
    /// mods, config и defaultconfigs.
    /// </summary>
    public InstanceProfile Duplicate(
        LauncherConfig config,
        string id,
        string? newName = null,
        bool copyGameData = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var source = config.Find(id)
            ?? throw new LauncherException($"Инстанс не найден: {id}");

        var name = string.IsNullOrWhiteSpace(newName)
            ? source.Name + " (копия)"
            : newName.Trim();

        var uniqueName = BuildUniqueName(config, name);
        var newId = BuildUniqueId(config, name, avoidExistingDirectory: true);
        var directory = ResolveNewGameDir(newId, null);

        try
        {
            Directory.CreateDirectory(directory);

            if (copyGameData)
            {
                CopyGameData(source.GameDir, directory);
            }
        }
        catch (LauncherException)
        {
            TryDeleteDirectory(directory);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(directory);
            throw new LauncherException(
                $"Не удалось скопировать папку игры в {directory}: {ex.Message}", ex);
        }

        var copy = new InstanceProfile
        {
            Id = newId,
            Name = uniqueName,
            GameDir = directory,
            JavaPath = source.JavaPath,
            MemoryMb = source.MemoryMb,
            MinMemoryMb = source.MinMemoryMb,
            JvmArgs = source.JvmArgs is null
                ? new List<string> { "-XX:+UseG1GC" }
                : new List<string>(source.JvmArgs),
            VersionId = source.VersionId,
            MinecraftVersion = source.MinecraftVersion,
            ForgeVersion = source.ForgeVersion,
            Loader = source.Loader,
            FabricVersion = source.FabricVersion,
            OptiFineVersion = source.OptiFineVersion,
            Username = source.Username,
            Uuid = source.Uuid,
            PackUrl = source.PackUrl,
            IconColor = source.IconColor,
            Notes = source.Notes,
            LastVersionId = source.LastVersionId,
            SortOrder = NextSortOrder(config)
        };

        config.Instances.Add(copy);
        Save(config);
        return copy;
    }

    /// <summary>
    /// Упаковывает настройки инстанса и (опционально) mods/config/defaultconfigs в zip.
    /// Возвращает полный путь к архиву.
    /// </summary>
    public string Export(InstanceProfile instance, string targetZipPath, bool includeModsAndConfig = true)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(targetZipPath))
        {
            throw new LauncherException("Не указан путь для экспорта инстанса.");
        }

        var target = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(targetZipPath.Trim().Trim('"')));

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку для экспорта {directory}: {ex.Message}", ex);
            }
        }

        var tempPath = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteJsonEntry(archive, instance);

                if (includeModsAndConfig && !string.IsNullOrWhiteSpace(instance.GameDir))
                {
                    string gameDir;
                    try
                    {
                        gameDir = Path.GetFullPath(
                            Environment.ExpandEnvironmentVariables(instance.GameDir.Trim().Trim('"')));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        throw new LauncherException($"Некорректная папка игры: {instance.GameDir}", ex);
                    }

                    foreach (var folder in GameDataFolders)
                    {
                        var source = Path.Combine(gameDir, folder);
                        if (!Directory.Exists(source))
                        {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(source, "*", RecursiveFiles))
                        {
                            var fullPath = Path.GetFullPath(file);
                            if (string.Equals(fullPath, target, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fullPath, tempPath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var entryName = Path
                                .GetRelativePath(gameDir, fullPath)
                                .Replace('\\', '/');

                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            entry.LastWriteTime = ClampZipTimestamp(File.GetLastWriteTime(fullPath));
                            using var sourceStream = new FileStream(
                                fullPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var destination = entry.Open();
                            sourceStream.CopyTo(destination);
                        }
                    }
                }
            }

            File.Move(tempPath, target, overwrite: true);
            return target;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LauncherException(
                $"Не удалось экспортировать инстанс в {target}: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// Импортирует инстанс из zip в новую папку внутри InstancesDir.
    /// Недопустимые пути в архиве (zip-slip) отклоняются.
    /// </summary>
    public InstanceProfile Import(LauncherConfig config, string zipPath, string? newName = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new LauncherException($"Файл импорта не найден: {zipPath}");
        }

        InstanceProfile imported;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var manifest = archive.Entries.FirstOrDefault(entry =>
                string.Equals(
                    NormalizeEntryPath(entry.FullName, throwOnTraversal: false),
                    ManifestEntryName,
                    StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                throw new LauncherException(
                    $"В архиве {Path.GetFileName(zipPath)} нет {ManifestEntryName}.");
            }

            using (var stream = manifest.Open())
            {
                imported = JsonSerializer.Deserialize<InstanceProfile>(stream, InstanceStore.JsonOptions)
                    ?? throw new LauncherException(
                        $"Не удалось прочитать {ManifestEntryName} из {Path.GetFileName(zipPath)}.");
            }

            var name = string.IsNullOrWhiteSpace(newName)
                ? imported.Name?.Trim()
                : newName.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(zipPath);
            }

            var id = BuildUniqueId(config, name, avoidExistingDirectory: true);
            var uniqueName = BuildUniqueName(config, name);
            var gameDir = Path.Combine(Path.GetFullPath(_paths.InstancesDir), id);

            InstanceProfile create;

            try
            {
                Directory.CreateDirectory(gameDir);
                ExtractGameData(archive, gameDir);
            }
            catch
            {
                TryDeleteDirectory(gameDir);
                throw;
            }

            create = new InstanceProfile
            {
                Id = id,
                Name = uniqueName,
                GameDir = gameDir,
                JavaPath = string.IsNullOrWhiteSpace(imported.JavaPath) ? "auto" : imported.JavaPath,
                MemoryMb = imported.MemoryMb > 0 ? imported.MemoryMb : 6144,
                MinMemoryMb = imported.MinMemoryMb > 0 ? imported.MinMemoryMb : 2048,
                JvmArgs = imported.JvmArgs is null
                    ? new List<string> { "-XX:+UseG1GC" }
                    : new List<string>(imported.JvmArgs),
                VersionId = imported.VersionId ?? "",
                MinecraftVersion = string.IsNullOrWhiteSpace(imported.MinecraftVersion)
                    ? "1.20.1"
                    : imported.MinecraftVersion,
                ForgeVersion = imported.ForgeVersion ?? "",
                Loader = imported.Loader ?? "",
                FabricVersion = imported.FabricVersion ?? "",
                OptiFineVersion = imported.OptiFineVersion ?? "",
                Username = string.IsNullOrWhiteSpace(imported.Username) ? "Player" : imported.Username,
                Uuid = imported.Uuid,
                PackUrl = imported.PackUrl ?? "",
                IconColor = string.IsNullOrWhiteSpace(imported.IconColor) ? "#5FBE4A" : imported.IconColor,
                Notes = imported.Notes ?? "",
                LastVersionId = imported.LastVersionId,
                SortOrder = NextSortOrder(config)
            };

            config.Instances.Add(create);

            try
            {
                Save(config);
            }
            catch
            {
                config.Instances.Remove(create);
                TryDeleteDirectory(gameDir);
                throw;
            }

            return create;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Файл {ManifestEntryName} в архиве повреждён: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(
                $"Архив {Path.GetFileName(zipPath)} повреждён: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось импортировать инстанс из {zipPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Сохраняет конфиг через <see cref="InstanceStore"/>.</summary>
    public void Save(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _store.Save(config);
    }

    private void DeleteGameDirectory(InstanceProfile instance)
    {
        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры — удалять нечего.");
        }

        string gameDir;
        string instancesRoot;

        try
        {
            gameDir = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(instance.GameDir.Trim().Trim('"')));
            instancesRoot = Path.GetFullPath(_paths.InstancesDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь папки игры: {instance.GameDir}", ex);
        }

        if (!IsStrictlyInside(gameDir, instancesRoot))
        {
            throw new LauncherException(
                $"Папка игры {gameDir} находится вне {instancesRoot} — удаление отменено. " +
                "Удалить её можно только вручную.");
        }

        if (File.Exists(gameDir) && !Directory.Exists(gameDir))
        {
            throw new LauncherException($"Путь {gameDir} не является папкой — удаление отменено.");
        }

        if (!Directory.Exists(gameDir))
        {
            return;
        }

        try
        {
            Directory.Delete(gameDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось удалить папку игры {gameDir}: {ex.Message}. " +
                "Закройте игру и повторите попытку.", ex);
        }
    }

    private string ResolveNewGameDir(string id, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return Path.Combine(Path.GetFullPath(_paths.InstancesDir), id);
        }

        try
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(gameDir.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректная папка игры: {gameDir}", ex);
        }
    }

    private static string RequireName(string name)
    {
        var cleanName = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new LauncherException("Название инстанса не может быть пустым.");
        }

        return cleanName;
    }

    private static string BuildUniqueName(LauncherConfig config, string name)
    {
        var cleanName = name.Trim();

        if (!config.Instances.Any(instance =>
                string.Equals(instance.Name, cleanName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return cleanName;
        }

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{cleanName} ({index})";
            if (!config.Instances.Any(instance =>
                    string.Equals(instance.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new LauncherException($"Не удалось подобрать уникальное имя для \"{cleanName}\".");
    }

    private string BuildUniqueId(LauncherConfig config, string name, bool avoidExistingDirectory)
    {
        var baseId = Slugify(name);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = index == 1 ? baseId : $"{baseId}-{index}";

            var taken = config.Instances.Any(instance =>
                string.Equals(instance.Id, candidate, StringComparison.OrdinalIgnoreCase));

            if (!taken && avoidExistingDirectory)
            {
                taken = Directory.Exists(Path.Combine(Path.GetFullPath(_paths.InstancesDir), candidate));
            }

            if (!taken)
            {
                return candidate;
            }
        }

        throw new LauncherException($"Не удалось подобрать уникальный id для \"{name}\".");
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var raw in name.Trim().ToLowerInvariant())
        {
            if (raw is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(raw);
                continue;
            }

            if (Transliteration.TryGetValue(raw, out var translated))
            {
                builder.Append(translated);
                continue;
            }

            if (char.IsLetterOrDigit(raw))
            {
                builder.Append(raw);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', ' ');

        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        return string.IsNullOrEmpty(slug) ? "instance" : slug;
    }

    private static int NextSortOrder(LauncherConfig config)
    {
        if (config.Instances.Count == 0)
        {
            return 0;
        }

        var max = config.Instances.Max(instance => instance.SortOrder);

        // Пока порядок никто не настраивал (все SortOrder = 0), новые инстансы
        // тоже остаются с 0 — тогда список сортируется по имени.
        if (max <= 0)
        {
            return 0;
        }

        return max == int.MaxValue ? max : max + 1;
    }

    private static void CopyGameData(string sourceGameDir, string targetGameDir)
    {
        if (string.IsNullOrWhiteSpace(sourceGameDir) || !Directory.Exists(sourceGameDir))
        {
            return;
        }

        foreach (var folder in GameDataFolders)
        {
            var source = Path.Combine(sourceGameDir, folder);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(targetGameDir, folder);
            Directory.CreateDirectory(destination);

            foreach (var sourceFile in Directory.EnumerateFiles(source, "*", RecursiveFiles))
            {
                var relative = Path.GetRelativePath(source, sourceFile);
                var targetFile = Path.Combine(destination, relative);

                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(sourceFile, targetFile, overwrite: true);
            }
        }
    }

    private static void ExtractGameData(ZipArchive archive, string gameDir)
    {
        var root = Path.GetFullPath(gameDir);

        // Сначала проверяем весь архив: размер, число файлов и пути.
        long totalBytes = 0;
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryPath(entry.FullName, throwOnTraversal: true);
            if (normalized is null || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!IsAllowedGameDataEntry(normalized))
            {
                continue;
            }

            if (entry.Length > 0)
            {
                totalBytes += entry.Length;
            }

            count++;

            if (count > MaxImportEntries || totalBytes > MaxImportTotalBytes)
            {
                throw new LauncherException(
                    "Архив слишком большой для безопасного импорта.");
            }

            var target = Path.GetFullPath(
                Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsStrictlyInside(target, root))
            {
                throw new LauncherException(
                    $"Архив содержит недопустимый путь: {entry.FullName}");
            }
        }

        long written = 0;

        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryPath(entry.FullName, throwOnTraversal: true);
            if (normalized is null || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (!IsAllowedGameDataEntry(normalized))
            {
                continue;
            }

            var target = Path.GetFullPath(
                Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = target + ".import-" + Guid.NewGuid().ToString("N");

            try
            {
                using var input = entry.Open();
                using var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.SequentialScan);

                var buffer = new byte[81920];
                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;

                    if (written > MaxImportTotalBytes)
                    {
                        throw new LauncherException(
                            "Архив слишком большой для безопасного импорта.");
                    }

                    output.Write(buffer, 0, read);
                }

                File.Move(tempPath, target, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    private static bool IsAllowedGameDataEntry(string normalized)
    {
        foreach (var folder in GameDataFolders)
        {
            if (normalized.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeEntryPath(string entryName, bool throwOnTraversal)
    {
        var normalized = entryName.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.StartsWith('/')
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':'))
        {
            if (throwOnTraversal)
            {
                throw new LauncherException($"Архив содержит недопустимый путь: {entryName}");
            }

            return null;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        if (parts.Any(part => part == ".."))
        {
            if (throwOnTraversal)
            {
                throw new LauncherException($"Архив содержит недопустимый путь: {entryName}");
            }

            return null;
        }

        return string.Join('/', parts.Where(part => part != "."));
    }

    private static bool IsStrictlyInside(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        return path.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteJsonEntry(ZipArchive archive, InstanceProfile instance)
    {
        var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        entry.LastWriteTime = ClampZipTimestamp(DateTime.Now);

        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, instance, InstanceStore.JsonOptions);
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
            // Временный файл не критичен.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
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
            // Уборка после ошибки не должна скрывать исходную ошибку.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }
}
