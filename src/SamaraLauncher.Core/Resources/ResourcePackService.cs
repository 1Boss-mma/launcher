using System.Text.Encodings.Web;
using System.Text.Json;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Resources;

/// <summary>
/// Управление ресурспаками инстанса: список, включение через options.txt,
/// импорт zip и удаление в бэкап.
/// </summary>
public sealed class ResourcePackService
{
    public const string FolderName = "resourcepacks";

    public const string OptionsFileName = "options.txt";

    private const string ResourcePacksKey = "resourcePacks";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly LauncherPaths _paths;

    public ResourcePackService() : this(new LauncherPaths())
    {
    }

    public ResourcePackService(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Сюда переносятся удалённые ресурспаки.</summary>
    public string BackupsRoot => Path.Combine(_paths.BackupsDir, "removed-resourcepacks");

    /// <summary>Полный путь к папке resourcepacks инстанса (без создания).</summary>
    public string GetFolder(InstanceProfile instance) =>
        PackStorage.GetPackFolder(instance, FolderName);

    /// <summary>
    /// Список zip-файлов и папок-паков с pack.mcmeta. Описание читается из
    /// pack.mcmeta; иконка читается только при <paramref name="includeIcon"/>.
    /// Ошибка одного пака не прерывает список.
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
                    PackIconReader.Inspect(path, PackKind.ResourcePack, includeIcon)));
            }

            foreach (var path in Directory.EnumerateDirectories(
                         folder,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var inspection = PackIconReader.Inspect(
                    path,
                    PackKind.ResourcePack,
                    includeIcon);
                if (!inspection.HasPackMcmeta)
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
    /// Имена включённых паков из строки <c>resourcePacks:[...]</c>.
    /// Префикс <c>file/</c> отбрасывается; служебные имена (vanilla и т.п.)
    /// возвращаются как есть. Повреждённый JSON даёт пустой список.
    /// </summary>
    public IReadOnlyList<string> GetEnabled(InstanceProfile instance)
    {
        var optionsPath = GetOptionsPath(instance);
        if (!File.Exists(optionsPath))
        {
            return Array.Empty<string>();
        }

        var options = OptionsFile.Load(optionsPath);
        var value = options.GetValue(ResourcePacksKey);
        var parsed = TryParseResourcePacks(value);

        return parsed is null ? Array.Empty<string>() : parsed;
    }

    /// <summary>
    /// Включает или выключает пак в options.txt. Меняется только строка
    /// resourcePacks; остальные строки, порядок, кодировка и переводы строк
    /// сохраняются. Перед заменой создаётся timestamp-бэкап options.txt.
    /// </summary>
    public void SetEnabled(InstanceProfile instance, string fileName, bool enabled)
    {
        var name = PackStorage.RequireFileName(fileName);
        var optionsPath = GetOptionsPath(instance);
        var options = OptionsFile.Load(optionsPath);
        var raw = options.GetValue(ResourcePacksKey);

        List<string> entries;
        if (string.IsNullOrWhiteSpace(raw))
        {
            entries = new List<string>();
        }
        else
        {
            entries = TryParseResourcePacks(raw)
                ?? throw new LauncherException(
                    $"Не удалось разобрать список {ResourcePacksKey} в {OptionsFileName}.");
        }

        var changed = false;

        if (enabled)
        {
            if (!entries.Any(entry => SamePack(entry, name)))
            {
                entries.Add("file/" + name);
                changed = true;
            }
        }
        else
        {
            changed = entries.RemoveAll(entry => SamePack(entry, name)) > 0;
        }

        if (!changed)
        {
            return;
        }

        options.SetValue(ResourcePacksKey, SerializeResourcePacks(entries));
        options.Save();
    }

    /// <summary>
    /// Копирует zip-файлы в resourcepacks. Архив должен содержать pack.mcmeta
    /// в корне. Существующие файлы не перезаписываются. Сами паки не включаются.
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
                $"Не удалось создать папку ресурспаков {folder}: {ex.Message}",
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
                throw new LauncherException($"Файл ресурспака не найден: {source}");
            }

            if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(
                    $"Можно импортировать только zip-файлы ресурспаков: {source}");
            }

            var inspection = PackIconReader.Inspect(source, PackKind.ResourcePack);
            if (!inspection.HasPackMcmeta)
            {
                throw new LauncherException(
                    $"В архиве нет pack.mcmeta в корне: {Path.GetFileName(source)}");
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
                    $"Ресурспак уже существует в папке {FolderName}: {fileName}");
            }

            sources.Add((source, target));
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Pack,
            $"Импорт ресурспаков: 0 из {sources.Count}",
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
            var inspection = PackIconReader.Inspect(target, PackKind.ResourcePack);

            imported.Add(PackStorage.CreateInfo(
                target,
                enabled.Contains(fileName),
                inspection));

            progress?.Report(new LauncherProgress(
                ProgressStage.Pack,
                $"Импорт ресурспаков: {imported.Count} из {sources.Count}",
                imported.Count,
                sources.Count));
        }

        return imported;
    }

    /// <summary>
    /// Удаляет zip или папку-пак. По умолчанию переносит в
    /// backups\removed-resourcepacks\&lt;timestamp&gt;.
    /// Возвращает путь, куда перенесён пак (или исходный путь при удалении).
    /// </summary>
    public string Delete(PackInfo pack, bool moveToBackups = true)
    {
        var path = PackStorage.RequirePackPath(pack);

        if (Directory.Exists(path))
        {
            if (!File.Exists(Path.Combine(path, "pack.mcmeta")))
            {
                throw new LauncherException(
                    $"В папке ресурспака нет pack.mcmeta: {path}");
            }
        }
        else if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Ресурспак должен быть zip-файлом: {path}");
        }

        if (moveToBackups)
        {
            return PackStorage.MoveToBackup(path, BackupsRoot, pack.FileName);
        }

        PackStorage.DeletePermanently(path);
        return path;
    }

    /// <summary>Создаёт (при необходимости) и открывает папку resourcepacks.</summary>
    public void OpenFolder(InstanceProfile instance) =>
        PackStorage.OpenFolder(
            GetFolder(instance),
            "Не удалось открыть папку ресурспаков");

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
            throw new LauncherException($"Недопустимый путь к ресурспаку: {rawPath}", ex);
        }
    }

    private static bool IsZipFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static List<string>? TryParseResourcePacks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<string>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = NormalizeReference(item.GetString() ?? "");
                if (name.Length > 0)
                {
                    result.Add(name);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeResourcePacks(List<string> entries) =>
        JsonSerializer.Serialize(entries, JsonOptions);

    private static bool SamePack(string reference, string fileName) =>
        string.Equals(
            NormalizeReference(reference),
            fileName,
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReference(string value)
    {
        var text = value.Trim().Replace('\\', '/');

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
