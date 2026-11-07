using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Instances;

/// <summary>
/// Чтение и запись config.json со списком инстансов.
/// </summary>
public sealed class InstanceStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public string ConfigPath { get; }

    public InstanceStore(string? configPath = null)
    {
        ConfigPath = configPath ?? DefaultConfigPath;
    }

    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SamaraLauncher",
            "config.json");

    public LauncherConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new LauncherConfig();
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? new LauncherConfig();
            Normalize(config);
            return config;
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Не удалось прочитать {ConfigPath}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл занят, нет прав или диск недоступен. Возвращать новый конфиг нельзя:
            // иначе следующий Save затрёт реальные инстансы.
            throw new LauncherException(
                $"Не удалось открыть {ConfigPath}: {ex.Message}. " +
                "Закройте лаунчер/антивирус и повторите попытку; файл настроек не изменён.",
                ex);
        }
    }

    /// <summary>
    /// Загружает конфиг, а при повреждённом JSON переименовывает битый файл
    /// в <c>config.json.bad-&lt;timestamp&gt;</c> и возвращает новый конфиг.
    /// </summary>
    public LauncherConfig LoadOrDefault()
    {
        try
        {
            return Load();
        }
        catch (LauncherException ex) when (ex.InnerException is JsonException)
        {
            MoveCorruptedConfig();
            return new LauncherConfig();
        }
    }

    /// <summary>
    /// Атомарно сохраняет конфиг: пишет во временный файл рядом и заменяет им config.json.
    /// При ошибке старый файл остаётся нетронутым.
    /// </summary>
    public void Save(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку настроек {dir}: {ex.Message}", ex);
            }
        }

        var tempPath = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tempPath, json);
            CreateBackupCopy();
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить настройки в {ConfigPath}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Копия предыдущего config.json перед успешной заменой. Не критична: если
    /// скопировать не удалось, сохранение всё равно продолжится.
    /// </summary>
    private void CreateBackupCopy()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        try
        {
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }
        catch (IOException)
        {
            // Бэкап не должен мешать сохранению.
        }
        catch (UnauthorizedAccessException)
        {
            // То же самое.
        }
    }

    private void MoveCorruptedConfig()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = ConfigPath + ".bad-" + stamp;

        for (var index = 2; File.Exists(target); index++)
        {
            target = ConfigPath + $".bad-{stamp}-{index}";
        }

        try
        {
            File.Move(ConfigPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Файл настроек {ConfigPath} повреждён, но переименовать его не удалось: {ex.Message}. " +
                "Переместите или удалите файл вручную и запустите лаунчер снова.", ex);
        }
    }

    private static void Normalize(LauncherConfig config)
    {
        config.Instances = (config.Instances ?? new List<InstanceProfile>())
            .Where(instance => instance is not null)
            .ToList();

        foreach (var instance in config.Instances)
        {
            instance.JvmArgs ??= new List<string>();
            instance.IconColor = string.IsNullOrWhiteSpace(instance.IconColor)
                ? "#5FBE4A"
                : instance.IconColor;
            instance.Notes ??= "";
            instance.Loader ??= "";
            instance.FabricVersion ??= "";
            instance.OptiFineVersion ??= "";
        }

        // Старые config.json не содержат этого поля — список должен остаться не null.
        config.HostedServers = (config.HostedServers ?? new List<HostedServerProfile>())
            .Where(server => server is not null)
            .ToList();

        if (config.WorldBackupRetention <= 0)
        {
            config.WorldBackupRetention = 10;
        }

        config.WorldBackupRetention = Math.Clamp(config.WorldBackupRetention, 1, 1000);

        foreach (var server in config.HostedServers)
        {
            server.ServerDir ??= "";
            server.JavaPath = string.IsNullOrWhiteSpace(server.JavaPath) ? "auto" : server.JavaPath;
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? "Сервер" : server.Name;
            server.Motd ??= "";
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
            // Временный файл не критичен.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }
}
