namespace SamaraLauncher.Core.Servers;

/// <summary>Результат Server List Ping. Ошибки сети возвращаются в <see cref="Error"/>.</summary>
public sealed class ServerPingResult
{
    public bool Online { get; set; }

    public string VersionName { get; set; } = "";

    public int Protocol { get; set; }

    public int PlayersOnline { get; set; }

    public int PlayersMax { get; set; }

    /// <summary>MOTD без §-кодов форматирования.</summary>
    public string Motd { get; set; } = "";

    /// <summary>PNG-иконка в base64 без префикса data URI.</summary>
    public string Favicon { get; set; } = "";

    public int LatencyMs { get; set; }

    public string? Error { get; set; }

    public IReadOnlyList<PlayerSample> Players { get; set; } = Array.Empty<PlayerSample>();
}

/// <summary>Игрок из необязательного sample в ответе статуса.</summary>
public sealed class PlayerSample
{
    public string Name { get; set; } = "";

    public string Id { get; set; } = "";
}
