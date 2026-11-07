namespace SamaraLauncher.Core.Models;

/// <summary>
/// Корневой файл настроек лаунчера (%APPDATA%\SamaraLauncher\config.json).
/// </summary>
public sealed class LauncherConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<InstanceProfile> Instances { get; set; } = new();

    public string? LastInstanceId { get; set; }

    public bool CheckLauncherUpdates { get; set; } = true;

    /// <summary>Сколько последних бэкапов mods/config хранить.</summary>
    public int BackupRetention { get; set; } = 5;

    /// <summary>Адрес сервера для виджета состояния (необязательно).</summary>
    public string? ServerAddress { get; set; }

    /// <summary>Тема оформления: auto, day или night.</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>Показывать частицы на панораме.</summary>
    public bool ParticlesEnabled { get; set; } = true;

    public string? HomeCoverPath { get; set; }

    /// <summary>Закрывать лаунчер после успешного запуска игры.</summary>
    public bool CloseOnLaunch { get; set; }

    /// <summary>Собственные серверы, которые хостит лаунчер.</summary>
    public List<HostedServerProfile> HostedServers { get; set; } = new();

    public string? LastHostedServerId { get; set; }

    /// <summary>Сколько последних бэкапов каждого серверного мира хранить.</summary>
    public int WorldBackupRetention { get; set; } = 10;

    public InstanceProfile? Find(string id) =>
        Instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public HostedServerProfile? FindHostedServer(string id) =>
        (HostedServers ?? new List<HostedServerProfile>())
            .FirstOrDefault(server => string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool Remove(string id)
    {
        var existing = Find(id);
        return existing is not null && Instances.Remove(existing);
    }
}
