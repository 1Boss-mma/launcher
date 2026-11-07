namespace SamaraLauncher.Core.Models;

/// <summary>
/// Описание собственного Forge-сервера, который хостит лаунчер.
/// </summary>
public sealed class HostedServerProfile
{
    public string Id { get; set; } = "server";

    public string Name { get; set; } = "Сервер";

    /// <summary>Папка сервера (внутри неё лежат libraries, mods, server.properties).</summary>
    public string ServerDir { get; set; } = "";

    /// <summary>Id инстанса, с которым связан сервер (необязательно).</summary>
    public string? InstanceId { get; set; }

    /// <summary>"auto" — искать Java в runtime сервера, tools и системе.</summary>
    public string JavaPath { get; set; } = "auto";

    public int Port { get; set; } = 25565;

    public int MemoryMb { get; set; } = 6144;

    public int MinMemoryMb { get; set; } = 2048;

    public string Motd { get; set; } = "Сервер 1.20.1";

    public int MaxPlayers { get; set; } = 6;

    public bool OnlineMode { get; set; }

    public bool Whitelist { get; set; }

    /// <summary>URL или локальный путь к pack.toml для серверной сборки.</summary>
    public string? PackUrl { get; set; }

    public DateTimeOffset? LastInstalledAt { get; set; }

    public DateTimeOffset? LastStartedAt { get; set; }
}
