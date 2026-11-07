using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Распаковывает нативные библиотеки (.dll/.so/.dylib) из jar'ов vanilla и Forge
/// в <c>versions\&lt;versionId&gt;\natives</c>.
/// </summary>
public sealed class NativesExtractor
{
    /// <summary>Собирает natives из version json указанной версии и её родителя (vanilla).</summary>
    public async Task ExtractAsync(
        string gameDir,
        string versionId,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для распаковки natives.");
        }

        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new LauncherException("Не указан id версии для распаковки natives.");
        }

        gameDir = Path.GetFullPath(gameDir);
        var paths = new VersionPaths(gameDir);
        var versionJson = paths.VersionJson(versionId);

        if (!File.Exists(versionJson))
        {
            throw new LauncherException(
                $"Не найден version json {versionJson}. Сначала установите Forge.");
        }

        var jsonPaths = new List<string> { versionJson };
        var inheritedId = ReadInheritedVersionId(versionJson, versionId);
        if (!string.IsNullOrWhiteSpace(inheritedId) &&
            !string.Equals(inheritedId, versionId, StringComparison.OrdinalIgnoreCase))
        {
            var inheritedJson = paths.VersionJson(inheritedId);
            if (File.Exists(inheritedJson))
            {
                jsonPaths.Insert(0, inheritedJson);
            }
        }

        var missingJars = new List<string>();
        var jars = new List<string>();
        var seenJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonPath in jsonPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string json;
            try
            {
                json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось прочитать version json {jsonPath}: {ex.Message}", ex);
            }

            foreach (var jar in CollectNativeJars(json, paths.LibrariesDir, missingJars))
            {
                if (seenJars.Add(jar))
                {
                    jars.Add(jar);
                }
            }
        }

        if (missingJars.Count > 0)
        {
            var sample = string.Join(", ", missingJars.Take(5).Select(Path.GetFileName));
            var suffix = missingJars.Count > 5 ? ", ..." : "";

            throw new LauncherException(
                $"Не найдены jar с natives ({missingJars.Count}): {sample}{suffix}. " +
                "Повторите установку Minecraft/Forge — файлы библиотек повреждены или удалены.");
        }

        var nativesDir = paths.NativesDir(versionId);
        try
        {
            Directory.CreateDirectory(nativesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку natives {nativesDir}: {ex.Message}", ex);
        }

        if (jars.Count == 0)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                "Natives для Windows не требуются",
                0,
                0));

            return;
        }

        for (var i = 0; i < jars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Natives: {i + 1}/{jars.Count} — {Path.GetFileName(jars[i])}",
                i,
                jars.Count));

            // ZipArchive/ExtractToFile — синхронный I/O; уводим его с UI-потока.
            await Task.Run(
                () => ExtractJar(jars[i], nativesDir, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Forge,
            $"Natives распакованы ({jars.Count} архивов)",
            jars.Count,
            jars.Count));
    }

    private IEnumerable<string> CollectNativeJars(
        string json,
        string librariesDir,
        List<string> missingJars)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный version json при распаковке natives: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            var metadata = VersionMetadata.Parse(root);
            var index = 0;

            foreach (var libraryElement in libraries.EnumerateArray())
            {
                var library = index < metadata.Libraries.Count
                    ? metadata.Libraries[index]
                    : new VersionLibrary();

                index++;

                if (!RuleEvaluator.IsAllowed(library.Rules))
                {
                    continue;
                }

                var classifiers = new List<string>();
                var nativesClassifier = GetNativesWindowsClassifier(libraryElement);
                if (nativesClassifier is not null)
                {
                    classifiers.Add(nativesClassifier);
                }

                var namedClassifier = GetNamedWindowsClassifier(library);
                if (namedClassifier is not null &&
                    !classifiers.Contains(namedClassifier, StringComparer.OrdinalIgnoreCase))
                {
                    classifiers.Add(namedClassifier);
                }

                foreach (var classifier in classifiers)
                {
                    var jarPath = ResolveJarPath(
                        libraryElement,
                        library,
                        librariesDir,
                        classifier,
                        namedClassifier is not null &&
                        string.Equals(classifier, namedClassifier, StringComparison.OrdinalIgnoreCase));

                    if (jarPath is null)
                    {
                        continue;
                    }

                    if (!File.Exists(jarPath))
                    {
                        missingJars.Add(jarPath);
                        continue;
                    }

                    yield return jarPath;
                }
            }
        }
    }

    private static string? ResolveJarPath(
        JsonElement libraryElement,
        VersionLibrary library,
        string librariesDir,
        string classifier,
        bool artifactIsClassifierJar)
    {
        if (libraryElement.TryGetProperty("downloads", out var downloads) &&
            downloads.ValueKind == JsonValueKind.Object)
        {
            if (downloads.TryGetProperty("classifiers", out var classifiers) &&
                classifiers.ValueKind == JsonValueKind.Object &&
                classifiers.TryGetProperty(classifier, out var classifierArtifact) &&
                classifierArtifact.ValueKind == JsonValueKind.Object)
            {
                return ResolveArtifactPath(classifierArtifact, library, classifier, librariesDir);
            }

            if (artifactIsClassifierJar &&
                downloads.TryGetProperty("artifact", out var artifact) &&
                artifact.ValueKind == JsonValueKind.Object)
            {
                return ResolveArtifactPath(artifact, library, classifier, librariesDir);
            }
        }

        if (artifactIsClassifierJar &&
            libraryElement.TryGetProperty("artifact", out var topLevelArtifact) &&
            topLevelArtifact.ValueKind == JsonValueKind.Object)
        {
            return ResolveArtifactPath(topLevelArtifact, library, classifier, librariesDir);
        }

        return Path.GetFullPath(VersionPaths.GuessLibraryPath(librariesDir, library, classifier));
    }

    private static string ResolveArtifactPath(
        JsonElement artifact,
        VersionLibrary library,
        string classifier,
        string librariesDir)
    {
        var relative = artifact.TryGetProperty("path", out var pathElement) &&
                       pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(relative))
        {
            return Path.GetFullPath(VersionPaths.GuessLibraryPath(librariesDir, library, classifier));
        }

        return VersionPaths.ResolveLibraryPath(librariesDir, relative);
    }

    private static void ExtractJar(string jarPath, string nativesDir, CancellationToken cancellationToken)
    {
        var jarTime = File.GetLastWriteTimeUtc(jarPath);

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name) ||
                    string.IsNullOrEmpty(entry.FullName) ||
                    entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    IsMetaInf(entry.FullName))
                {
                    continue;
                }

                var extension = Path.GetExtension(entry.Name);
                if (!IsNativeExtension(extension))
                {
                    continue;
                }

                var destination = Path.Combine(nativesDir, Path.GetFileName(entry.Name));
                var info = new FileInfo(destination);

                if (info.Exists &&
                    info.Length == entry.Length &&
                    info.LastWriteTimeUtc >= jarTime)
                {
                    continue;
                }

                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(
                $"Не удалось открыть архив natives {jarPath}: файл повреждён. " +
                "Удалите библиотеку и повторите установку.", ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Не удалось распаковать natives из {jarPath}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException(
                $"Нет доступа для записи natives в {nativesDir}: {ex.Message}", ex);
        }
    }

    private static bool IsMetaInf(string entryName)
    {
        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (string.Equals(segment, "META-INF", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeExtension(string extension) =>
        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase);

    private static string? ReadInheritedVersionId(string versionJsonPath, string versionId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
            if (document.RootElement.TryGetProperty("inheritsFrom", out var inheritsFrom) &&
                inheritsFrom.ValueKind == JsonValueKind.String)
            {
                var value = inheritsFrom.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // Ниже попробуем вывести версию из id.
        }

        var marker = versionId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? versionId[..marker] : null;
    }

    private static string? GetNativesWindowsClassifier(JsonElement libraryElement)
    {
        if (!libraryElement.TryGetProperty("natives", out var natives) ||
            natives.ValueKind != JsonValueKind.Object ||
            !natives.TryGetProperty("windows", out var windows) ||
            windows.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var classifier = windows.GetString();
        if (string.IsNullOrWhiteSpace(classifier))
        {
            return null;
        }

        var arch = Environment.Is64BitOperatingSystem ? "64" : "32";
        return classifier.Replace("${arch}", arch, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetNamedWindowsClassifier(VersionLibrary library)
    {
        var parts = library.Name.Split(':');
        if (parts.Length >= 4 && IsWindowsNativesClassifier(parts[3]))
        {
            return parts[3];
        }

        return null;
    }

    private static bool IsWindowsNativesClassifier(string classifier)
    {
        if (string.Equals(classifier, "natives-windows", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string prefix = "natives-windows-";
        if (!classifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = classifier[prefix.Length..].ToLowerInvariant();
        var architecture = RuntimeInformation.ProcessArchitecture;

        return suffix switch
        {
            "64" or "x86_64" => architecture == Architecture.X64,
            "32" or "x86" => architecture == Architecture.X86,
            "arm64" => architecture == Architecture.Arm64,
            _ => false
        };
    }
}
