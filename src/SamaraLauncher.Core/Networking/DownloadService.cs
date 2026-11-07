using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Networking;

/// <summary>
/// Загрузка файлов, строк и JSON по HTTP с повторами, проверкой хешей и прогрессом.
/// </summary>
public sealed class DownloadService
{
    private const int MaxAttempts = 3;
    private const int BufferSize = 81920;

    private static readonly HttpClient DefaultHttpClient = CreateHttpClient();

    /// <summary>Общий экземпляр для всего лаунчера.</summary>
    public static DownloadService Shared { get; } = new();

    private readonly HttpClient _httpClient;

    public DownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    private static HttpClient CreateHttpClient()
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
            Timeout = TimeSpan.FromMinutes(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");
        return client;
    }

    /// <summary>
    /// Скачивает файл во временный <c>.tmp</c>, проверяет хеш и атомарно заменяет файл назначения.
    /// Существующий файл с верным хешем не перекачивается.
    /// </summary>
    /// <param name="url">Адрес файла.</param>
    /// <param name="destinationPath">Куда сохранить файл.</param>
    /// <param name="sha1">Ожидаемый SHA1 (регистр не важен), если известен.</param>
    /// <param name="sha256">Ожидаемый SHA256 (регистр не важен), если известен.</param>
    /// <param name="expectedSize">Ожидаемый размер в байтах; 0 — не проверять.</param>
    /// <param name="progress">Прогресс по байтам текущего файла.</param>
    /// <param name="stage">Стадия установки для прогресса.</param>
    /// <param name="displayName">Имя файла в сообщении прогресса.</param>
    /// <param name="cancellationToken">Отмена загрузки.</param>
    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string? sha1 = null,
        string? sha256 = null,
        long expectedSize = 0,
        IProgress<LauncherProgress>? progress = null,
        ProgressStage stage = ProgressStage.Minecraft,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new LauncherException("Не указан адрес для скачивания.");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new LauncherException($"Не указан путь для сохранения файла {url}.");
        }

        destinationPath = Path.GetFullPath(destinationPath);
        var label = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(destinationPath)
            : displayName;

        if (await IsFileValidAsync(destinationPath, sha1, sha256, expectedSize, cancellationToken))
        {
            progress?.Report(new LauncherProgress(stage, $"{label}: уже загружено", 0, 0));
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку {directory}: {ex.Message}", ex);
            }
        }

        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(
                    url,
                    tempPath,
                    expectedSize,
                    progress,
                    stage,
                    label,
                    attempt,
                    cancellationToken).ConfigureAwait(false);

                if (!await IsFileValidAsync(
                        tempPath,
                        sha1,
                        sha256,
                        expectedSize,
                        cancellationToken).ConfigureAwait(false))
                {
                    lastError = new IOException(
                        "скачанный файл не прошёл проверку хеша или размера");
                    DeleteQuietly(tempPath);

                    if (attempt < MaxAttempts)
                    {
                        progress?.Report(new LauncherProgress(
                            stage,
                            $"{label}: повтор {attempt + 1}/{MaxAttempts} после проверки",
                            0,
                            0));

                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                try
                {
                    File.Move(tempPath, destinationPath, overwrite: true);
                }
                catch (IOException ex)
                {
                    // Это не сетевая ошибка: файл назначения занят (игра/антивирус).
                    DeleteQuietly(tempPath);
                    throw new LauncherException(
                        $"Не удалось заменить {destinationPath}: файл занят. " +
                        "Закройте игру и повторите попытку.", ex);
                }

                progress?.Report(new LauncherProgress(stage, $"{label}: готово", 0, 0));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DeleteQuietly(tempPath);
                throw;
            }
            catch (LauncherException)
            {
                DeleteQuietly(tempPath);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                DeleteQuietly(tempPath);
                throw new LauncherException(
                    $"Нет доступа для записи {destinationPath}: {ex.Message}. " +
                    "Проверьте права на папку игры.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex;

                if (attempt < MaxAttempts)
                {
                    progress?.Report(new LauncherProgress(
                        stage,
                        $"{label}: повтор {attempt + 1}/{MaxAttempts} после ошибки",
                        0,
                        0));

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        DeleteQuietly(tempPath);
                        throw;
                    }
                }
            }
        }

        DeleteQuietly(tempPath);
        throw new LauncherException(
            $"Не удалось скачать {url} после {MaxAttempts} попыток: {lastError?.Message ?? "неизвестная ошибка"}",
            lastError ?? new IOException("Загрузка не удалась"));
    }

    /// <summary>Скачивает текст (например, манифест версий).</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                    {
                        lastError = new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new LauncherException(
                        $"Не удалось получить {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                        "Проверьте интернет и повторите попытку.");
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LauncherException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new LauncherException(
            $"Не удалось получить {url} после {MaxAttempts} попыток: {lastError?.Message ?? "неизвестная ошибка"}",
            lastError ?? new IOException("Запрос не удался"));
    }

    /// <summary>Скачивает и разбирает JSON. Вызывающий обязан освободить документ.</summary>
    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"По адресу {url} вернулся некорректный JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Проверяет существующий файл по размеру и (если заданы) хешам.</summary>
    public static bool IsFileValid(
        string path,
        string? sha1 = null,
        string? sha256 = null,
        long expectedSize = 0)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (expectedSize > 0 && info.Length != expectedSize)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sha1))
        {
            var actual = Convert.ToHexString(ComputeHash(path, SHA1.Create()));
            if (!string.Equals(actual, sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var actual = Convert.ToHexString(ComputeHash(path, SHA256.Create()));
            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Асинхронный вариант проверки файла — не блокирует UI во время хеширования.</summary>
    public static async Task<bool> IsFileValidAsync(
        string path,
        string? sha1 = null,
        string? sha256 = null,
        long expectedSize = 0,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (expectedSize > 0 && info.Length != expectedSize)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sha1))
        {
            var actual = await ComputeHashAsync(path, SHA1.Create(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var actual = await ComputeHashAsync(path, SHA256.Create(), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task DownloadOnceAsync(
        string url,
        string tempPath,
        long expectedSize,
        IProgress<LauncherProgress>? progress,
        ProgressStage stage,
        string label,
        int attempt,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        var existing = 0L;

        try
        {
            if (attempt > 1 && File.Exists(tempPath))
            {
                existing = new FileInfo(tempPath).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existing = 0;
        }

        if (expectedSize > 0 && existing >= expectedSize)
        {
            DeleteQuietly(tempPath);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            DeleteQuietly(tempPath);
            existing = 0;
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: временный файл устарел.");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (IsTransient(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            throw new LauncherException(
                $"Не удалось скачать {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                "Проверьте интернет и повторите попытку.");
        }

        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (existing > 0 && !append)
        {
            // Сервер не поддержал Range — начинаем файл заново.
            existing = 0;
        }

        var remaining = response.Content.Headers.ContentLength ?? 0;
        var total = expectedSize > 0
            ? expectedSize
            : (remaining > 0 ? remaining + existing : 0);

        var message = attempt > 1
            ? $"{label}: попытка {attempt}/{MaxAttempts}"
            : label;

        await using var source = await response.Content
            .ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);

        await using var target = new FileStream(
            tempPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        long completed = existing;
        var lastReport = 0L;

        progress?.Report(new LauncherProgress(stage, message, completed, total));

        while (true)
        {
            var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            completed += read;

            var now = Environment.TickCount64;
            if (now - lastReport >= 120 || (total > 0 && completed >= total))
            {
                lastReport = now;
                progress?.Report(new LauncherProgress(stage, message, completed, total));
            }
        }

        await target.FlushAsync(timeout.Token).ConfigureAwait(false);
        progress?.Report(new LauncherProgress(stage, message, completed, total > 0 ? total : completed));
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static byte[] ComputeHash(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   BufferSize,
                   FileOptions.SequentialScan))
        {
            return algorithm.ComputeHash(stream);
        }
    }

    private static async Task<string> ComputeHashAsync(
        string path,
        HashAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        using (algorithm)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hash = await algorithm.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
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
            // Временный файл мог остаться заблокированным — следующая попытка перезапишет его.
        }
        catch (UnauthorizedAccessException)
        {
            // То же: не заслоняем основную ошибку загрузки.
        }
    }
}
