using System.ComponentModel;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Java;

/// <summary>
/// Ищет Java для инстанса: явный путь, runtime инстанса, JAVA_HOME, PATH,
/// Eclipse Adoptium. По умолчанию требуется Java 17+; для старых версий
/// Minecraft можно запросить точную major (например, Java 8).
/// </summary>
public sealed class JavaLocator : IJavaLocator
{
    private static readonly Regex VersionPattern = new(
        "version\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyVersionPattern = new(
        "(\\d+(?:\\.\\d+)*(?:_\\d+)?(?:[-+][0-9A-Za-z._-]+)?)",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public JavaInfo Find(InstanceProfile instance) => Find(instance, 17, allowNewer: true);

    /// <inheritdoc />
    public JavaInfo Find(InstanceProfile instance, int requiredMajor, bool allowNewer = true)
    {
        if (requiredMajor < 1)
        {
            throw new LauncherException("Не указана требуемая версия Java.");
        }

        if (!string.IsNullOrWhiteSpace(instance.JavaPath) &&
            !string.Equals(instance.JavaPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return FindExplicit(instance.JavaPath, requiredMajor, allowNewer);
        }

        return FindAutomatic(instance.GameDir, requiredMajor, allowNewer);
    }

    /// <summary>Проверяет конкретный java.exe и возвращает версию. Бросает LauncherException.</summary>
    public static JavaInfo Probe(string javaPath)
    {
        var fullPath = NormalizePath(javaPath);

        if (!File.Exists(fullPath))
        {
            throw new LauncherException($"Java не найдена: {fullPath}");
        }

        var (version, major) = ReadVersion(fullPath);
        return new JavaInfo(fullPath, version, major);
    }

    /// <summary>Мягкий вариант Probe: null, если файл не запускается или версию не удалось разобрать.</summary>
    public static JavaInfo? TryProbe(string javaPath)
    {
        try
        {
            return Probe(javaPath);
        }
        catch (LauncherException)
        {
            return null;
        }
    }

    private static JavaInfo FindExplicit(string configuredPath, int requiredMajor, bool allowNewer)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        var fullPath = NormalizePath(expanded);

        if (!File.Exists(fullPath))
        {
            throw new LauncherException(
                $"Указанная Java не найдена: {fullPath}. " +
                "Исправьте путь в настройках инстанса или выберите \"auto\".");
        }

        var info = Probe(fullPath);
        if (!info.IsCompatible(requiredMajor, allowNewer))
        {
            var expected = allowNewer
                ? $"Java {requiredMajor} или новее"
                : $"Java {requiredMajor}";

            throw new LauncherException(
                $"Указана Java {info.Version}, но нужна {expected}. " +
                $"Путь: {info.Path}");
        }

        return info;
    }

    private static JavaInfo FindAutomatic(
        string gameDir,
        int requiredMajor,
        bool allowNewer)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры — невозможно найти Java в runtime инстанса.");
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (File.Exists(full) && seen.Add(full))
            {
                candidates.Add(full);
            }
        }

