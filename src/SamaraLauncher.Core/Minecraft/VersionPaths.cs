namespace SamaraLauncher.Core.Minecraft;

/// <summary>Пути внутри игрового инстанса.</summary>
public sealed class VersionPaths
{
    public VersionPaths(string gameDir)
    {
        GameDir = Path.GetFullPath(gameDir);
    }

    public string GameDir { get; }

    public string LibrariesDir => Path.Combine(GameDir, "libraries");

    public string AssetsDir => Path.Combine(GameDir, "assets");

    public string AssetsIndexesDir => Path.Combine(AssetsDir, "indexes");

    public string AssetsObjectsDir => Path.Combine(AssetsDir, "objects");

    public string VersionsDir => Path.Combine(GameDir, "versions");

    public string ModsDir => Path.Combine(GameDir, "mods");

    public string ConfigDir => Path.Combine(GameDir, "config");

    public string CrashReportsDir => Path.Combine(GameDir, "crash-reports");

    public string LogsDir => Path.Combine(GameDir, "logs");

    public string RuntimeDir => Path.Combine(GameDir, "runtime");

    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);

    public string VersionJson(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");

    public string VersionJar(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");

    public string NativesDir(string versionId) => Path.Combine(VersionDir(versionId), "natives");

    public static string LibraryPath(string librariesDir, DownloadArtifact artifact)
    {
        if (artifact is null)
        {
            throw new LauncherException("В version json отсутствует описание библиотеки.");
        }

        return ResolveLibraryPath(librariesDir, artifact.Path);
    }

    /// <summary>
    /// Проверяет относительный путь артефакта из version json и не выпускает его
    /// за пределы librariesDir (zip-slip / абсолютный путь / UNC).
    /// </summary>
    public static string ResolveLibraryPath(string librariesDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(librariesDir) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new LauncherException("В version json указан пустой путь библиотеки.");
        }

        var root = Path.GetFullPath(librariesDir);
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();

        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new LauncherException(
                $"В version json указан недопустимый путь библиотеки: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        var rootWithSeparator = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Путь библиотеки выходит за пределы {librariesDir}: {relativePath}");
        }

        return fullPath;
    }

    /// <summary>
    /// Проверяет относительный путь из version json и не выпускает его за пределы
    /// папки игры (client jar и другие файлы).
    /// </summary>
    public static string ResolveGamePath(string gameDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new LauncherException("В version json указан пустой путь файла.");
        }

        var root = Path.GetFullPath(gameDir);
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();

        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new LauncherException(
                $"В version json указан недопустимый путь файла: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        var rootWithSeparator = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Путь файла выходит за пределы {gameDir}: {relativePath}");
        }

        return fullPath;
    }

    public static string GuessLibraryPath(string librariesDir, VersionLibrary library, string? classifier = null)
    {
        if (library.Artifact is not null && string.IsNullOrEmpty(classifier))
        {
            return LibraryPath(librariesDir, library.Artifact);
        }

        if (classifier is not null && library.Classifiers.TryGetValue(classifier, out var artifact))
        {
            return LibraryPath(librariesDir, artifact);
        }

        var (group, artifactName, version, nameClassifier) = library.ParsedName;
        var actualClassifier = classifier ?? nameClassifier;
        var file = actualClassifier is null
            ? $"{artifactName}-{version}.jar"
            : $"{artifactName}-{version}-{actualClassifier}.jar";

        var relative = Path.Combine(
            group.Replace('.', Path.DirectorySeparatorChar),
            artifactName,
            version,
            file);

        return ResolveLibraryPath(librariesDir, relative);
    }

    /// <summary>Ищет установленную версию Forge, например 1.20.1-forge-47.4.28.</summary>
    public static string? DetectForgeVersionId(string gameDir)
    {
        var dir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var candidates = Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.Contains("-forge-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name!, Comparer<string>.Create(VersionText.Compare))
            .ToList();

        return candidates.FirstOrDefault();
    }
}
