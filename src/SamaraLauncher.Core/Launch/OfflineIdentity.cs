using System.Security.Cryptography;
using System.Text;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Оффлайн-аутентификация: UUID, который Minecraft вычисляет для offline-mode.
/// </summary>
public static class OfflineIdentity
{
    public static string ComputeUuid(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        // UUID версии 3 (name-based, MD5).
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeUuid(string uuid) => uuid.Replace("-", "").ToLowerInvariant();
}
