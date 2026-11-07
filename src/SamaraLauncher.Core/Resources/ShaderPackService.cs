using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Resources;

/// <summary>
/// Управление шейдерпаками инстанса: список, выбор активного пака через
/// optionsshaders.txt, импорт zip и удаление в бэкап.
/// </summary>
public sealed class ShaderPackService
{
    public const string FolderName = "shaderpacks";

    public const string OptionsFileName = "optionsshaders.txt";

    private const string ShaderPackKey = "shaderPack";

    private const string OffValue = "OFF";

    private readonly LauncherPaths _paths;

    public ShaderPackService() : this(new LauncherPaths())
    {
    }

    public ShaderPackService(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Сюда переносятся удалённые шейдерпаки.</summary>
    public string BackupsRoot => Path.Combine(_paths.BackupsDir, "removed-shaderpacks");

    /// <summary>Полный путь к папке shaderpacks инстанса (без создания).</summary>
    public string GetFolder(InstanceProfile instance) =>
        PackStorage.GetPackFolder(instance, FolderName);

    /// <summary>
    /// Список zip-файлов и папок-шейдерпаков. Включённым может быть только
    /// один пак — тот, что указан в optionsshaders.txt. Иконка читается
    /// только при <paramref name="includeIcon"/>.
    /// </summary>
    public IReadOnlyList<PackInfo> List(InstanceProfile instance, bool includeIcon = true)
    {
        var folder = GetFolder(instance);
        if (!Directory.Exists(folder))
        {
            return Array.Empty<PackInfo>();
        }

        var enabled = new HashSet<string>(
            GetEnabled(instance),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<PackInfo>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         folder,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!IsZipFile(path))
                {
                    continue;
                }

                var fileName = Path.GetFileName(path);
                result.Add(PackStorage.CreateInfo(
                    path,
                    enabled.Contains(fileName),
                    PackIconReader.Inspect(path, PackKind.ShaderPack, includeIcon)));
            }

            foreach (var path in Directory.EnumerateDirectories(
                         folder,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var inspection = PackIconReader.Inspect(path, PackKind.ShaderPack, includeIcon);
                if (!inspection.IsValid)
                {
                    continue;
                }

                var fileName = Path.GetFileName(path);
                result.Add(PackStorage.CreateInfo(
                    path,
                    enabled.Contains(fileName),
                    inspection));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Возвращаем то, что успели прочитать.
        }

        return result
            .OrderBy(pack => pack.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Имя активного шейдерпака из строки <c>shaderPack:&lt;name&gt;</c>.
    /// Пустой список — шейдеры выключены или файла настроек нет.
    /// </summary>
    public IReadOnlyList<string> GetEnabled(InstanceProfile instance)
    {
        var optionsPath = GetOptionsPath(instance);
        if (!File.Exists(optionsPath))
        {
            return Array.Empty<string>();
        }

        var options = OptionsFile.Load(optionsPath);
        var value = options.GetValue(ShaderPackKey);

        if (string.IsNullOrWhiteSpace(value) || IsOff(value))
        {
            return Array.Empty<string>();
        }

        var name = NormalizeReference(value);
        return name.Length == 0
            ? Array.Empty<string>()
            : new[] { name };
    }

    /// <summary>
    /// Включает шейдерпак (записывает shaderPack:&lt;name&gt;) или выключает
    /// активный пак (shaderPack:OFF). Меняется только эта строка; остальные
    /// строки, порядок, кодировка и переводы строк сохраняются.
    /// </summary>
    public void SetEnabled(InstanceProfile instance, string fileName, bool enabled)
    {
        var name = PackStorage.RequireFileName(fileName);
        var optionsPath = GetOptionsPath(instance);
        var options = OptionsFile.Load(optionsPath);
        var current = options.GetValue(ShaderPackKey);

        if (enabled)
        {
            options.SetValue(ShaderPackKey, name);
            options.Save();
            return;
        }

        if (string.IsNullOrWhiteSpace(current) || IsOff(current))
        {
            return;
        }

        if (!string.Equals(
                NormalizeReference(current),
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        options.SetValue(ShaderPackKey, OffValue);
        options.Save();
    }

    /// <summary>
    /// Копирует zip-файлы в shaderpacks. Архив должен содержать папку shaders
    /// или pack.mcmeta. Существующие файлы не перезаписываются, пак не включается.
    /// </summary>
    public IReadOnlyList<PackInfo> Import(
        InstanceProfile instance,
        IEnumerable<string> zipPaths) =>
        Import(instance, zipPaths, progress: null);

    /// <summary>То же с отчётом о прогрессе импорта.</summary>
    public IReadOnlyList<PackInfo> Import(
        InstanceProfile instance,
        IEnumerable<string> zipPaths,
        IProgress<LauncherProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(zipPaths);

        var folder = GetFolder(instance);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку шейдерпаков {folder}: {ex.Message}",
                ex);
        }

        var sources = new List<(string Source, string Target)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in zipPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var source = ResolveSourcePath(rawPath);

            if (!File.Exists(source))
            {
                throw new LauncherException($"Файл шейдерпака не найден: {source}");
            }

            if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(
                    $"Можно импортировать только zip-файлы шейдерпаков: {source}");
            }

            var inspection = PackIconReader.Inspect(source, PackKind.ShaderPack);
            if (!inspection.IsValid)
            {
                throw new LauncherException(
                    $"В архиве нет папки shaders или pack.mcmeta: {Path.GetFileName(source)}");
            }

            var fileName = Path.GetFileName(source);
            if (!names.Add(fileName))
            {
                throw new LauncherException(
                    $"В списке импорта несколько файлов с именем {fileName}.");
            }

            var target = Path.Combine(folder, fileName);

            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new LauncherException(
                    $"Шейдерпак уже существует в папке {FolderName}: {fileName}");
            }

            sources.Add((source, target));
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Pack,
            $"Импорт шейдерпаков: 0 из {sources.Count}",
            0,
            sources.Count));

