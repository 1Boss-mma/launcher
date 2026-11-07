using System.Globalization;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Servers;

/// <summary>Сервер из списка инстанса (аналог ServerData в Minecraft).</summary>
public sealed class ServerEntry
{
    public const int DefaultPort = 25565;

    public string Name { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; } = DefaultPort;

    public bool IsFavorite { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>PNG-иконка в base64 без префикса data URI.</summary>
    public string? IconBase64 { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Адрес для отображения: host или host:port.</summary>
    [JsonIgnore]
    public string Address => Port == DefaultPort ? Host : Host + ":" + Port;
}

/// <summary>Разбор и форматирование адресов host[:port], включая IPv6 в скобках.</summary>
internal static class ServerAddress
{
    public static bool TryParse(
        string? value,
        int defaultPort,
        out string host,
        out int port)
    {
        host = "";
        port = defaultPort;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text.StartsWith('['))
        {
            var closing = text.IndexOf(']');
            if (closing <= 1)
            {
                return false;
            }

            host = text[1..closing].Trim();
            var rest = text[(closing + 1)..].Trim();
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryParsePort(rest[1..], out port))
                {
                    return false;
                }
            }
        }
        else
        {
            var firstColon = text.IndexOf(':');
            if (firstColon >= 0 && text.IndexOf(':', firstColon + 1) >= 0)
            {
                // IPv6-литерал без скобок: порт берём по умолчанию.
                host = text;
            }
            else if (firstColon >= 0)
            {
                host = text[..firstColon].Trim();
                if (!TryParsePort(text[(firstColon + 1)..], out port))
                {
                    return false;
                }
            }
            else
            {
                host = text;
            }
        }

        return host.Length > 0 && port is >= 1 and <= 65535;
    }

    public static string Format(string host, int port)
    {
        var normalizedHost = host.Trim();

        if (normalizedHost.Contains(':') && !normalizedHost.StartsWith('['))
        {
            normalizedHost = "[" + normalizedHost + "]";
        }

        return port == ServerEntry.DefaultPort
            ? normalizedHost
            : normalizedHost + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;
}
