using System.Globalization;

namespace SamaraLauncher.Core.ServerHost;

/// <summary>
/// Результат проверки папки сервера перед установкой/запуском.
/// </summary>
public sealed record ServerInspection(
    bool DirectoryExists,
    bool ForgeInstalled,
    bool EulaAccepted,
    int ModCount,
    IReadOnlyList<string> Worlds,
    bool PortInUse,
    string? InstalledForgeVersion,
    string LevelName,
    string? Error);

/// <summary>
/// Адреса, по которым друзья могут подключиться к локальному серверу.
/// </summary>
public sealed record ServerAddressInfo(
    int Port,
    IReadOnlyList<string> LocalAddresses,
    string? PublicAddress)
{
    /// <summary>Первый локальный IPv4-адрес (обычно адрес в домашней сети).</summary>
    public string? PrimaryLocal => LocalAddresses?.FirstOrDefault();

    /// <summary>Публичный адрес или null, если его не удалось определить.</summary>
    public string? PrimaryPublic =>
        string.IsNullOrWhiteSpace(PublicAddress) ? null : PublicAddress;

    /// <summary>Форматирует адрес как host:port, IPv6 — в квадратных скобках.</summary>
    public static string Format(string host, int port)
    {
        var normalized = host.Trim();

        if (!(normalized.StartsWith('[') && normalized.EndsWith(']')) && normalized.Contains(':'))
        {
            normalized = "[" + normalized + "]";
        }

        return normalized + ":" + port.ToString(CultureInfo.InvariantCulture);
    }
}
