using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Java;

/// <summary>
/// Обнаружение установленных Java и подбор версии для инстанса.
/// </summary>
public sealed class JavaManager
{
    private readonly LauncherPaths _paths;
    private readonly JavaRuntimeInstaller _installer;

    public JavaManager(LauncherPaths? paths = null, JavaRuntimeInstaller? runtimeInstaller = null)
    {
        _paths = paths ?? new LauncherPaths();
        _installer = runtimeInstaller ?? new JavaRuntimeInstaller();
    }

    /// <summary>
    /// Ищет Java в runtime инстансов, JAVA_HOME, PATH и Eclipse Adoptium.
    /// Возвращает уникальные пути, отсортированные по major-версии.
    /// </summary>
    public IReadOnlyList<JavaInfo> Discover()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (!string.Equals(Path.GetFileName(fullPath), "java.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(fullPath) && seen.Add(fullPath))
            {
                candidates.Add(fullPath);
            }
        }

        void AddRuntime(string? runtimeDir)
        {
            if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
            {
                return;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             runtimeDir,
                             "java.exe",
                             SearchOption.AllDirectories))
                {
                    Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Часть runtime может быть недоступна — проверяем остальные.
            }
        }

        // 1. Runtime, который лаунчер положил в инстансы.
        if (Directory.Exists(_paths.InstancesDir))
        {
            try
            {
                foreach (var instanceDir in Directory.EnumerateDirectories(_paths.InstancesDir))
                {
                    AddRuntime(Path.Combine(instanceDir, "runtime"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Каталог инстансов может быть недоступен.
            }
        }

        // 2. Общий runtime лаунчера (на случай будущих общих установок).
        AddRuntime(Path.Combine(_paths.LocalDataDir, "runtime"));

        // 3. JAVA_HOME.
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            Add(Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe"));
        }

        // 4. PATH.
        foreach (var path in JavaLocator.FindOnPath())
        {
            Add(path);
        }

        // 5. Eclipse Adoptium: пользовательская и системная установки.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddRuntime(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));
        }

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                AddRuntime(Path.Combine(programFiles, "Eclipse Adoptium"));
            }
        }

        var result = new List<JavaInfo>(candidates.Count);
        var seenInfo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var info = JavaLocator.TryProbe(candidate);
            if (info is null || !seenInfo.Add(info.Path))
            {
                continue;
            }

            result.Add(info);
        }

        return result
            .OrderBy(info => info.MajorVersion)
            .ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Подбирает Java для инстанса: явный путь, runtime инстанса,
    /// затем любая найденная Java с major &gt;= <paramref name="requiredMajor"/>.
    /// </summary>
    public JavaInfo? FindFor(InstanceProfile instance, int requiredMajor = 17)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!string.IsNullOrWhiteSpace(instance.JavaPath) &&
            !string.Equals(instance.JavaPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            var explicitInfo = JavaLocator.TryProbe(instance.JavaPath);
            if (explicitInfo is not null && IsCompatible(explicitInfo, requiredMajor))
            {
                return explicitInfo;
            }
        }

        if (!string.IsNullOrWhiteSpace(instance.GameDir))
        {
            string gameDir;
            try
            {
                gameDir = Path.GetFullPath(instance.GameDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                gameDir = "";
            }

            if (!string.IsNullOrEmpty(gameDir))
            {
                var fromRuntime = DiscoverRuntime(Path.Combine(gameDir, "runtime"))
                    .Where(info => IsCompatible(info, requiredMajor))
                    .OrderBy(info => info.MajorVersion)
                    .ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (fromRuntime is not null)
                {
                    return fromRuntime;
                }
            }
        }

        return Discover()
            .Where(info => IsCompatible(info, requiredMajor))
            .OrderBy(info => info.MajorVersion)
            .ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Скачивает Temurin нужной версии в runtime инстанса, если её ещё нет.</summary>
    public Task<JavaInfo> EnsureAsync(
        InstanceProfile instance,
        int major,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException(
                "У инстанса не указана папка игры — невозможно установить Java.");
        }

        return _installer.EnsureInstalledAsync(instance.GameDir, major, progress, cancellationToken);
    }

    public bool IsCompatible(JavaInfo java, int requiredMajor) =>
        java is not null && java.MajorVersion >= requiredMajor;

    private static IEnumerable<JavaInfo> DiscoverRuntime(string runtimeDir)
    {
        if (!Directory.Exists(runtimeDir))
        {
            return Array.Empty<JavaInfo>();
        }

        var result = new List<JavaInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         runtimeDir,
                         "java.exe",
                         SearchOption.AllDirectories))
            {
                var info = JavaLocator.TryProbe(path);
                if (info is not null && seen.Add(info.Path))
                {
                    result.Add(info);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Недоступные файлы пропускаем.
        }

        return result;
    }
}
