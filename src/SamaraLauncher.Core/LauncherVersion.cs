using System.Globalization;
using System.Reflection;
using SamaraLauncher.Core.Channels;

namespace SamaraLauncher.Core;

/// <summary>Версия лаунчера и простое сравнение версий вида 1.2.3 / 1.2.3-beta.1.</summary>
public static class LauncherVersion
{
    /// <summary>
    /// Текущая версия сборки из <see cref="AssemblyInformationalVersionAttribute"/>.
    /// Build-метаданные (<c>+build...</c>) не входят в отображаемую версию.
    /// </summary>
    public static string Current { get; } = ReadCurrent();

    public static bool IsNewer(string candidate, string current)
    {
        if (SemVer.TryParse(candidate, out var candidateVersion) &&
            SemVer.TryParse(current, out var currentVersion))
        {
            return candidateVersion.CompareTo(currentVersion) > 0;
        }

        var a = Parse(candidate);
        var b = Parse(current);

        for (var i = 0; i < Math.Max(a.Numbers.Length, b.Numbers.Length); i++)
        {
            var x = i < a.Numbers.Length ? a.Numbers[i] : 0;
            var y = i < b.Numbers.Length ? b.Numbers[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        if (string.IsNullOrEmpty(a.PreRelease))
        {
            return !string.IsNullOrEmpty(b.PreRelease);
        }

        if (string.IsNullOrEmpty(b.PreRelease))
        {
            return false;
        }

        return VersionText.Compare(a.PreRelease, b.PreRelease) > 0;
    }

    private static string ReadCurrent()
    {
        var assembly = typeof(LauncherVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var value = informational.Trim();
            var metadata = value.IndexOf('+', StringComparison.Ordinal);
            return metadata > 0 ? value[..metadata] : value;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static (int[] Numbers, string PreRelease) Parse(string value)
    {
        var text = (value ?? "").Trim().TrimStart('v', 'V');

        // Build-метаданные по SemVer не влияют на сравнение версий.
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? text[(dash + 1)..] : "";
        var main = dash >= 0 ? text[..dash] : text;

        var numbers = main
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToArray();

        return (numbers, pre);
    }
}
