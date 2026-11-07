using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>
/// Устанавливает ванильный Minecraft: version json, client jar, библиотеки и ассеты.
/// По умолчанию — 1.20.1; можно установить любую версию из каталога Mojang.
/// </summary>
public sealed class MinecraftInstaller
{
    public const string MinecraftVersion = "1.20.1";

    private readonly DownloadService _downloads;
    private readonly MinecraftVersionCatalog _catalog;

    public MinecraftInstaller(
        DownloadService? downloads = null,
        MinecraftVersionCatalog? catalog = null)
    {
        _downloads = downloads ?? DownloadService.Shared;
        _catalog = catalog ?? new MinecraftVersionCatalog(downloads: _downloads);
    }

    /// <summary>
    /// Доустанавливает отсутствующие файлы Minecraft 1.20.1. Повторный вызов на готовом
    /// инстансе не делает сетевых запросов.
    /// </summary>
    public Task EnsureVanillaAsync(
        string gameDir,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        EnsureVanillaAsync(gameDir, MinecraftVersion, progress, cancellationToken);

    /// <summary>
    /// Доустанавливает отсутствующие файлы указанной ванильной версии. Уже корректные
    /// файлы повторно не скачиваются; существующие миры, настройки и моды не трогаются.
    /// </summary>
    public async Task EnsureVanillaAsync(
        string gameDir,
        string versionId,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для установки Minecraft.");
        }

        versionId = NormalizeVersionId(versionId);

        gameDir = Path.GetFullPath(gameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        var paths = new VersionPaths(gameDir);
        EnsureLauncherProfiles(gameDir, versionId);

        var versionJsonPath = paths.VersionJson(versionId);
        if (!IsReadableVersionJson(versionJsonPath))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Получение version json Minecraft {versionId}",
                0,
                0));

