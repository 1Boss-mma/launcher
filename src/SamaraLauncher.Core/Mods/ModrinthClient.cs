using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Клиент Modrinth API v2: поиск версий по SHA-512 и получение проекта.
/// </summary>
public sealed class ModrinthClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2/";
    private const int RetryAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient DefaultHttpClient = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Time, ModrinthSearchResult Result)> _searchCache = new();

    public ModrinthClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = RequestTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// Возвращает версии Modrinth по SHA-512 хешам файлов.
    /// Неизвестные хеши в ответе отсутствуют.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
        IEnumerable<string> sha512Hashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sha512Hashes);

        var hashes = sha512Hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(NormalizeHash)
            .Where(IsSha512)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (hashes.Count == 0)
        {
            return new Dictionary<string, ModrinthVersion>();
        }

        var payload = JsonSerializer.Serialize(new VersionFilesRequest
        {
            Hashes = hashes,
            Algorithm = "sha512"
        });

        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "version_files")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                },
                "version_files",
                cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, "version_files", cancellationToken).ConfigureAwait(false);

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LauncherException(
                    "Modrinth вернул неожиданный ответ на запрос version_files.");
            }

            var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var version = MapVersion(property.Value);
                if (version is not null)
                {
                    result[property.Name] = version;
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Modrinth вернул некорректный JSON на запрос version_files: {ex.Message}",
                ex);
        }
    }

    /// <summary>Возвращает проект по id или slug. null — проект не найден (HTTP 404).</summary>
    public async Task<ModrinthProject?> GetProjectAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug))
        {
            throw new LauncherException("Не указан id или slug проекта Modrinth.");
        }

        var url = BaseUrl + "project/" + Uri.EscapeDataString(idOrSlug.Trim());

        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                "project",
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "project", cancellationToken).ConfigureAwait(false);

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var project = await JsonSerializer
                .DeserializeAsync<ModrinthProjectDto>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (project is null)
            {
                return null;
            }

            return new ModrinthProject
            {
                Id = project.Id ?? "",
                Slug = project.Slug ?? "",
                Title = project.Title ?? "",
                Description = project.Description ?? "",
                ClientSide = project.ClientSide ?? "unknown",
                ServerSide = project.ServerSide ?? "unknown",
                Body = project.Body ?? "",
                GameVersions = project.GameVersions ?? new List<string>(),
                Loaders = project.Loaders ?? new List<string>(),
                Gallery = project.Gallery?.Where(image => !string.IsNullOrWhiteSpace(image.Url)).Select(image => image.Url!).ToList() ?? new List<string>(),
                Downloads = project.Downloads,
                Updated = project.Updated,
                IconUrl = project.IconUrl ?? ""
            };
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Modrinth вернул некорректный JSON для проекта {idOrSlug}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Возвращает все версии проекта (GET /v2/project/{id}/version). 404 — пустой список.
    /// Фильтрация по игре/загрузчику и выбор новейшей — на стороне ModUpdateService.
    /// </summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug))
        {
            throw new LauncherException("Не указан id или slug проекта Modrinth.");
        }

        var url = BaseUrl + "project/" + Uri.EscapeDataString(idOrSlug.Trim()) + "/version";

        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                "project versions",
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<ModrinthVersion>();
        }

        await EnsureSuccessAsync(response, "project versions", cancellationToken).ConfigureAwait(false);

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var versions = await JsonSerializer
                .DeserializeAsync<List<ModrinthVersionDto>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (versions is null)
            {
                return Array.Empty<ModrinthVersion>();
            }

            var result = new List<ModrinthVersion>(versions.Count);

            foreach (var dto in versions)
            {
                var version = MapVersion(dto);
                if (version is not null)
                {
                    result.Add(version);
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Modrinth вернул некорректный JSON для версий проекта {idOrSlug}: {ex.Message}",
                ex);
        }
    }

    /// <summary>Получает конкретную версию, в том числе указанную зависимостью.</summary>
    public async Task<ModrinthVersion?> GetVersionAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new LauncherException("Не указан ID версии Modrinth.");
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "version/" + Uri.EscapeDataString(id.Trim())),
            "version", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, "version", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var dto = await JsonSerializer.DeserializeAsync<ModrinthVersionDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return MapVersion(dto);
    }

    /// <summary>Категории модов из официального справочника Modrinth.</summary>
    public async Task<IReadOnlyList<ModrinthCategory>> GetModCategoriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "tag/category"), "categories", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "categories", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var categories = await JsonSerializer.DeserializeAsync<List<ModrinthCategory>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new();
        return categories.Where(item => item.ProjectType == "mod" && !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ищет проекты модов с фильтрами и постраничной загрузкой.</summary>
    public async Task<ModrinthSearchResult> SearchModsAsync(
        string query, string minecraftVersion, string loader, string sort, int offset,
        string category = "", string environment = "",
        CancellationToken cancellationToken = default, string projectType = "mod")
    {
        if (projectType is not ("mod" or "resourcepack" or "shader")) throw new ArgumentException("Unknown project type.", nameof(projectType));
        var facets = new List<string[]> { new[] { "project_type:" + projectType } };
        if (!string.IsNullOrWhiteSpace(minecraftVersion))
            facets.Add(new[] { "versions:" + minecraftVersion.Trim() });
        if (!string.IsNullOrWhiteSpace(loader))
            facets.Add(new[] { "categories:" + loader.Trim().ToLowerInvariant() });
        if (!string.IsNullOrWhiteSpace(category))
            facets.Add(new[] { "categories:" + category.Trim().ToLowerInvariant() });
        if (environment is "client_only" or "server_only" or "client_and_server")
            facets.Add(new[] { "environment:" + environment });
        else if (environment == "client")
            facets.Add(new[] { "client_side:required", "client_side:optional" });
        else if (environment == "server")
            facets.Add(new[] { "server_side:required", "server_side:optional" });
        if (!new[] { "relevance", "downloads", "updated", "newest", "follows" }.Contains(sort))
            sort = "relevance";
        var url = BaseUrl + "search?limit=20&offset=" + Math.Max(0, offset)
            + "&index=" + Uri.EscapeDataString(sort)
            + "&query=" + Uri.EscapeDataString(query ?? "")
            + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets));
        if (_searchCache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(5))
            return new ModrinthSearchResult { Hits = cached.Result.Hits, TotalHits = cached.Result.TotalHits, IsCached = true };
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), "search", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "search", cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<ModrinthSearchResult>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new ModrinthSearchResult();
            result.Hits = result.Hits.Where(hit => string.Equals(hit.ProjectType, projectType, StringComparison.Ordinal)).ToList();
            if (_searchCache.Count >= 100) _searchCache.Clear();
            _searchCache[url] = (DateTimeOffset.UtcNow, result);
            return result;
        }
        catch (Exception ex) when (ex is LauncherException or HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_searchCache.TryGetValue(url, out cached))
                return new ModrinthSearchResult { Hits = cached.Result.Hits, TotalHits = cached.Result.TotalHits, IsCached = true };
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= RetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = requestFactory();

            HttpResponseMessage response;

            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(
                    $"Modrinth не ответил за {RequestTimeout.TotalSeconds:0} секунд.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;

                if (attempt < RetryAttempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new LauncherException($"Не удалось подключиться к Modrinth: {ex.Message}", ex);
            }

            if (IsRetryable(response.StatusCode) && attempt < RetryAttempts)
            {
                var delay = GetRetryDelay(response) ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }

        throw new LauncherException(
            $"Modrinth ({operation}) недоступен: {lastError?.Message ?? "превышено число попыток"}",
            lastError ?? new HttpRequestException("Modrinth недоступен"));
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is not null)
        {
            return retryAfter.Delta.Value > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : retryAfter.Delta.Value;
        }

        if (retryAfter?.Date is not null)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
            }
        }

        return null;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        var status = (int)response.StatusCode;
        var hint = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                " Превышен лимит запросов Modrinth, повторите позже.",
            HttpStatusCode.BadRequest =>
                " Проверьте корректность запроса.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                " Доступ к Modrinth запрещён.",
            HttpStatusCode.NotFound =>
                " Ресурс не найден.",
            _ => ""
        };

        throw new LauncherException(
            $"Modrinth ({operation}) вернул HTTP {status} {response.ReasonPhrase}.{hint}");
    }

    private static ModrinthVersion? MapVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return MapVersion(element.Deserialize<ModrinthVersionDto>(JsonOptions));
    }

    private static ModrinthVersion? MapVersion(ModrinthVersionDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var file = dto.Files?.FirstOrDefault(item => item.Primary)
                   ?? dto.Files?.FirstOrDefault();

        var sha512 = "";
        if (file?.Hashes is not null)
        {
            file.Hashes.TryGetValue("sha512", out sha512);
        }

        return new ModrinthVersion
        {
            Id = dto.Id ?? "",
            ProjectId = dto.ProjectId ?? "",
            Name = dto.Name ?? "",
            VersionNumber = dto.VersionNumber ?? "",
            DatePublished = dto.DatePublished ?? default,
            DownloadUrl = file?.Url ?? "",
            FileName = file?.FileName ?? "",
            Sha512 = sha512 ?? "",
            VersionType = dto.VersionType ?? "",
            Dependencies = dto.Dependencies ?? new List<ModrinthDependency>(),
            GameVersions = dto.GameVersions ?? new List<string>(),
            Loaders = dto.Loaders ?? new List<string>(),
            Changelog = dto.Changelog ?? ""
        };
    }

    private static string NormalizeHash(string hash)
    {
        var value = hash.Trim();

        if (value.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha512:".Length..];
        }

        return value.ToLowerInvariant();
    }

    private static bool IsSha512(string hash) =>
        hash.Length == 128 && hash.All(Uri.IsHexDigit);

    private sealed class VersionFilesRequest
    {
        [JsonPropertyName("hashes")]
        public List<string> Hashes { get; init; } = new();

        [JsonPropertyName("algorithm")]
        public string Algorithm { get; init; } = "sha512";
    }

    private sealed class ModrinthVersionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; init; }

        [JsonPropertyName("date_published")]
        public DateTimeOffset? DatePublished { get; init; }

        [JsonPropertyName("changelog")]
        public string? Changelog { get; init; }

        [JsonPropertyName("version_type")]
        public string? VersionType { get; init; }

        [JsonPropertyName("dependencies")]
        public List<ModrinthDependency>? Dependencies { get; init; }

        [JsonPropertyName("game_versions")]
        public List<string>? GameVersions { get; init; }

        [JsonPropertyName("loaders")]
        public List<string>? Loaders { get; init; }

        [JsonPropertyName("files")]
        public List<ModrinthFileDto>? Files { get; init; }
    }

    private sealed class ModrinthFileDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("filename")]
        public string? FileName { get; init; }

        [JsonPropertyName("primary")]
        public bool Primary { get; init; }

        [JsonPropertyName("hashes")]
        public Dictionary<string, string>? Hashes { get; init; }
    }

    private sealed class ModrinthProjectDto
    {
        [JsonPropertyName("downloads")] public long? Downloads { get; init; }
        [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; init; }
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("client_side")] public string? ClientSide { get; init; }
        [JsonPropertyName("server_side")] public string? ServerSide { get; init; }

        [JsonPropertyName("game_versions")]
        public List<string>? GameVersions { get; init; }

        [JsonPropertyName("loaders")]
        public List<string>? Loaders { get; init; }

        [JsonPropertyName("gallery")]
        public List<ModrinthGalleryDto>? Gallery { get; init; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; init; }
    }

    private sealed class ModrinthGalleryDto
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
    }
}

