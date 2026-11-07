using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Channels;

/// <summary>
/// Проверка latest.json канала обновлений, выбор максимального подходящего релиза
/// и загрузка exe с проверкой SHA-256 и размера.
/// </summary>
public sealed class ChannelService
{
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly LauncherPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly DownloadService _downloadService;

    public ChannelService(LauncherPaths? paths = null, HttpClient? httpClient = null)
    {
        _paths = paths ?? new LauncherPaths();
        _httpClient = httpClient ?? SharedHttpClient;
        _downloadService = new DownloadService(_httpClient);
    }

    /// <summary>Папка, куда скачиваются exe каналов.</summary>
    public string UpdatesDir => _paths.UpdatesDir;

    /// <summary>
    /// Скачивает latest.json и возвращает максимальный подходящий релиз канала
    /// новее <paramref name="currentVersion"/>. Если обновления нет — null.
    /// </summary>
    public async Task<ChannelManifest?> CheckAsync(
        string manifestUrl,
        string currentVersion,
        UpdateChannel channel,
        CancellationToken ct = default)
    {
        var uri = ValidateManifestUrl(manifestUrl);

        if (!SemVer.TryParse(currentVersion, out var current))
        {
            throw new LauncherException(
                $"Некорректная текущая версия лаунчера: \"{currentVersion}\". " +
                "Ожидается SemVer MAJOR.MINOR.PATCH.");
        }

        var json = await FetchManifestJsonAsync(uri, ct).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Некорректный latest.json ({uri}): {ex.Message}",
                ex);
        }

        using (document)
        {
            var manifests = ReadManifests(document.RootElement, uri, channel);
            ChannelManifest? best = null;
            var bestVersion = default(SemVer);
            var bestRank = -1;

            foreach (var manifest in manifests)
            {
                if (!SemVer.TryParse(manifest.Version, out var version))
                {
                    continue;
                }

                if (version.CompareTo(current) <= 0 || !manifest.IsCompatibleWith(currentVersion))
                {
                    continue;
                }

                var rank = ChannelRank(manifest.Channel);

                if (best is null)
                {
                    best = manifest;
                    bestVersion = version;
                    bestRank = rank;
                    continue;
                }

                var comparison = version.CompareTo(bestVersion);
                if (comparison > 0 || (comparison == 0 && rank > bestRank))
                {
                    best = manifest;
                    bestVersion = version;
                    bestRank = rank;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Скачивает exe релиза в
    /// <c>%LOCALAPPDATA%\SamaraLauncher\updates\Launcher-&lt;version&gt;-&lt;channel&gt;.exe.download</c>.
    /// Проверяет SHA-256 и размер, файл заменяется атомарно. Возвращает путь к .download.
    /// </summary>
    public async Task<string> DownloadAsync(
        ChannelManifest manifest,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (manifest is null)
        {
            throw new LauncherException("Не передан манифест обновления.");
        }

        if (!SemVer.TryParse(manifest.Version, out var version))
        {
            throw new LauncherException(
                $"Некорректная версия обновления: \"{manifest.Version}\".");
        }

        var url = ValidateDownloadUrl(manifest.Url);
        var sha256 = NormalizeSha256(manifest.Sha256);

        if (manifest.Size <= 0)
        {
            throw new LauncherException(
                "В манифесте обновления не указан корректный размер файла (size).");
        }

        var channelName = UpdateChannels.ToString(manifest.Channel);
        var fileName =
            $"Launcher-{SanitizeFileName(version.ToString())}-{channelName}.exe.download";

        string targetPath;
        try
        {
            targetPath = Path.Combine(_paths.UpdatesDir, fileName);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Некорректный путь для сохранения обновления: {fileName}",
                ex);
        }

        try
        {
            Directory.CreateDirectory(_paths.UpdatesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку обновлений {_paths.UpdatesDir}: {ex.Message}",
                ex);
        }

        ct.ThrowIfCancellationRequested();

        bool alreadyDownloaded;
        try
        {
            alreadyDownloaded = await DownloadService.IsFileValidAsync(
                    targetPath,
                    sha256: sha256,
                    expectedSize: manifest.Size,
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось проверить скачанный файл обновления {targetPath}: {ex.Message}",
                ex);
        }

        if (alreadyDownloaded)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Update,
                $"Обновление {version} уже скачано.",
                1,
                1));

            return targetPath;
        }

        TryDelete(targetPath);

        try
        {
            await _downloadService.DownloadFileAsync(
                url,
                targetPath,
                sha256: sha256,
                expectedSize: manifest.Size,
                progress: progress,
                stage: ProgressStage.Update,
                displayName: $"Лаунчер {version} ({channelName})",
                cancellationToken: ct).ConfigureAwait(false);

            if (!await DownloadService.IsFileValidAsync(
                    targetPath,
                    sha256: sha256,
                    expectedSize: manifest.Size,
                    cancellationToken: ct).ConfigureAwait(false))
            {
                TryDelete(targetPath);
                throw new LauncherException(
                    $"Скачанный файл обновления {version} не прошёл проверку sha256 или размера.");
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            TryDelete(targetPath);
            throw new LauncherException(
                "Не удалось скачать обновление: истекло время ожидания.",
                ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            TryDelete(targetPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            TryDelete(targetPath);
            throw new LauncherException(
                $"Не удалось скачать обновление: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            TryDelete(targetPath);
            throw new LauncherException(
                $"Не удалось сохранить обновление: {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(targetPath);
            throw new LauncherException(
                $"Нет доступа к папке обновлений {_paths.UpdatesDir}: {ex.Message}",
                ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Update,
            $"Обновление {version} готово: {Path.GetFileName(targetPath)}",
            1,
            1));

        return targetPath;
    }

    /// <summary>
    /// Проверяет обновление и сразу скачивает его. null — обновления нет.
    /// </summary>
    public async Task<ChannelManifest?> CheckAndDownloadAsync(
        string manifestUrl,
        string currentVersion,
        UpdateChannel channel,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        var manifest = await CheckAsync(manifestUrl, currentVersion, channel, ct)
            .ConfigureAwait(false);

        if (manifest is null)
        {
            return null;
        }

        await DownloadAsync(manifest, progress, ct).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Упрощённый вариант проверки и загрузки — без отчёта о прогрессе.</summary>
    public Task<ChannelManifest?> CheckAndDownloadAsync(
        string manifestUrl,
        string currentVersion,
        UpdateChannel channel,
        CancellationToken ct) =>
        CheckAndDownloadAsync(manifestUrl, currentVersion, channel, null, ct);

    private async Task<string> FetchManifestJsonAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ManifestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(
                    $"Сервер обновлений вернул HTTP {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase} для {uri}.");
            }

            return await response.Content
                .ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LauncherException(
                "Не удалось проверить обновления: истекло время ожидания " +
                $"({ManifestTimeout.TotalSeconds:0} с).",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LauncherException(
                $"Не удалось проверить обновления: {ex.Message}",
                ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать latest.json: {ex.Message}",
                ex);
        }
    }

    private static List<ChannelManifest> ReadManifests(
        JsonElement root,
        Uri manifestUri,
        UpdateChannel requestedChannel)
    {
        var result = new List<ChannelManifest>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var item in root.EnumerateArray())
            {
                var manifest = ReadManifest(
                    item,
                    manifestUri,
                    requestedChannel,
                    $"latest.json[{index++}]");

                if (manifest is not null)
                {
                    result.Add(manifest);
                }
            }

            return result;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LauncherException(
                "latest.json должен содержать объект релиза или массив \"releases\".");
        }

        if (TryGetProperty(root, "releases", out var releases))
        {
            if (releases.ValueKind != JsonValueKind.Array)
            {
                throw new LauncherException(
                    "В latest.json поле \"releases\" должно быть массивом.");
            }

            var index = 0;

            foreach (var item in releases.EnumerateArray())
            {
                var manifest = ReadManifest(
                    item,
                    manifestUri,
                    requestedChannel,
                    $"releases[{index++}]");

                if (manifest is not null)
                {
                    result.Add(manifest);
                }
            }

            return result;
        }

        var single = ReadManifest(root, manifestUri, requestedChannel, "latest.json");
        if (single is not null)
        {
            result.Add(single);
        }

        return result;
    }

    private static ChannelManifest? ReadManifest(
        JsonElement element,
        Uri manifestUri,
        UpdateChannel requestedChannel,
        string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LauncherException($"{label}: элемент должен быть JSON-объектом.");
        }

        var channel = ReadChannel(element, requestedChannel, label);
        if (!IsChannelAllowed(channel, requestedChannel))
        {
            return null;
        }

        var versionText = GetRequiredString(element, "version", label);
        if (!SemVer.TryParse(versionText, out var version))
        {
            throw new LauncherException(
                $"{label}: некорректная версия \"{versionText}\". " +
                "Ожидается SemVer MAJOR.MINOR.PATCH.");
        }

        var minVersionText = GetOptionalString(element, "minVersion", label, "").Trim();
        if (minVersionText.Length > 0)
        {
            if (!SemVer.TryParse(minVersionText, out var minVersion))
            {
                throw new LauncherException(
                    $"{label}: некорректная minVersion \"{minVersionText}\". " +
                    "Ожидается SemVer.");
            }

            minVersionText = minVersion.ToString();
        }

        return new ChannelManifest
        {
            Version = version.ToString(),
            Channel = channel,
            Url = ReadUrl(element, manifestUri, label),
            Sha256 = ReadSha256(element, label),
            Notes = GetOptionalString(element, "notes", label, ""),
            Mandatory = GetOptionalBool(element, "mandatory", label),
            MinVersion = minVersionText,
            PublishedAt = ReadPublishedAt(element, label),
            Size = GetRequiredSize(element, label)
        };
    }

    private static UpdateChannel ReadChannel(
        JsonElement element,
        UpdateChannel fallback,
        string label)
    {
        if (!TryGetProperty(element, "channel", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LauncherException($"{label}: поле \"channel\" должно быть строкой.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!UpdateChannels.TryParse(text, out var channel))
        {
            throw new LauncherException(
                $"{label}: неизвестный канал обновления \"{text}\". " +
                "Допустимо: stable, beta, dev.");
        }

        return channel;
    }

    private static string GetRequiredString(JsonElement element, string name, string label)
    {
        if (!TryGetProperty(element, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            throw new LauncherException($"{label}: не указано поле \"{name}\".");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LauncherException($"{label}: поле \"{name}\" должно быть строкой.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LauncherException($"{label}: поле \"{name}\" пустое.");
        }

        return text.Trim();
    }

    private static string GetOptionalString(
        JsonElement element,
        string name,
        string label,
        string fallback)
    {
        if (!TryGetProperty(element, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LauncherException($"{label}: поле \"{name}\" должно быть строкой.");
        }

        return value.GetString() ?? fallback;
    }

    private static bool GetOptionalBool(JsonElement element, string name, string label)
    {
        if (!TryGetProperty(element, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new LauncherException(
                $"{label}: поле \"{name}\" должно быть true или false.")
        };
    }

    private static long GetRequiredSize(JsonElement element, string label)
    {
        if (!TryGetProperty(element, "size", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            throw new LauncherException($"{label}: не указан размер файла (size).");
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var size)
            && size > 0)
        {
            return size;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out size)
            && size > 0)
        {
            return size;
        }

        throw new LauncherException(
            $"{label}: поле \"size\" должно быть положительным числом байт.");
    }

    private static DateTimeOffset? ReadPublishedAt(JsonElement element, string label)
    {
        if (!TryGetProperty(element, "publishedAt", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LauncherException(
                $"{label}: поле \"publishedAt\" должно быть строкой ISO 8601.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var publishedAt))
        {
            return publishedAt;
        }

        throw new LauncherException(
            $"{label}: некорректная дата publishedAt \"{text}\".");
    }

    private static string ReadUrl(JsonElement element, Uri manifestUri, string label)
    {
        var text = GetRequiredString(element, "url", label);

        if (!Uri.TryCreate(manifestUri, text, out var url))
        {
            throw new LauncherException($"{label}: некорректный url \"{text}\".");
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new LauncherException(
                $"{label}: url должен использовать http или https.");
        }

        return url.ToString();
    }

    private static string ReadSha256(JsonElement element, string label)
    {
        var text = GetRequiredString(element, "sha256", label);

        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["sha256:".Length..].Trim();
        }

        if (text.Length != 64 || !text.All(IsHex))
        {
            throw new LauncherException(
                $"{label}: поле \"sha256\" должно содержать 64 hex-символа.");
        }

        return text.ToLowerInvariant();
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsChannelAllowed(UpdateChannel release, UpdateChannel requested) =>
        requested switch
        {
            UpdateChannel.Stable => release == UpdateChannel.Stable,
            UpdateChannel.Beta => release is UpdateChannel.Stable or UpdateChannel.Beta,
            UpdateChannel.Dev => true,
            _ => false
        };

    private static int ChannelRank(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => 2,
        UpdateChannel.Beta => 1,
        UpdateChannel.Dev => 0,
        _ => -1
    };

    private static Uri ValidateManifestUrl(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new LauncherException(
                "Не задан адрес latest.json для проверки обновлений.");
        }

        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LauncherException(
                "Адрес latest.json должен быть абсолютным http(s)-URL.");
        }

        return uri;
    }

    private static string ValidateDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new LauncherException(
                "В манифесте не указан url файла обновления.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LauncherException(
                "В манифесте указан некорректный url: нужен абсолютный http(s)-адрес.");
        }

        return uri.ToString();
    }

    private static string NormalizeSha256(string? value)
    {
        var hash = (value ?? "").Trim();

        if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            hash = hash["sha256:".Length..].Trim();
        }

        if (hash.Length != 64 || !hash.All(IsHex))
        {
            throw new LauncherException(
                "В манифесте обновления указан некорректный sha256: " +
                "нужны 64 hex-символа.");
        }

        return hash.ToLowerInvariant();
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            return "latest";
        }

        return result.Length > 80 ? result[..80] : result;
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
            // Временный/недокачанный файл не критичен.
        }
        catch (UnauthorizedAccessException)
        {
            // То же: не заслоняем основную ошибку.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SamaraLauncher/{LauncherVersion.Current}");

        return client;
    }
}
