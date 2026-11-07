using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Integrity;

/// <summary>Запись о защищённом локальном файле.</summary>
public sealed class ProtectedFileInfo
{
    public string Sha256 { get; set; } = "";

    public long Size { get; set; }

    public DateTimeOffset ProtectedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Reason { get; set; } = "local patch";
}

/// <summary>
/// Манифест локальных патчей <c>&lt;gameDir&gt;\overrides.json</c>.
/// Ключи — относительные пути с прямыми слэшами.
/// </summary>
public sealed class ProtectedFilesManifest
{
    public const string FileName = "overrides.json";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private Dictionary<string, ProtectedFileInfo> _files =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _saveLock = new();

    public ProtectedFilesManifest(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для манифеста overrides.json.");
        }

        GameDir = Path.GetFullPath(gameDir);
        FilePath = Path.Combine(GameDir, FileName);
    }

    public string GameDir { get; }

    public string FilePath { get; }

    public int Version { get; private set; } = CurrentVersion;

    public IReadOnlyDictionary<string, ProtectedFileInfo> Files => _files;

    /// <summary>Читает overrides.json; если файла нет — возвращает пустой манифест.</summary>
    public static ProtectedFilesManifest Load(string gameDir)
    {
        var manifest = new ProtectedFilesManifest(gameDir);
        manifest.Reload();
        return manifest;
    }

    public void Reload()
    {
        _files = new Dictionary<string, ProtectedFileInfo>(StringComparer.OrdinalIgnoreCase);
        Version = CurrentVersion;

        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
            if (dto is null)
            {
                return;
            }

            Version = dto.Version <= 0 ? CurrentVersion : dto.Version;

            if (dto.Files is null)
            {
                return;
            }

            foreach (var (path, info) in dto.Files)
            {
                if (info is null)
                {
                    continue;
                }

                var normalized = NormalizePath(path);
                info.Sha256 = NormalizeHash(info.Sha256);
                info.Reason = string.IsNullOrWhiteSpace(info.Reason) ? "local patch" : info.Reason;
                _files[normalized] = info;
            }
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать {FilePath}: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать {FilePath}: {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException(
                $"Нет доступа к {FilePath}: {ex.Message}",
                ex);
        }
    }

    /// <summary>Атомарно сохраняет манифест (через уникальный временный файл).</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new ManifestDto
            {
                Version = CurrentVersion,
                Files = new Dictionary<string, ProtectedFileInfo>(_files, StringComparer.OrdinalIgnoreCase)
            };

            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch (IOException ex)
            {
                TryDelete(tempPath);
                throw new LauncherException(
                    $"Не удалось сохранить {FilePath}: {ex.Message}",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(tempPath);
                throw new LauncherException(
                    $"Нет доступа для записи {FilePath}: {ex.Message}",
                    ex);
            }
        }
    }

    public ProtectedFileInfo Add(string relativePath, string sha256, long size, string reason)
    {
        var normalized = NormalizePath(relativePath);
        var normalizedHash = NormalizeHash(sha256);

        if (normalizedHash.Length != 64 || !normalizedHash.All(IsHexChar))
        {
            throw new LauncherException(
                $"Некорректный SHA-256 для {normalized}: ожидалось 64 hex-символа.");
        }

        var info = new ProtectedFileInfo
        {
            Sha256 = normalizedHash,
            Size = size,
            ProtectedAt = DateTimeOffset.UtcNow,
            Reason = string.IsNullOrWhiteSpace(reason) ? "local patch" : reason.Trim()
        };

        _files[normalized] = info;
        return info;
    }

    public bool Remove(string relativePath) => _files.Remove(NormalizePath(relativePath));

    public bool Contains(string relativePath) => _files.ContainsKey(NormalizePath(relativePath));

    public bool TryGet(string relativePath, out ProtectedFileInfo info) =>
        _files.TryGetValue(NormalizePath(relativePath), out info!);

    /// <summary>Приводит путь к виду "mods/foo.jar" и запрещает выход за пределы gameDir.</summary>
    public static string NormalizePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new LauncherException("Путь файла в overrides.json пуст.");
        }

        var path = relativePath.Trim().Replace('\\', '/');

        if (Path.IsPathRooted(path) || path.StartsWith('/'))
        {
            throw new LauncherException(
                $"В overrides.json допускаются только относительные пути: {relativePath}");
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new LauncherException(
                    $"Путь в overrides.json выходит за пределы папки игры: {relativePath}");
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            throw new LauncherException($"Путь файла в overrides.json пуст: {relativePath}");
        }

        return string.Join('/', parts);
    }

    public static string GetFullPath(string gameDir, string relativePath)
    {
        var root = Path.GetFullPath(gameDir);
        var relative = NormalizePath(relativePath);
        var full = Path.GetFullPath(
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        var rootWithSeparator = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Путь выходит за пределы папки игры: {relativePath}");
        }

        return full;
    }

    private static string NormalizeHash(string? hash) =>
        (hash ?? "").Trim().Trim('"').ToLowerInvariant();

    private static bool IsHexChar(char value) =>
        value is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';

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

    private sealed class ManifestDto
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, ProtectedFileInfo>? Files { get; set; } = new();
    }
}