        var imported = new List<PackInfo>(sources.Count);
        var enabled = new HashSet<string>(
            GetEnabled(instance),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (source, target) in sources)
        {
            PackStorage.CopyFileAtomic(source, target);

            var fileName = Path.GetFileName(target);
            var inspection = PackIconReader.Inspect(target, PackKind.ShaderPack);

            imported.Add(PackStorage.CreateInfo(
                target,
                enabled.Contains(fileName),
                inspection));

            progress?.Report(new LauncherProgress(
                ProgressStage.Pack,
                $"Импорт шейдерпаков: {imported.Count} из {sources.Count}",
                imported.Count,
                sources.Count));
        }

        return imported;
    }

    /// <summary>
    /// Удаляет zip или папку-шейдерпак. По умолчанию переносит в
    /// backups\removed-shaderpacks\&lt;timestamp&gt;.
    /// Возвращает путь, куда перенесён пак (или исходный путь при удалении).
    /// </summary>
    public string Delete(PackInfo pack, bool moveToBackups = true)
    {
        var path = PackStorage.RequirePackPath(pack);

        if (Directory.Exists(path))
        {
            var hasShaders = Directory.Exists(Path.Combine(path, "shaders"));
            var hasPackMcmeta = File.Exists(Path.Combine(path, "pack.mcmeta"));

            if (!hasShaders && !hasPackMcmeta)
            {
                throw new LauncherException(
                    $"В папке нет shaders или pack.mcmeta: {path}");
            }
        }
        else if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Шейдерпак должен быть zip-файлом: {path}");
        }

        if (moveToBackups)
        {
            return PackStorage.MoveToBackup(path, BackupsRoot, pack.FileName);
        }

        PackStorage.DeletePermanently(path);
        return path;
    }

    /// <summary>Создаёт (при необходимости) и открывает папку shaderpacks.</summary>
    public void OpenFolder(InstanceProfile instance) =>
        PackStorage.OpenFolder(
            GetFolder(instance),
            "Не удалось открыть папку шейдерпаков");

    private string GetOptionsPath(InstanceProfile instance) =>
        Path.Combine(PackStorage.GetGameDir(instance), OptionsFileName);

    private static string ResolveSourcePath(string rawPath)
    {
        try
        {
            return Path.GetFullPath(rawPath.Trim().Trim('"'));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Недопустимый путь к шейдерпаку: {rawPath}", ex);
        }
    }

    private static bool IsZipFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsOff(string value) =>
        string.Equals(value.Trim(), OffValue, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReference(string value)
    {
        var text = value.Trim().Trim('"').Replace('\\', '/');

        if (text.StartsWith("file/", StringComparison.OrdinalIgnoreCase))
        {
            text = text["file/".Length..];
        }

        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        return text;
    }
}
