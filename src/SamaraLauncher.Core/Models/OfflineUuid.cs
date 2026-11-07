using System.Security.Cryptography;
using System.Text;

namespace SamaraLauncher.Core.Models;

public static class OfflineUuid
{
    /// <summary>
    /// UUID оффлайн-игрока: MD5 от "OfflinePlayer:{name}", версия 3, как в Java.
    /// </summary>
    public static string FromUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Ник не может быть пустым.", nameof(username));
        }

        var bytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        var hash = MD5.HashData(bytes);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Normalize(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return "";
        }

        return uuid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }
}
