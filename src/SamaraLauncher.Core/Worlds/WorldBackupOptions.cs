using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Worlds;

/// <summary>
/// Настройки автобэкапов миров. Хранятся в backups\worlds\backup-settings.json.
/// </summary>
public sealed class WorldBackupOptions
{
    public const int DefaultIntervalHours = 24;
    public const int DefaultRetention = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public bool Enabled { get; set; }

    /// <summary>Интервал между автобэкапами в часах (минимум 1).</summary>
    public int IntervalHours { get; set; } = DefaultIntervalHours;

    /// <summary>Сколько последних бэкапов хранить (минимум 1).</summary>
    public int Retention { get; set; } = DefaultRetention;

    /// <summary>null — backups\worlds рядом с данными лаунчера.</summary>
    public string? DestinationDirectory { get; set; }

    /// <summary>Время последнего успешного автобэкапа (UTC).</summary>
    public DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>Путь к backup-settings.json для стандартных каталогов лаунчера.</summary>
    public static string GetSettingsPath(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return GetSettingsPath(WorldService.GetWorldBackupsDirectory(paths));
    }

    /// <summary>Путь к backup-settings.json внутри папки автобэкапов миров.</summary>
    public static string GetSettingsPath(string worldBackupsRoot)
    {
        if (string.IsNullOrWhiteSpace(worldBackupsRoot))
        {
            throw new LauncherException("Не указана папка автобэкапов миров.");
        }

        return Path.Combine(worldBackupsRoot, "backup-settings.json");
    }

    /// <summary>Загружает настройки; при отсутствии файла возвращает значения по умолчанию.</summary>
    public static WorldBackupOptions Load(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Load(GetSettingsPath(paths));
    }

    /// <summary>Загружает настройки; при отсутствии файла возвращает значения по умолчанию.</summary>
    public static WorldBackupOptions Load(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new LauncherException("Не указан путь к настройкам автобэкапов.");
        }

        if (!File.Exists(settingsPath))
        {
            return new WorldBackupOptions();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var options = JsonSerializer.Deserialize<WorldBackupOptions>(json, JsonOptions)
                ?? new WorldBackupOptions();
            options.Normalize();
            return options;
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать настройки автобэкапов {settingsPath}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось прочитать настройки автобэкапов {settingsPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Как <see cref="Load(LauncherPaths)"/>, но битый файл не мешает запуску.</summary>
    public static WorldBackupOptions LoadOrDefault(LauncherPaths paths)
    {
        try
        {
            return Load(paths);
        }
        catch (LauncherException)
        {
            return new WorldBackupOptions();
        }
    }

    /// <summary>Атомарно сохраняет настройки: временный файл + замена.</summary>
    public void Save(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Save(GetSettingsPath(paths));
    }

    /// <summary>Атомарно сохраняет настройки: временный файл + замена.</summary>
    public void Save(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new LauncherException("Не указан путь к настройкам автобэкапов.");
        }

        Normalize();

        var directory = Path.GetDirectoryName(settingsPath);
        var tempPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить настройки автобэкапов в {settingsPath}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal void Normalize()
    {
        IntervalHours = Math.Clamp(IntervalHours, 1, 24 * 365);
        Retention = Math.Clamp(Retention, 1, 1000);

        if (string.IsNullOrWhiteSpace(DestinationDirectory))
        {
            DestinationDirectory = null;
        }
        else
        {
            DestinationDirectory = DestinationDirectory.Trim();
        }

        if (LastRunUtc is { } lastRun && lastRun.Year < 1980)
        {
            LastRunUtc = null;
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
}
