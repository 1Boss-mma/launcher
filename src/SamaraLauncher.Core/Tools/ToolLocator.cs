namespace SamaraLauncher.Core.Tools;

/// <summary>
/// Поиск внешних инструментов лаунчера (packwiz-installer-bootstrap.jar).
/// </summary>
public sealed class ToolLocator
{
    public const string BootstrapFileName = "packwiz-installer-bootstrap.jar";

    private const int MaxParentLevels = 12;

    public ToolLocator(string? baseDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(baseDirectory);
    }

    public string BaseDirectory { get; }

    /// <summary>Ищет bootstrap, возвращает null, если файл не найден.</summary>
    public string? TryFindPackwizBootstrap()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            foreach (var candidate in Candidates(root))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            var toolsDir = Path.Combine(root, "tools");
            if (Directory.Exists(toolsDir))
            {
                var versioned = Directory
                    .EnumerateFiles(toolsDir, "packwiz-installer-bootstrap*.jar")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (versioned is not null)
                {
                    return Path.GetFullPath(versioned);
                }
            }
        }

        return null;
    }

    /// <summary>Ищет bootstrap и бросает понятную ошибку, если его нет.</summary>
    public string FindPackwizBootstrap() =>
        TryFindPackwizBootstrap()
        ?? throw new LauncherException(
            $"Не найден {BootstrapFileName}. Положите его в папку tools рядом с лаунчером " +
            $"(искали в {BaseDirectory} и выше по дереву каталогов).");

    private static IEnumerable<string> Candidates(string root)
    {
        yield return Path.Combine(root, "tools", BootstrapFileName);
        yield return Path.Combine(root, BootstrapFileName);
    }

    private IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in WalkUp(BaseDirectory))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        foreach (var directory in WalkUp(Path.GetFullPath(Environment.CurrentDirectory)))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> WalkUp(string startDirectory)
    {
        var directory = startDirectory;

        for (var level = 0; level < MaxParentLevels && !string.IsNullOrEmpty(directory); level++)
        {
            yield return directory;

            var parent = Path.GetDirectoryName(directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            directory = parent;
        }
    }
}
