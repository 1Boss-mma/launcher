namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Результат слияния version json с цепочкой <c>inheritsFrom</c>.
/// </summary>
public sealed class ResolvedVersion
{
    /// <summary>Объединённые данные дочерней версии и всех родителей.</summary>
    public required VersionMetadata Metadata { get; init; }

    /// <summary>Id корневой (ванильной) версии.</summary>
    public required string BaseVersionId { get; init; }

    /// <summary>Id запрошенной дочерней версии.</summary>
    public required string VersionId { get; init; }
}

/// <summary>
/// Загружает version json и рекурсивно сливает его с родителями по <c>inheritsFrom</c>.
/// Используется для Fabric, произвольного Forge и OptiFine, где дочерний json
/// содержит только дополнения к ванильному.
/// </summary>
public sealed class InheritedVersionResolver
{
    /// <summary>Максимальная длина цепочки inheritsFrom.</summary>
    public const int MaxDepth = 10;

    public static ResolvedVersion Load(string gameDir, string versionId)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для чтения version json.");
        }

        var requestedId = NormalizeVersionId(versionId);

        VersionPaths paths;
        try
        {
            paths = new VersionPaths(gameDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь к папке игры: {gameDir}", ex);
        }

        var chain = new List<(string Id, VersionMetadata Metadata)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = requestedId;

        while (true)
        {
            if (!visited.Add(current))
            {
                var sequence = string.Join(
                    " -> ",
                    chain.Select(item => item.Id).Append(current));

                throw new LauncherException(
                    $"Циклическая ссылка inheritsFrom в version json: {sequence}.");
            }

            if (chain.Count >= MaxDepth)
            {
                throw new LauncherException(
                    $"Цепочка inheritsFrom для {requestedId} слишком длинная " +
                    $"(больше {MaxDepth}). Проверьте version json.");
            }

            var versionJson = paths.VersionJson(current);
            if (!File.Exists(versionJson))
            {
                var description = chain.Count == 0
                    ? "версии"
                    : "родительской версии";

                throw new LauncherException(
                    $"Не найден version json {description} {current}: {versionJson}. " +
                    "Установите версию заново.");
            }

            var metadata = VersionMetadata.Load(versionJson);
            chain.Add((current, metadata));

            var parent = metadata.InheritsFrom?.Trim() ?? "";
            if (parent.Length == 0)
            {
                break;
            }

            current = NormalizeVersionId(parent);
        }

        var merged = new VersionMetadata();
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            Merge(merged, chain[index].Metadata);
        }

        merged.Id = requestedId;
        merged.InheritsFrom = null;

        return new ResolvedVersion
        {
            Metadata = merged,
            BaseVersionId = chain[^1].Id,
            VersionId = requestedId
        };
    }

    private static void Merge(VersionMetadata target, VersionMetadata source)
    {
        if (!string.IsNullOrWhiteSpace(source.MainClass))
        {
            target.MainClass = source.MainClass;
        }

        if (!string.IsNullOrWhiteSpace(source.Assets))
        {
            target.Assets = source.Assets;
        }

        if (!string.IsNullOrWhiteSpace(source.AssetIndexId))
        {
            target.AssetIndexId = source.AssetIndexId;
        }

        if (!string.IsNullOrWhiteSpace(source.Type))
        {
            target.Type = source.Type;
        }

        if (!string.IsNullOrWhiteSpace(source.ClientJarPath))
        {
            target.ClientJarPath = source.ClientJarPath;
        }

        if (source.JavaMajorVersion is > 0)
        {
            target.JavaMajorVersion = source.JavaMajorVersion;
        }

        target.Libraries = MergeLibraries(target.Libraries, source.Libraries);

        // Родительские аргументы идут первыми, дочерние дополняют их.
        target.JvmArguments.AddRange(source.JvmArguments);
        target.GameArguments.AddRange(source.GameArguments);
    }

    /// <summary>
    /// Объединяет библиотеки, сохраняя порядок «родитель → ребёнок».
    /// Одинаковые имена (без учёта регистра) схлопываются: остаётся дочерняя запись.
    /// Библиотеки без имени не дедуплицируются.
    /// </summary>
    private static List<VersionLibrary> MergeLibraries(
        IReadOnlyList<VersionLibrary> existing,
        IReadOnlyList<VersionLibrary> added)
    {
        var combined = new List<VersionLibrary>(existing.Count + added.Count);
        combined.AddRange(existing);
        combined.AddRange(added);

        var lastIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < combined.Count; index++)
        {
            var name = combined[index].Name?.Trim() ?? "";
            if (name.Length > 0)
            {
                lastIndexByName[name] = index;
            }
        }

        var result = new List<VersionLibrary>(combined.Count);
        for (var index = 0; index < combined.Count; index++)
        {
            var library = combined[index];
            var name = library.Name?.Trim() ?? "";

            if (name.Length == 0 || lastIndexByName[name] == index)
            {
                result.Add(library);
            }
        }

        return result;
    }

    private static string NormalizeVersionId(string? versionId)
    {
        var id = versionId?.Trim() ?? "";
        if (id.Length == 0 ||
            id is "." or ".." ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains(Path.DirectorySeparatorChar) ||
            id.Contains(Path.AltDirectorySeparatorChar) ||
            id.Contains(':'))
        {
            throw new LauncherException(
                $"Некорректный id версии Minecraft: \"{versionId}\". " +
                "Id не должен содержать путь или недопустимые символы.");
        }

        return id;
    }
}
