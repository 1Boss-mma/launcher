namespace SamaraLauncher.Core.Models;

/// <summary>
/// Каталоги лаунчера. Настройки — в %APPDATA%, тяжёлые данные — в %LOCALAPPDATA%.
/// </summary>
public sealed class LauncherPaths
{
    public LauncherPaths(string? appDataDir = null, string? localDataDir = null)
    {
        AppDataDir = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SamaraLauncher");

        LocalDataDir = localDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamaraLauncher");
    }

    public string AppDataDir { get; }

    public string LocalDataDir { get; }

    public string ConfigPath => Path.Combine(AppDataDir, "config.json");

    public string InstancesDir => Path.Combine(LocalDataDir, "instances");

    public string CacheDir => Path.Combine(LocalDataDir, "cache");

    public string BackupsDir => Path.Combine(LocalDataDir, "backups");

    public string LogsDir => Path.Combine(LocalDataDir, "logs");

    public string UpdatesDir => Path.Combine(LocalDataDir, "updates");

    /// <summary>Папка с tools\packwiz-installer-bootstrap.jar рядом с exe.</summary>
    public static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    public static string DefaultInstanceGameDir(string instanceId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamaraLauncher",
            "instances",
            instanceId);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LocalDataDir);
        Directory.CreateDirectory(InstancesDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(UpdatesDir);
    }
}
