using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>Одна версия из официального манифеста Mojang.</summary>
public sealed record MinecraftCatalogVersion(
    string Id,
    string Type,
    string Url,
    string Sha1,
    DateTimeOffset ReleaseTime,
    DateTimeOffset Time)
{
    /// <summary>Время релиза; если в манифесте его нет — время последнего изменения.</summary>
    [JsonIgnore]
    public DateTimeOffset EffectiveTime => ReleaseTime != default ? ReleaseTime : Time;

    [JsonIgnore]
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSnapshot => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Каталог официальных версий Minecraft с локальным кэшем. Сетевые запросы идут
/// через <see cref="DownloadService"/>; при недоступности сети возвращается
/// сохранённый кэш, даже если он устарел.
/// </summary>
public sealed class MinecraftVersionCatalog
{
    public const string ManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public const string CacheFileName = "minecraft-versions.json";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DownloadService _downloads;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MinecraftVersionCatalog(
        LauncherPaths? paths = null,
        DownloadService? downloads = null,
        TimeProvider? timeProvider = null)
    {
        var launcherPaths = paths ?? new LauncherPaths();
        _downloads = downloads ?? DownloadService.Shared;
        _timeProvider = timeProvider ?? TimeProvider.System;
        CachePath = Path.Combine(launcherPaths.CacheDir, CacheFileName);
    }

    /// <summary>Путь к локальному кэшу каталога.</summary>
    public string CachePath { get; }

    /// <summary>Когда каталог был успешно получен (из сети или из кэша).</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Последний успешный вызов обновил каталог из сети.</summary>
    public bool LastRefreshFromNetwork { get; private set; }

    /// <summary>Сеть не ответила, поэтому отдан устаревший кэш.</summary>
    public bool LastRefreshUsedStaleCache { get; private set; }

    /// <summary>Текст последней ошибки сети; null после успешного обновления.</summary>
    public string? LastError { get; private set; }

    /// <summary>Возраст текущего снимка каталога. null — данных ещё нет.</summary>
    public TimeSpan? CacheAge =>
        FetchedAt is { } fetchedAt ? _timeProvider.GetUtcNow() - fetchedAt : null;

    /// <summary>Кэш не старше <see cref="CacheTtl"/>.</summary>
    public bool IsCacheFresh =>
        FetchedAt is { } fetchedAt && IsFresh(fetchedAt);

    private bool IsFresh(DateTimeOffset fetchedAt) =>
        _timeProvider.GetUtcNow() - fetchedAt < CacheTtl;

    /// <summary>
    /// Возвращает список версий (сначала новые). Свежий кэш используется без сети;
    /// <paramref name="forceRefresh"/> заставляет обновить его, а при ошибке — отдать
    /// устаревший кэш. Если кэша нет, бросает <see cref="LauncherException"/>.
    /// </summary>
    public async Task<IReadOnlyList<MinecraftCatalogVersion>> GetVersionsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = TryLoadCache();

            if (!forceRefresh && cached is not null && IsFresh(cached.FetchedAt))
            {
                return ApplyCache(cached, fromNetwork: false, usedStaleCache: false);
            }

            try
            {
                var json = await _downloads
                    .GetStringAsync(ManifestUrl, cancellationToken)
                    .ConfigureAwait(false);

                var versions = ParseManifest(json);
                if (versions.Count == 0)
                {
                    throw new LauncherException(
                        "Официальный манифест Minecraft не содержит ни одной версии.");
                }

                var fetchedAt = _timeProvider.GetUtcNow();

                try
                {
                    SaveCache(fetchedAt, versions);
                }
                catch (LauncherException ex)
                {
                    // Кэш — вспомогательный: без него каталог всё равно можно показать.
                    LastError = ex.Message;
                }

                return ApplyCache(
                    new MinecraftCatalogCache(fetchedAt, versions),
                    fromNetwork: true,
                    usedStaleCache: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LauncherException ex)
            {
                if (cached is not null)
                {
                    LastError = ex.Message;
                    return ApplyCache(cached, fromNetwork: false, usedStaleCache: true);
                }

                throw new LauncherException(
                    "Не удалось получить список версий Minecraft, а локальный кэш отсутствует. " +
                    "Проверьте подключение к интернету и повторите попытку.",
                    ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Разбирает официальный manifest без сетевых запросов.</summary>
    public static IReadOnlyList<MinecraftCatalogVersion> ParseManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LauncherException("Пустой ответ манифеста версий Minecraft.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный манифест версий Minecraft: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                throw new LauncherException(
                    "В манифесте версий Minecraft нет списка versions.");
            }

            return ParseVersions(versions);
        }
    }

    internal static string SerializeCache(
        DateTimeOffset fetchedAt,
        IReadOnlyList<MinecraftCatalogVersion> versions)
    {
        return JsonSerializer.Serialize(
            new MinecraftCatalogCache(fetchedAt, versions.ToList()),
            CacheJsonOptions);
    }

    internal static MinecraftCatalogCache? ParseCache(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parsed = ParseVersions(versions);
            if (parsed.Count == 0)
            {
                return null;
            }

            var fetchedAt = root.TryGetProperty("fetchedAt", out var fetchedElement) &&
                            fetchedElement.ValueKind == JsonValueKind.String
                ? ParseDate(fetchedElement.GetString() ?? "")
                : default;

            return new MinecraftCatalogCache(fetchedAt, parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void SaveCache(
        DateTimeOffset fetchedAt,
        IReadOnlyList<MinecraftCatalogVersion> versions)
    {
        var json = SerializeCache(fetchedAt, versions);
        var directory = Path.GetDirectoryName(CachePath);
        var tempPath = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить кэш версий Minecraft в {CachePath}: {ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal MinecraftCatalogCache? TryLoadCache()
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        try
        {
            var cache = ParseCache(File.ReadAllText(CachePath));
            if (cache is null)
            {
                return null;
            }

            if (cache.FetchedAt == default)
            {
                return cache with { FetchedAt = File.GetLastWriteTimeUtc(CachePath) };
            }

            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IReadOnlyList<MinecraftCatalogVersion> ApplyCache(
        MinecraftCatalogCache cache,
        bool fromNetwork,
        bool usedStaleCache)
    {
        FetchedAt = cache.FetchedAt == default ? null : cache.FetchedAt;
        LastRefreshFromNetwork = fromNetwork;
        LastRefreshUsedStaleCache = usedStaleCache;

        if (fromNetwork || !usedStaleCache)
        {
            LastError = null;
        }

        return cache.Versions;
    }

    private static List<MinecraftCatalogVersion> ParseVersions(JsonElement versions)
    {
        var result = new List<MinecraftCatalogVersion>();

        foreach (var entry in versions.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(entry, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new MinecraftCatalogVersion(
                id,
                GetString(entry, "type"),
                GetString(entry, "url"),
                GetString(entry, "sha1"),
                ParseDate(GetString(entry, "releaseTime")),
                ParseDate(GetString(entry, "time"))));
        }

        return result
            .OrderByDescending(version => version.EffectiveTime)
            .ThenByDescending(
                version => version.Id,
                Comparer<string>.Create((left, right) => VersionText.Compare(left, right)))
            .ToList();
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : default;

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

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
}

internal sealed record MinecraftCatalogCache(
    DateTimeOffset FetchedAt,
    IReadOnlyList<MinecraftCatalogVersion> Versions);
