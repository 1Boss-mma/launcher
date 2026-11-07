using System.Globalization;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Подбор major-версии Java для версии Minecraft. Если version json содержит
/// <c>javaVersion.majorVersion</c>, используется он; иначе — таблица ниже.
/// </summary>
public static class MinecraftJavaVersion
{
    /// <summary>Нужная major-версия Java: из version json или по версии Minecraft.</summary>
    public static int GetRequiredMajor(string? versionId, int? declaredMajor = null)
    {
        var major = declaredMajor is > 0 ? declaredMajor.Value : GetFallbackMajor(versionId);
        return NormalizeMajor(major);
    }

    /// <summary>
    /// Приводит major-версию Java к поддерживаемым лаунчером LTS (8, 17, 21).
    /// Minecraft 1.17.x объявляет Java 16, но подходящий runtime — Java 17.
    /// Для 18–20 ближайшая поддерживаемая LTS — Java 21.
    /// </summary>
    public static int NormalizeMajor(int major) => major switch
    {
        <= 8 => 8,
        <= 17 => 17,
        <= 20 => 21,
        _ => 21
    };

    /// <summary>
    /// Таблица совместимости: &lt;=1.16.5 — Java 8, 1.17–1.20.4 — Java 17,
    /// &gt;=1.20.5 — Java 21. Для снапшотов и нестандартных id — консервативная оценка.
    /// </summary>
    public static int GetFallbackMajor(string? versionId)
    {
        var id = MinecraftVersionRules.StripForgeSuffix(versionId).Trim();
        if (id.Length == 0)
        {
            return 17;
        }

        // Alpha/beta/classic: a1.2.6, b1.7.3, c0.30_01c.
        if ((id[0] is 'a' or 'A' or 'b' or 'B' or 'c' or 'C') &&
            id.Length > 1 &&
            char.IsDigit(id[1]))
        {
            return 8;
        }

        var snapshotMajor = ParseSnapshotMajor(id);
        if (snapshotMajor is not null)
        {
            return snapshotMajor.Value;
        }

        var normalized = id.TrimStart('v', 'V');
        var parts = normalized.Split('.');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            if (major > 1)
            {
                return 21;
            }

            if (major == 1 &&
                int.TryParse(
                    LeadingDigits(parts[1]),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var minor))
            {
                if (minor <= 16)
                {
                    return 8;
                }

                if (minor <= 19)
                {
                    return 17;
                }

                if (minor > 20)
                {
                    return 21;
                }

                var patch = parts.Length >= 3
                    ? ParseLeadingInt(parts[2])
                    : 0;

                return patch <= 4 ? 17 : 21;
            }
        }

        return 17;
    }

    private static int? ParseSnapshotMajor(string id)
    {
        // Снапшоты: 24w14a, 21w03a. Точная версия есть в javaVersion у Mojang,
        // поэтому это только резервная оценка.
        var w = id.IndexOf('w');
        if (w < 2 || w > 4 ||
            !int.TryParse(id[..w], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        if (year >= 24)
        {
            return 21;
        }

        return year >= 20 ? 17 : 8;
    }

    private static int ParseLeadingInt(string value)
    {
        var digits = LeadingDigits(value);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static string LeadingDigits(string value)
    {
        var length = 0;
        while (length < value.Length && char.IsDigit(value[length]))
        {
            length++;
        }

        return length == 0 ? "" : value[..length];
    }
}