        void AddRuntime(string runtimeDir)
        {
            if (!Directory.Exists(runtimeDir))
            {
                return;
            }

            var found = new List<string>();
            try
            {
                found.AddRange(Directory.EnumerateFiles(runtimeDir, "java.exe", SearchOption.AllDirectories));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Часть runtime может быть недоступна — проверяем то, что нашли.
            }

            foreach (var path in found
                         .OrderByDescending(p => p.Contains("gamma", StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(p => p.Contains("17", StringComparison.OrdinalIgnoreCase))
                         .ThenBy(p => p.Length))
            {
                Add(path);
            }
        }

        // 1. Runtime, который лаунчер/Forge уже положили в инстанс.
        AddRuntime(Path.Combine(Path.GetFullPath(gameDir), "runtime"));

        // 2. JAVA_HOME.
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            Add(Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe"));
        }

        // 3. PATH — через where.exe, с запасным перебором каталогов PATH.
        foreach (var path in FindOnPath())
        {
            Add(path);
        }

        // 4. Eclipse Adoptium, установленная в профиль пользователя.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddRuntime(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));
        }

        var compatible = new List<JavaInfo>();
        var incompatible = new List<JavaInfo>();

        foreach (var candidate in candidates)
        {
            var info = TryProbe(candidate);
            if (info is null)
            {
                continue;
            }

            if (info.IsCompatible(requiredMajor, allowNewer))
            {
                compatible.Add(info);
            }
            else
            {
                incompatible.Add(info);
            }
        }

        if (compatible.Count > 0)
        {
            // Сначала точное совпадение major, затем ближайшая более новая Java.
            return compatible
                .OrderBy(info => info.MajorVersion == requiredMajor ? 0 : 1)
                .ThenBy(info => info.MajorVersion)
                .ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        if (incompatible.Count > 0)
        {
            var versions = string.Join(", ", incompatible
                .Select(info => $"{info.Version} ({info.Path})")
                .Distinct(StringComparer.OrdinalIgnoreCase));

            if (allowNewer)
            {
                throw new LauncherException(
                    $"Найдена только Java старее {requiredMajor}: {versions}. " +
                    $"Нужна Java {requiredMajor} или новее.");
            }

            throw new LauncherException(
                $"Найдена Java {versions}, но для этой версии Minecraft нужна Java {requiredMajor}.");
        }

        throw new LauncherException(
            $"Java {requiredMajor} не найдена. Установите Java {requiredMajor} " +
            $"(например, Eclipse Temurin {requiredMajor}) или укажите путь к java.exe " +
            "в настройках инстанса.");
    }

    internal static IEnumerable<string> FindOnPath()
    {
        var results = new List<string>();

        try
        {
            var result = ProcessHost
                .RunAsync("where.exe", new[] { "java.exe" }, timeout: TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();

            if (!result.TimedOut)
            {
                foreach (var line in result.Output.Split(
                             new[] { '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (File.Exists(line))
                    {
                        results.Add(line);
                    }
                }
            }
        }
        catch (LauncherException)
        {
            // where.exe недоступен — ниже переберём PATH вручную.
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), "java.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (Directory.Exists(expanded))
            {
                expanded = Path.Combine(expanded, "bin", "java.exe");
            }

            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь к Java: {path}");
        }
    }

    private static (string Version, int Major) ReadVersion(string javaPath)
    {
        var directory = Path.GetDirectoryName(javaPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            directory = null;
        }

        ProcessRunResult result;

        try
        {
            result = ProcessHost
                .RunAsync(
                    javaPath,
                    new[] { "-version" },
                    workingDirectory: directory,
                    timeout: TimeSpan.FromSeconds(15))
                .GetAwaiter()
                .GetResult();
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new LauncherException(
                $"Не удалось запустить Java {javaPath}: {ex.Message}. " +
                "Проверьте, что это рабочая java.exe версии 17+.", ex);
        }

        if (result.TimedOut)
        {
            throw new LauncherException(
                $"Java не ответила за 15 секунд: {javaPath}. Файл повреждён или завис.");
        }

        var output = result.Output;

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"Не удалось проверить Java {javaPath}: процесс завершился с кодом {result.ExitCode}. " +
                Shorten(output));
        }

        var match = VersionPattern.Match(output);
        if (!match.Success)
        {
            match = AnyVersionPattern.Match(output);
            if (!match.Success)
            {
                throw new LauncherException(
                    $"Не удалось определить версию Java: {javaPath}. Ответ: {Shorten(output)}");
            }
        }

        var version = match.Groups[1].Value;
        var major = ParseMajor(version);

        return (version, major);
    }

    private static int ParseMajor(string version)
    {
        var firstToken = version.Split(
            new[] { '.', '_', '-', '+' },
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (!int.TryParse(firstToken, out var major))
        {
            throw new LauncherException($"Непонятная версия Java: \"{version}\".");
        }

        // Старый формат: 1.8.0_402 — major во второй части.
        if (major == 1)
        {
            var parts = version.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor))
            {
                return legacyMajor;
            }
        }

        return major;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[^500..];
    }
}
