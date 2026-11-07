using System.Globalization;

namespace SamaraLauncher.Core.Channels;

/// <summary>
/// Лёгкая реализация SemVer 2.0.0 без NuGet: MAJOR.MINOR.PATCH,
/// необязательные prerelease (<c>-beta.1</c>) и build-метаданные (<c>+build</c>).
/// Build-метаданные не влияют на сравнение версий.
/// </summary>
public readonly struct SemVer : IComparable<SemVer>
{
    private readonly string? _prerelease;
    private readonly string? _buildMetadata;

    private SemVer(int major, int minor, int patch, string prerelease, string buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
        _buildMetadata = buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string Prerelease => _prerelease ?? "";

    public string BuildMetadata => _buildMetadata ?? "";

    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>
    /// Разбирает версию вида <c>1.2.3</c>, <c>1.2.3-beta.1</c>,
    /// <c>1.2.3-beta.1+sha.abc</c>. Ведущий <c>v</c> допускается.
    /// </summary>
    public static bool TryParse(string? value, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text.Length > 1
            && (text[0] == 'v' || text[0] == 'V')
            && text[1] >= '0'
            && text[1] <= '9')
        {
            text = text[1..];
        }

        var buildMetadata = "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            buildMetadata = text[(plus + 1)..];
            text = text[..plus];

            if (!AreIdentifiersValid(buildMetadata, allowLeadingZero: true))
            {
                return false;
            }
        }

        var prerelease = "";
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];

            if (!AreIdentifiersValid(prerelease, allowLeadingZero: false))
            {
                return false;
            }
        }

        var components = text.Split('.');
        if (components.Length != 3
            || !TryParseComponent(components[0], out var major)
            || !TryParseComponent(components[1], out var minor)
            || !TryParseComponent(components[2], out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, prerelease, buildMetadata);
        return true;
    }

    /// <summary>Разбирает версию или выбрасывает <see cref="LauncherException"/>.</summary>
    public static SemVer Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new LauncherException(
                $"Некорректная SemVer-версия: \"{value}\". " +
                "Ожидается MAJOR.MINOR.PATCH[-prerelease][+build].");
        }

        return version;
    }

    /// <summary>
    /// Сравнивает версии по правилам SemVer 2.0.0.
    /// Возвращает отрицательное число, 0 или положительное число.
    /// </summary>
    public int CompareTo(SemVer other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        var left = Prerelease;
        var right = other.Prerelease;

        // Версия без prerelease считается старше любой prerelease той же базы.
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var count = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            var x = leftParts[i];
            var y = rightParts[i];
            var xNumeric = IsNumeric(x);
            var yNumeric = IsNumeric(y);

            if (xNumeric && yNumeric)
            {
                // Ведущие нули запрещены, поэтому длина равна числу разрядов.
                var byLength = x.Length.CompareTo(y.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(x, y);
                if (byDigits != 0)
                {
                    return byDigits;
                }
            }
            else if (xNumeric)
            {
                return -1;
            }
            else if (yNumeric)
            {
                return 1;
            }
            else
            {
                var byText = string.CompareOrdinal(x, y);
                if (byText != 0)
                {
                    return byText;
                }
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    /// <summary>Сравнение версий — псевдоним <see cref="CompareTo"/>.</summary>
    public int Compare(SemVer other) => CompareTo(other);

    public bool IsGreaterThan(SemVer other) => CompareTo(other) > 0;

    /// <summary>Сравнение с поддержкой null: null меньше любой версии.</summary>
    public static int Compare(SemVer? left, SemVer? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return right is null ? 1 : left.Value.CompareTo(right.Value);
    }

    /// <summary>
    /// Сравнивает две строковые версии. Некорректная версия — <see cref="LauncherException"/>.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        if (right is null)
        {
            return 1;
        }

        return Parse(left).CompareTo(Parse(right));
    }

    public override string ToString()
    {
        var result = $"{Major}.{Minor}.{Patch}";

        if (Prerelease.Length > 0)
        {
            result += "-" + Prerelease;
        }

        if (BuildMetadata.Length > 0)
        {
            result += "+" + BuildMetadata;
        }

        return result;
    }

    private static bool TryParseComponent(string text, out int value)
    {
        value = 0;

        // SemVer запрещает ведущие нули у MAJOR/MINOR/PATCH.
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool AreIdentifiersValid(string value, bool allowLeadingZero)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            var numeric = true;

            foreach (var ch in identifier)
            {
                if (ch >= '0' && ch <= '9')
                {
                    continue;
                }

                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '-')
                {
                    numeric = false;
                    continue;
                }

                return false;
            }

            if (numeric
                && !allowLeadingZero
                && identifier.Length > 1
                && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumeric(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return true;
    }
}