/// <summary>Версия мода на Modrinth.</summary>
public sealed class ModrinthVersion
{
    public string Id { get; set; } = "";

    public string ProjectId { get; set; } = "";

    public string Name { get; set; } = "";

    public string VersionNumber { get; set; } = "";

    public DateTimeOffset DatePublished { get; set; }

    public string DownloadUrl { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>SHA-512 файла из files[].hashes — для проверки после скачивания.</summary>
    public string Sha512 { get; set; } = "";

    public List<string> GameVersions { get; set; } = new();

    public List<string> Loaders { get; set; } = new();

    public string Changelog { get; set; } = "";

    public string VersionType { get; set; } = "";

    public List<ModrinthDependency> Dependencies { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string Type { get; set; } = "";
}

/// <summary>Проект на Modrinth.</summary>
public sealed class ModrinthProject
{
    public long? Downloads { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public string Id { get; set; } = "";

    public string Slug { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public string ClientSide { get; set; } = "unknown";

    public string ServerSide { get; set; } = "unknown";

    public string Body { get; set; } = "";

    public List<string> GameVersions { get; set; } = new();

    public List<string> Loaders { get; set; } = new();

    public List<string> Gallery { get; set; } = new();

    public string IconUrl { get; set; } = "";
}

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthSearchHit> Hits { get; set; } = new();
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
    [JsonIgnore] public bool IsCached { get; set; }
}

public sealed class ModrinthCategory
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
}

public sealed class ModrinthSearchHit : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    private bool _isInstalling;
    private double _installProgress;
    private string _installationState = "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore] public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInstalling)));
        }
    }
    [JsonIgnore] public double InstallProgress
    {
        get => _installProgress;
        set
        {
            var next = Math.Clamp(value, 0d, 1d);
            if (Math.Abs(_installProgress - next) < 0.0001d) return;
            _installProgress = next;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InstallProgress)));
        }
    }
    [JsonPropertyName("project_id")] public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("date_modified")] public DateTimeOffset Updated { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    public string Details => $"{Author} · {Downloads:N0} загрузок · {Updated:dd.MM.yyyy} · {string.Join(", ", Categories)}";
    [JsonIgnore] public string InstallationState
    {
        get => _installationState;
        set
        {
            if (_installationState == value) return;
            _installationState = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InstallationState)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActionLabel)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInstalled)));
        }
    }
    [JsonIgnore] public bool IsInstalled => InstallationState is "Установлен" or "Есть обновление";
    [JsonIgnore] public string ActionLabel => IsInstalled ? "Установлено" : "Установить";
    public string? IconUri => Uri.TryCreate(IconUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
        ? uri.AbsoluteUri : null;
}

