namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Поиск Java для инстанса.
/// </summary>
public static class JavaLocator
{
    /// <summary>
    /// Возвращает путь к java.exe. "auto" — runtime инстанса, JAVA_HOME, затем PATH.
    /// </summary>
    public static string Resolve(string gameDir, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && !string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var explicitPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (!File.Exists(explicitPath))
            {
                throw new LauncherException($"Указанная Java не найдена: {explicitPath}");
            }

            return explicitPath;
        }

        var bundled = FindBundledJava(gameDir);
        if (bundled is not null)
        {
            return bundled;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var fromHome = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(fromHome))
            {
                return fromHome;
            }
        }

        // Полагаемся на PATH; Process.Start найдёт java.exe сам.
        return "java";
    }

    /// <summary>
    /// Java, установленная лаунчером: runtime\jre-* (Temurin) и старый runtime
    /// java-runtime-gamma. Возвращает null, если ничего не найдено.
    /// </summary>
    private static string? FindBundledJava(string gameDir)
    {
        string runtimeDir;

        try
        {
            runtimeDir = Path.Combine(Path.GetFullPath(gameDir), "runtime");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!Directory.Exists(runtimeDir))
        {
            return null;
        }

        var candidates = new List<string>();

        try
        {
            var jreDirectories = Directory
                .EnumerateDirectories(runtimeDir, "jre-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => GetTrailingNumber(path))
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var directory in jreDirectories)
            {
                candidates.Add(Path.Combine(directory, "bin", "java.exe"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Часть runtime недоступна — проверим известные пути ниже.
        }

        candidates.Add(Path.Combine(
            runtimeDir,
            "java-runtime-gamma",
            "windows",
            "java-runtime-gamma",
            "bin",
            "java.exe"));
        candidates.Add(Path.Combine(runtimeDir, "java-runtime-gamma", "bin", "java.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // Temurin распаковывается с вложенной папкой jdk-...-jre — ищем java.exe.
        try
        {
            return Directory
                .EnumerateFiles(runtimeDir, "java.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains("gamma", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(GetTrailingNumber)
                .ThenBy(path => path.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int GetTrailingNumber(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        var dash = name.LastIndexOf('-');
        return dash >= 0 && int.TryParse(name[(dash + 1)..], out var number) ? number : 0;
    }
}
