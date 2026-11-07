using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Проверка обновлений модов через Modrinth по SHA-512 и безопасная загрузка новых jar.
/// </summary>
public sealed class ModUpdateService
{
    private const int HashBatchSize = 100;
    private const int BufferSize = 81920;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient DefaultDownloadClient = CreateDownloadClient();

    private readonly ModrinthClient _modrinth;
    private readonly ModMetadataReader _metadataReader;
    private readonly ModService _mods;
    private readonly HttpClient _downloadClient;

    public ModUpdateService(
        ModrinthClient? modrinth = null,
        ModMetadataReader? metadataReader = null,
        ModService? mods = null,
        HttpClient? downloadClient = null)
    {
        _modrinth = modrinth ?? new ModrinthClient();
        _metadataReader = metadataReader ?? new ModMetadataReader();
        _mods = mods ?? new ModService();
        _downloadClient = downloadClient ?? DefaultDownloadClient;
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = DownloadTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");
        return client;
    }

    /// <summary>
    /// Проверяет включённые <c>.jar</c> инстанса по SHA-512 через Modrinth.
    /// Возвращает только моды, у которых версия в метаданных отличается от версии на Modrinth.
    /// </summary>
    public async Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(
        InstanceProfile instance,
        CancellationToken cancellationToken = default)
    {
        var entries = _mods.List(instance)
            .Where(entry => entry.Enabled)
            .ToList();

        if (entries.Count == 0)
        {
            return Array.Empty<ModUpdateInfo>();
        }

        var candidates = new List<ModCandidate>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string hash;

            try
            {
                hash = await ComputeSha512Async(entry.FullPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Один недоступный файл не должен ломать проверку остальных.
                continue;
            }

            var metadata = _metadataReader.Read(entry.FullPath);
            candidates.Add(new ModCandidate(entry, metadata, hash));
        }

        var updates = new List<ModUpdateInfo>();
        var minecraftVersion = instance.MinecraftVersion?.Trim() ?? "";

        foreach (var batch in candidates.Chunk(HashBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versions = await _modrinth
                .GetVersionsByHashesAsync(batch.Select(candidate => candidate.Hash), cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                versions.TryGetValue(candidate.Hash, out var installedVersion);

                var projectId = installedVersion?.ProjectId;
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    continue;
                }

                var projectVersions = await _modrinth
                    .GetProjectVersionsAsync(projectId, cancellationToken)
                    .ConfigureAwait(false);

                var latest = projectVersions
                    .Where(version => !string.IsNullOrWhiteSpace(version.DownloadUrl))
                    .Where(version => version.VersionType == "release")
                    .Where(version => version.Dependencies.All(dependency => dependency.Type != "required" && dependency.Type != "incompatible"))
                    .Where(version => IsCompatible(version, minecraftVersion, instance))
                    .OrderByDescending(version => version, ModVersionComparer.Instance)
                    .FirstOrDefault();

                if (latest is null)
                {
                    continue;
                }

                if (installedVersion is not null
                    && !string.IsNullOrWhiteSpace(latest.Id)
                    && string.Equals(latest.Id, installedVersion.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var currentVersion = candidate.Metadata?.Version?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(currentVersion))
                {
                    currentVersion = installedVersion?.VersionNumber?.Trim() ?? "";
                }

                if (!string.IsNullOrWhiteSpace(currentVersion)
                    && VersionText.Compare(latest.VersionNumber, currentVersion) <= 0)
                {
                    continue;
                }

                updates.Add(new ModUpdateInfo
                {
                    FileName = candidate.Entry.FileName,
                    ModId = candidate.Metadata?.ModId ?? "",
                    CurrentVersion = currentVersion,
                    LatestVersion = latest.VersionNumber,
                    DownloadUrl = latest.DownloadUrl,
                    Sha512 = latest.Sha512,
                    Changelog = latest.Changelog,
                    ProjectId = latest.ProjectId,
                    VersionId = latest.Id,
                    OriginalSha512 = candidate.Hash,
                    PublishedAt = latest.DatePublished
                });
            }
        }

        return updates
            .OrderBy(update => update.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsCompatible(ModrinthVersion version, string minecraftVersion, InstanceProfile instance)
    {
        var versionId = instance.VersionId;
        var loader = versionId?.Contains("neoforge", StringComparison.OrdinalIgnoreCase) == true ? "neoforge"
            : versionId?.Contains("fabric", StringComparison.OrdinalIgnoreCase) == true ? "fabric"
            : versionId?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true ||
              (string.IsNullOrWhiteSpace(versionId) && !string.IsNullOrWhiteSpace(instance.ForgeVersion)) ? "forge" : "";
        return !string.IsNullOrWhiteSpace(minecraftVersion) && loader.Length > 0 &&
               version.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase) &&
               version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Скачивает новый jar во временный файл рядом с mods, проверяет, что это zip/jar,
    /// переименовывает старый файл в <c>.jar.old-&lt;timestamp&gt;</c> и атомарно ставит новый.
    /// Возвращает путь нового файла.
    /// </summary>
    public async Task<string> DownloadAsync(
        ModUpdateInfo update,
        string modsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            throw new LauncherException("Не указана папка mods для обновления.");
        }

        var fileName = Path.GetFileName(update.FileName);

        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Некорректное имя файла мода для обновления: {update.FileName}");
        }

        if (!Uri.TryCreate(update.DownloadUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new LauncherException(
                $"В обновлении {fileName} не указан корректный адрес загрузки.");
        }

        string modsDir;

        try
        {
            modsDir = Path.GetFullPath(modsDirectory);
            Directory.CreateDirectory(modsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось подготовить папку mods {modsDirectory}: {ex.Message}",
                ex);
        }

        var targetPath = Path.Combine(modsDir, fileName);
        var tempPath = Path.Combine(
            modsDir,
            fileName + "." + Guid.NewGuid().ToString("N") + ".download");

        try
        {
            await DownloadFileAsync(uri, tempPath, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(update.Sha512))
            {
                throw new LauncherException(
                    $"Modrinth не вернул sha512 для {fileName} — обновление отменено.");
            }

            var actualHash = await ComputeSha512Async(tempPath, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(
                    actualHash,
                    NormalizeSha512(update.Sha512),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(
                    $"Контрольная сумма скачанного {fileName} не совпала с Modrinth — файл заменён не будет.");
            }

            if (!IsZipArchive(tempPath))
            {
                throw new LauncherException(
                    $"Скачанный файл {fileName} не является jar/zip-архивом.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tempPath);
            throw new LauncherException(
                $"Не удалось проверить скачанный файл {fileName}: {ex.Message}",
                ex);
        }
        catch
        {
            DeleteQuietly(tempPath);
            throw;
        }

        string? oldPath = null;

        try
        {
            if (!File.Exists(targetPath))
                throw new LauncherException($"Исходный файл {fileName} больше не найден; обновление отменено.");
            if (File.Exists(targetPath))
            {
                if (string.IsNullOrWhiteSpace(update.OriginalSha512))
                    throw new LauncherException($"Нельзя заменить {fileName}: не сохранён хеш исходного файла.");
                var originalHash = await ComputeSha512Async(targetPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(originalHash, NormalizeSha512(update.OriginalSha512), StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException($"Файл {fileName} изменился после проверки обновлений; замена отменена.");
                oldPath = BuildOldPath(targetPath);
                File.Move(targetPath, oldPath);
            }

            File.Move(tempPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tempPath);

            if (oldPath is not null && !File.Exists(targetPath))
            {
                try
                {
                    File.Move(oldPath, targetPath);
                }
                catch (IOException)
                {
                    // Старый файл останется под именем .old-<timestamp>.
                }
                catch (UnauthorizedAccessException)
                {
                    // То же самое: не заслоняем основную ошибку.
                }
            }

            throw new LauncherException(
                $"Не удалось заменить {fileName}: {ex.Message}",
                ex);
        }
        catch
        {
            DeleteQuietly(tempPath);
            throw;
        }

        return targetPath;
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string tempPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        try
        {
            using var response = await _downloadClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(
                    $"Не удалось скачать обновление {Path.GetFileName(uri.LocalPath)}: " +
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var input = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);

            await using var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await input.CopyToAsync(output, BufferSize, timeout.Token).ConfigureAwait(false);
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LauncherException(
                $"Не удалось скачать обновление: истекло время ожидания " +
                $"({DownloadTimeout.TotalMinutes:0} мин).",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LauncherException($"Не удалось скачать обновление: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException($"Не удалось сохранить обновление: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException(
                $"Нет доступа для записи обновления в {Path.GetDirectoryName(tempPath)}: {ex.Message}",
                ex);
        }
    }

    private static async Task<string> ComputeSha512Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string BuildOldPath(string targetPath)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = targetPath + ".old-" + stamp;

        for (var index = 2; File.Exists(candidate) && index < 1000; index++)
        {
            candidate = targetPath + ".old-" + stamp + "-" + index.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private static string NormalizeSha512(string value)
    {
        var hash = (value ?? "").Trim();

        if (hash.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase))
        {
            hash = hash["sha512:".Length..];
        }

        return hash.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static void CleanupOldFiles(string modsDir)
    {
        try
        {
            var threshold = DateTime.UtcNow - TimeSpan.FromDays(7);

            foreach (var path in Directory.EnumerateFiles(modsDir, "*.old-*", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    DeleteQuietly(path);
                }
            }

            foreach (var path in Directory.EnumerateFiles(modsDir, "*.download", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    DeleteQuietly(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Уборка старых файлов не должна ломать обновление.
        }
    }

    private sealed class ModVersionComparer : IComparer<ModrinthVersion>
    {
        public static readonly ModVersionComparer Instance = new();

        public int Compare(ModrinthVersion? left, ModrinthVersion? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var byVersion = VersionText.Compare(left.VersionNumber, right.VersionNumber);
            return byVersion != 0
                ? byVersion
                : left.DatePublished.CompareTo(right.DatePublished);
        }
    }

    private static void DeleteQuietly(string path)
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
            // Временный файл останется до следующей попытки.
        }
        catch (UnauthorizedAccessException)
        {
            // Не заслоняем основную ошибку.
        }
    }

    private sealed record ModCandidate(ModEntry Entry, ModMetadata? Metadata, string Hash);
}

/// <summary>Доступное обновление мода.</summary>
public sealed class ModUpdateInfo
{
    /// <summary>Имя текущего файла в папке mods.</summary>
    public string FileName { get; set; } = "";

    public string ModId { get; set; } = "";

    public string CurrentVersion { get; set; } = "";

    public string LatestVersion { get; set; } = "";

    public string DownloadUrl { get; set; } = "";

    /// <summary>SHA-512 файла с Modrinth; обязателен для безопасной замены jar.</summary>
    public string Sha512 { get; set; } = "";

    /// <summary>Хеш исходного файла, подтверждающий, что он не изменился после проверки.</summary>
    public string OriginalSha512 { get; set; } = "";

    public string Changelog { get; set; } = "";

    public string ProjectId { get; set; } = "";

    public string VersionId { get; set; } = "";

    public DateTimeOffset PublishedAt { get; set; }
}