            await DownloadVersionJsonAsync(versionId, versionJsonPath, progress, cancellationToken);
        }

        var plan = LoadPlan(versionJsonPath, paths, versionId);

        if (IsInstalledQuick(plan, paths))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Minecraft {versionId} уже установлен",
                0,
                0));

            return;
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Проверка файлов Minecraft {versionId}",
            0,
            0));

        await EnsureClientAsync(plan, progress, cancellationToken);
        await EnsureLibrariesAsync(plan, progress, cancellationToken);
        await EnsureAssetIndexAsync(plan, progress, cancellationToken);
        await EnsureAssetsAsync(plan, paths, progress, cancellationToken);

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Minecraft {versionId} установлен",
            0,
            0));
    }

    private async Task DownloadVersionJsonAsync(
        string versionId,
        string destinationPath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var versions = await _catalog.GetVersionsAsync(
            forceRefresh: false,
            cancellationToken).ConfigureAwait(false);

        var version = versions.FirstOrDefault(item =>
            string.Equals(item.Id, versionId, StringComparison.Ordinal));

        if (version is null)
        {
            // Каталог мог закэшироваться до выхода новой версии — обновляем принудительно.
            versions = await _catalog.GetVersionsAsync(
                forceRefresh: true,
                cancellationToken).ConfigureAwait(false);

            version = versions.FirstOrDefault(item =>
                string.Equals(item.Id, versionId, StringComparison.Ordinal));
        }

        if (version is null)
        {
            throw new LauncherException(
                $"В официальном манифесте Minecraft не найдена версия {versionId}. " +
                "Проверьте id версии или обновите каталог.");
        }

        if (string.IsNullOrWhiteSpace(version.Url))
        {
            throw new LauncherException(
                $"В манифесте нет ссылки на version json {versionId}.");
        }

        await _downloads.DownloadFileAsync(
            version.Url,
            destinationPath,
            sha1: version.Sha1,
            progress: progress,
            stage: ProgressStage.Minecraft,
            displayName: $"version json {versionId}",
            cancellationToken: cancellationToken);
    }

    private async Task EnsureClientAsync(
        VanillaPlan plan,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (await DownloadService.IsFileValidAsync(
                plan.Client.DestinationPath,
                plan.Client.Sha1,
                expectedSize: plan.Client.Size,
                cancellationToken: cancellationToken))
        {
            return;
        }

        await _downloads.DownloadFileAsync(
            plan.Client.Url,
            plan.Client.DestinationPath,
            sha1: plan.Client.Sha1,
            expectedSize: plan.Client.Size,
            progress: progress,
            stage: ProgressStage.Minecraft,
            displayName: $"client {plan.VersionId}.jar",
            cancellationToken: cancellationToken);
    }

    private async Task EnsureLibrariesAsync(
        VanillaPlan plan,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var missing = new List<DownloadItem>();

        foreach (var library in plan.Libraries)
        {
            if (await DownloadService.IsFileValidAsync(
                    library.DestinationPath,
                    library.Sha1,
                    expectedSize: library.Size,
                    cancellationToken: cancellationToken))
            {
                continue;
            }

            missing.Add(library);
        }

        if (missing.Count == 0)
        {
            return;
        }

        var completed = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 12),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(missing, parallelOptions, async (library, token) =>
        {
            await _downloads.DownloadFileAsync(
                library.Url,
                library.DestinationPath,
                sha1: library.Sha1,
                expectedSize: library.Size,
                stage: ProgressStage.Minecraft,
                displayName: library.FileName,
                cancellationToken: token);

            var count = Interlocked.Increment(ref completed);
            if (count % 4 == 0 || count == missing.Count)
            {
                progress?.Report(new LauncherProgress(
                    ProgressStage.Minecraft,
                    $"Библиотеки: {count}/{missing.Count}",
                    count,
                    missing.Count));
            }
        });

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Библиотеки готовы ({missing.Count})",
            missing.Count,
            missing.Count));
    }

    private async Task EnsureAssetIndexAsync(
        VanillaPlan plan,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assetIndex = plan.AssetIndex;
        if (assetIndex is null)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                "Asset index для этой версии не требуется",
                0,
                0));

            return;
        }

        if (await DownloadService.IsFileValidAsync(
                assetIndex.DestinationPath,
                assetIndex.Sha1,
                expectedSize: assetIndex.Size,
                cancellationToken: cancellationToken))
        {
            return;
        }

        await _downloads.DownloadFileAsync(
            assetIndex.Url,
            assetIndex.DestinationPath,
            sha1: assetIndex.Sha1,
            expectedSize: assetIndex.Size,
            progress: progress,
            stage: ProgressStage.Minecraft,
            displayName: $"asset index {plan.AssetIndexId}",
            cancellationToken: cancellationToken);
    }

    private async Task EnsureAssetsAsync(
        VanillaPlan plan,
        VersionPaths paths,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assetIndex = plan.AssetIndex;
        if (assetIndex is null)
        {
            if (plan.RequiresVirtualAssets)
            {
                var virtualDir = Path.Combine(paths.AssetsDir, "virtual", plan.AssetIndexId);
                try
                {
                    Directory.CreateDirectory(virtualDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new LauncherException(
                        $"Не удалось создать папку legacy-ассетов {virtualDir}: {ex.Message}", ex);
                }
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                "Ассеты для этой версии не требуются",
                0,
                0));

            return;
        }

        var objects = ReadAssetObjects(assetIndex.DestinationPath, paths);
        var unique = new List<AssetObject>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in objects)
        {
            if (seenHashes.Add(asset.Hash))
            {
                unique.Add(asset);
            }
        }

        var missing = new List<AssetObject>();

        for (var i = 0; i < unique.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = unique[i];

            if (i % 100 == 0)
            {
                progress?.Report(new LauncherProgress(
                    ProgressStage.Minecraft,
                    $"Проверка ассетов: {i}/{unique.Count}",
                    i,
                    unique.Count));
            }

            var info = new FileInfo(asset.DestinationPath);
            if (info.Exists &&
                (asset.Size <= 0 || info.Length == asset.Size) &&
                await DownloadService.IsFileValidAsync(
                    asset.DestinationPath,
                    asset.Hash,
                    expectedSize: asset.Size,
                    cancellationToken: cancellationToken))
            {
                continue;
            }

            missing.Add(asset);
        }

        if (missing.Count > 0)
        {
            var totalBytes = missing.Sum(asset => Math.Max(0, asset.Size));
            long completedBytes = 0;
            var completedFiles = 0;

            progress?.Report(new LauncherProgress(
                ProgressStage.Minecraft,
                $"Ассеты: 0/{missing.Count} (0 из {ToMegabytes(totalBytes)})",
                0,
                totalBytes));

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 12),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(missing, parallelOptions, async (asset, token) =>
            {
                await _downloads.DownloadFileAsync(
                    asset.Url,
                    asset.DestinationPath,
                    sha1: asset.Hash,
                    expectedSize: asset.Size,
                    stage: ProgressStage.Minecraft,
                    displayName: asset.Hash,
                    cancellationToken: token);

                var done = Interlocked.Add(ref completedBytes, Math.Max(0, asset.Size));
                var count = Interlocked.Increment(ref completedFiles);
                if (count % 25 == 0 || count == missing.Count)
                {
                    progress?.Report(new LauncherProgress(
                        ProgressStage.Minecraft,
                        $"Ассеты: {count}/{missing.Count} ({ToMegabytes(done)} из {ToMegabytes(totalBytes)})",
                        done,
                        totalBytes));
                }
            });
        }

        if (plan.RequiresVirtualAssets)
        {
            await EnsureVirtualAssetsAsync(plan, paths, objects, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Ассеты готовы ({objects.Count} файлов)",
            objects.Count,
            objects.Count));
    }

    private static async Task EnsureVirtualAssetsAsync(
        VanillaPlan plan,
        VersionPaths paths,
        IReadOnlyList<AssetObject> objects,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var virtualDir = Path.Combine(paths.AssetsDir, "virtual", plan.AssetIndexId);
        try
        {
            Directory.CreateDirectory(virtualDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку legacy-ассетов {virtualDir}: {ex.Message}", ex);
        }

        var copied = 0;

        // Копирование тысяч мелких файлов — синхронный I/O, уводим его с UI-потока.
        await Task.Run(
            () =>
            {
                for (var i = 0; i < objects.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var asset = objects[i];

                    if (i % 250 == 0)
                    {
                        progress?.Report(new LauncherProgress(
                            ProgressStage.Minecraft,
                            $"Legacy-ассеты: {i}/{objects.Count}",
                            i,
                            objects.Count));
                    }

                    var target = ResolveVirtualPath(virtualDir, asset.Name);
                    if (target is null)
                    {
                        continue;
                    }

                    var info = new FileInfo(target);
                    if (info.Exists && (asset.Size <= 0 || info.Length == asset.Size))
                    {
                        continue;
                    }

                    try
                    {
                        var directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.Copy(asset.DestinationPath, target, overwrite: true);
                        copied++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new LauncherException(
                            $"Не удалось создать legacy-ассет {target}: {ex.Message}", ex);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new LauncherProgress(
            ProgressStage.Minecraft,
            $"Legacy-ассеты готовы ({copied} новых)",
            objects.Count,
            objects.Count));
    }

    private static string? ResolveVirtualPath(string virtualDir, string assetName)
    {
        var normalized = assetName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();

        if (normalized.Length == 0 ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':'))
        {
            return null;
        }

        var root = Path.GetFullPath(virtualDir);
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        var rootWithSeparator = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? target
            : null;
    }

    private static VanillaPlan LoadPlan(
        string versionJsonPath,
        VersionPaths paths,
        string versionId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(versionJsonPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный version json {versionJsonPath}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException($"Не удалось прочитать version json {versionJsonPath}: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            VersionMetadata metadata;
            try
            {
                metadata = VersionMetadata.Parse(root);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new LauncherException(
                    $"Некорректная структура version json {versionJsonPath}: {ex.Message}", ex);
            }

            if (!root.TryGetProperty("downloads", out var downloads) ||
                downloads.ValueKind != JsonValueKind.Object ||
                !downloads.TryGetProperty("client", out var clientElement) ||
                clientElement.ValueKind != JsonValueKind.Object)
            {
                throw new LauncherException(
                    $"В {versionJsonPath} нет downloads.client — это не ванильный version json Minecraft.");
            }

            var clientPath = paths.VersionJar(versionId);
            if (!string.IsNullOrWhiteSpace(metadata.ClientJarPath))
            {
                clientPath = VersionPaths.ResolveGamePath(paths.GameDir, metadata.ClientJarPath);
            }

            var client = ReadDownloadItem(clientElement, clientPath)
                         ?? throw new LauncherException($"В {versionJsonPath} у client нет ссылки на скачивание.");

            var assetIndexId = metadata.AssetIndexId ?? metadata.Assets ?? "";
            var requiresVirtualAssets = MinecraftVersionRules.IsLegacyAssets(
                versionId,
                metadata.Assets,
                assetIndexId);

            if (string.IsNullOrWhiteSpace(assetIndexId) && requiresVirtualAssets)
            {
                assetIndexId = "legacy";
            }

            var assetIndex = string.IsNullOrWhiteSpace(assetIndexId)
                ? null
                : ReadAssetIndex(root, paths, assetIndexId);

            var libraries = new List<DownloadItem>();
            var byPath = new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("libraries", out var rawLibraries) &&
                rawLibraries.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var libraryElement in rawLibraries.EnumerateArray())
                {
                    var library = index < metadata.Libraries.Count
                        ? metadata.Libraries[index]
                        : new VersionLibrary();

                    index++;

                    if (!RuleEvaluator.IsAllowed(library.Rules))
                    {
                        continue;
                    }

                    AddLibraryArtifacts(libraryElement, library, paths.LibrariesDir, byPath);
                }
            }

            libraries.AddRange(byPath.Values);
            libraries.Sort((left, right) =>
                string.Compare(left.DestinationPath, right.DestinationPath, StringComparison.OrdinalIgnoreCase));

            return new VanillaPlan
            {
                VersionId = versionId,
                Client = client,
                AssetIndex = assetIndex,
                AssetIndexId = assetIndexId,
                RequiresVirtualAssets = requiresVirtualAssets,
                Libraries = libraries
            };
        }
    }

    private static void AddLibraryArtifacts(
        JsonElement libraryElement,
        VersionLibrary library,
        string librariesDir,
        IDictionary<string, DownloadItem> items)
    {
        var hasDownloads = libraryElement.TryGetProperty("downloads", out var downloads) &&
                           downloads.ValueKind == JsonValueKind.Object;
        var nativesClassifier = GetNativesWindowsClassifier(libraryElement);
        var nameClassifier = GetNamedWindowsClassifier(library);
        var hasArtifact = TryGetArtifactElement(libraryElement, out var artifact);

        if (hasArtifact)
        {
            AddArtifact(artifact, library, classifier: null, librariesDir, items);
        }
        else if (nameClassifier is null &&
                 !string.IsNullOrWhiteSpace(library.Name))
        {
            // Старый формат библиотек: только name/url, без downloads.artifact.
            AddMavenItem(library, classifier: null, librariesDir, items);
        }

        if (nativesClassifier is not null)
        {
            if (hasDownloads &&
                downloads.TryGetProperty("classifiers", out var classifiers) &&
                classifiers.ValueKind == JsonValueKind.Object &&
                classifiers.TryGetProperty(nativesClassifier, out var classifierArtifact) &&
                classifierArtifact.ValueKind == JsonValueKind.Object)
            {
                AddArtifact(classifierArtifact, library, nativesClassifier, librariesDir, items);
            }
            else
            {
                AddMavenItem(library, nativesClassifier, librariesDir, items);
            }

            return;
        }

        if (nameClassifier is not null)
        {
            if (hasDownloads &&
                downloads.TryGetProperty("classifiers", out var nameClassifiers) &&
                nameClassifiers.ValueKind == JsonValueKind.Object &&
                nameClassifiers.TryGetProperty(nameClassifier, out var namedArtifact) &&
                namedArtifact.ValueKind == JsonValueKind.Object)
            {
                AddArtifact(namedArtifact, library, nameClassifier, librariesDir, items);
            }
            else if (!hasArtifact)
            {
                // Например, org.lwjgl:lwjgl-platform:...:natives-windows из старых json.
                AddMavenItem(library, nameClassifier, librariesDir, items);
            }
        }
    }

    private static void AddMavenItem(
        VersionLibrary library,
        string? classifier,
        string librariesDir,
        IDictionary<string, DownloadItem> items)
    {
        var destination = Path.GetFullPath(
            VersionPaths.GuessLibraryPath(librariesDir, library, classifier));

        if (items.ContainsKey(destination))
        {
            return;
        }

        var relative = Path.GetRelativePath(librariesDir, destination).Replace('\\', '/');
        var baseUrl = string.IsNullOrWhiteSpace(library.Url)
            ? "https://libraries.minecraft.net/"
            : library.Url!;

        items[destination] = new DownloadItem(
            CombineUrl(baseUrl, relative),
            destination,
            "",
            0);
    }

    private static void AddArtifact(
        JsonElement artifact,
        VersionLibrary library,
        string? classifier,
        string librariesDir,
        IDictionary<string, DownloadItem> items)
    {
        var relativePath = GetString(artifact, "path");
        string destination;

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            destination = VersionPaths.ResolveLibraryPath(librariesDir, relativePath);
        }
        else
        {
            destination = VersionPaths.GuessLibraryPath(librariesDir, library, classifier);
        }

        if (items.ContainsKey(destination))
        {
            return;
        }

        var url = GetString(artifact, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            var relativeForUrl = Path.GetRelativePath(librariesDir, destination)
                .Replace('\\', '/');
            var baseUrl = string.IsNullOrWhiteSpace(library.Url)
                ? "https://libraries.minecraft.net/"
                : library.Url!;

            url = CombineUrl(baseUrl, relativeForUrl);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        items[destination] = new DownloadItem(
            url,
            destination,
            GetString(artifact, "sha1") ?? "",
            GetLong(artifact, "size"));
    }

    private static DownloadItem? ReadDownloadItem(JsonElement element, string destinationPath)
    {
        var url = GetString(element, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new DownloadItem(
            url,
            destinationPath,
            GetString(element, "sha1") ?? "",
            GetLong(element, "size"));
    }

    private static DownloadItem? ReadAssetIndex(
        JsonElement root,
        VersionPaths paths,
        string id)
    {
        var destination = Path.Combine(paths.AssetsIndexesDir, id + ".json");

        if (root.TryGetProperty("assetIndex", out var assetIndex) &&
            assetIndex.ValueKind == JsonValueKind.Object)
        {
            var sha1 = GetString(assetIndex, "sha1") ?? "";
            var url = GetString(assetIndex, "url");

            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(sha1))
            {
                url = $"https://piston-meta.mojang.com/v1/packages/{sha1}/{id}.json";
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return new DownloadItem(url, destination, sha1, GetLong(assetIndex, "size"));
            }
        }

        // У самых старых alpha/beta assetIndex отсутствует — ассеты не нужны.
        return null;
    }

    private static List<AssetObject> ReadAssetObjects(string indexPath, VersionPaths paths)
    {
        if (!File.Exists(indexPath))
        {
            throw new LauncherException($"Не найден asset index {indexPath}. Повторите установку Minecraft.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(indexPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный asset index {indexPath}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException($"Не удалось прочитать asset index {indexPath}: {ex.Message}", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Object)
            {
                throw new LauncherException($"В asset index {indexPath} нет списка objects.");
            }

            var result = new List<AssetObject>();

            foreach (var property in objects.EnumerateObject())
            {
                var name = property.Name;
                var hash = GetString(property.Value, "hash");
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(hash) ||
                    hash.Length < 2)
                {
                    continue;
                }

                var size = GetLong(property.Value, "size");
                var destination = Path.Combine(paths.AssetsObjectsDir, hash[..2], hash);

                result.Add(new AssetObject(
                    name,
                    hash,
                    $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                    size,
                    destination));
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return result;
        }
    }

    private static bool IsInstalledQuick(VanillaPlan plan, VersionPaths paths)
    {
        if (!HasExpectedSize(plan.Client.DestinationPath, plan.Client.Size))
        {
            return false;
        }

        foreach (var library in plan.Libraries)
        {
            if (!HasExpectedSize(library.DestinationPath, library.Size))
            {
                return false;
            }
        }

        var assetIndex = plan.AssetIndex;
        if (assetIndex is null)
        {
            if (plan.RequiresVirtualAssets &&
                !Directory.Exists(Path.Combine(
                    paths.AssetsDir,
                    "virtual",
                    plan.AssetIndexId)))
            {
                return false;
            }
        }
        else
        {
            if (!HasExpectedSize(assetIndex.DestinationPath, assetIndex.Size))
            {
                return false;
            }

            try
            {
                var objects = ReadAssetObjects(assetIndex.DestinationPath, paths);
                foreach (var asset in objects)
                {
                    if (!HasExpectedSize(asset.DestinationPath, asset.Size))
                    {
                        return false;
                    }
                }

                if (plan.RequiresVirtualAssets &&
                    !AreVirtualAssetsReady(plan, paths, objects))
                {
                    return false;
                }
            }
            catch (LauncherException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreVirtualAssetsReady(
        VanillaPlan plan,
        VersionPaths paths,
        IReadOnlyList<AssetObject> objects)
    {
        var virtualDir = Path.Combine(paths.AssetsDir, "virtual", plan.AssetIndexId);

        foreach (var asset in objects)
        {
            var target = ResolveVirtualPath(virtualDir, asset.Name);
            if (target is null)
            {
                return false;
            }

            var info = new FileInfo(target);
            if (!info.Exists || (asset.Size > 0 && info.Length != asset.Size))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExpectedSize(string path, long expectedSize)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && (expectedSize <= 0 || info.Length == expectedSize);
    }

    private static bool IsReadableVersionJson(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("downloads", out var downloads) &&
                   downloads.ValueKind == JsonValueKind.Object &&
                   downloads.TryGetProperty("client", out _);
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureLauncherProfiles(string gameDir, string versionId)
    {
        var path = Path.Combine(gameDir, "launcher_profiles.json");
        var exists = File.Exists(path);
        JsonObject root;

        if (exists)
        {
            try
            {
                var parsed = JsonNode.Parse(
                    File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (parsed is not JsonObject objectRoot)
                {
                    // Корень не объект — не перезаписываем незнакомый формат.
                    return;
                }

                root = objectRoot;
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Не трогаем файл, который не удалось прочитать.
                return;
            }
        }
        else
        {
            root = new JsonObject();
        }

        var changed = false;

        var profiles = root["profiles"] as JsonObject;
        if (profiles is null)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
            changed = true;
        }

        if (!profiles.ContainsKey(versionId))
        {
            var now = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);

            profiles[versionId] = new JsonObject
            {
                ["name"] = JsonValue.Create(versionId),
                ["type"] = JsonValue.Create("custom"),
                ["created"] = JsonValue.Create(now),
                ["lastUsed"] = JsonValue.Create(now),
                ["lastVersionId"] = JsonValue.Create(versionId),
                ["icon"] = JsonValue.Create("Grass")
            };

            changed = true;
        }

        if (root["settings"] is not JsonObject)
        {
            root["settings"] = new JsonObject
            {
                ["enableSnapshots"] = JsonValue.Create(false),
                ["keepLauncherOpen"] = JsonValue.Create(false)
            };

            changed = true;
        }

        if (!root.ContainsKey("version"))
        {
            root["version"] = JsonValue.Create(3);
            changed = true;
        }

        if (exists && !changed)
        {
            return;
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(
                tempPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось обновить {path}: {ex.Message}. Проверьте права на папку игры.", ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool TryGetArtifactElement(JsonElement libraryElement, out JsonElement artifact)
    {
        if (libraryElement.TryGetProperty("downloads", out var downloads) &&
            downloads.ValueKind == JsonValueKind.Object &&
            downloads.TryGetProperty("artifact", out var fromDownloads) &&
            fromDownloads.ValueKind == JsonValueKind.Object)
        {
            artifact = fromDownloads;
            return true;
        }

        if (libraryElement.TryGetProperty("artifact", out var topLevel) &&
            topLevel.ValueKind == JsonValueKind.Object)
        {
            artifact = topLevel;
            return true;
        }

        artifact = default;
        return false;
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

    private static string CombineUrl(string baseUrl, string relativePath) =>
        baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string ToMegabytes(long bytes) => $"{bytes / 1024d / 1024d:0.#} МБ";

    private static string NormalizeVersionId(string versionId)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный файл не критичен.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }

    private sealed class VanillaPlan
    {
        public required string VersionId { get; init; }

        public required DownloadItem Client { get; init; }

        public DownloadItem? AssetIndex { get; init; }

        public required string AssetIndexId { get; init; }

        public bool RequiresVirtualAssets { get; init; }

        public required List<DownloadItem> Libraries { get; init; }
    }

    private sealed record DownloadItem(
        string Url,
        string DestinationPath,
        string Sha1,
        long Size)
    {
        public string FileName => Path.GetFileName(DestinationPath);
    }

    private sealed record AssetObject(
        string Name,
        string Hash,
        string Url,
        long Size,
        string DestinationPath);
}
