using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Update;

/// <summary>
/// Проверка, загрузка и установка обновлений самого лаунчера.
/// latest.json и exe лежат на своём сервере; замену выполняет Launcher.Updater.exe.
/// </summary>
public sealed class LauncherUpdateService
{
    public const string ManifestUrl = "https://github.com/1Boss-mma/launcher/releases/latest/download/latest.json";

    public Task<LauncherUpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct) =>
        CheckAsync(ManifestUrl, currentVersion, ct);

    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LauncherPaths _paths;
    private readonly HttpClient _httpClient;

    public LauncherUpdateService(LauncherPaths? paths = null, HttpClient? httpClient = null)
    {
        _paths = paths ?? new LauncherPaths();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <summary>
    /// Скачивает latest.json и возвращает описание обновления, если на сервере версия новее.
    /// Если обновления нет — возвращает null.
    /// </summary>
    public async Task<LauncherUpdateInfo?> CheckAsync(string manifestUrl, string currentVersion, CancellationToken ct)
    {
        ValidateManifestUrl(manifestUrl);

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            throw new LauncherException("Не задана текущая версия лаунчера.");
        }

        var manifest = await FetchManifestAsync(manifestUrl, ct).ConfigureAwait(false);

        if (!LauncherVersion.IsNewer(manifest.Version, currentVersion))
        {
            return null;
        }

        return new LauncherUpdateInfo
        {
            Version = manifest.Version,
            Notes = manifest.Notes ?? "",
            Mandatory = manifest.Mandatory,
            Manifest = manifest
        };
    }

    /// <summary>
    /// Скачивает обновление в %LOCALAPPDATA%\SamaraLauncher\updates\Launcher-&lt;version&gt;.exe
    /// и проверяет sha256, если он задан в latest.json. Возвращает путь к файлу.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        if (manifest is null)
        {
            throw new LauncherException("Не передан манифест обновления.");
        }

        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new LauncherException("В latest.json не указан корректный url файла обновления.");
        }

        if (!IsValidSha256(manifest.Sha256))
        {
            throw new LauncherException(
                "В latest.json не указан корректный sha256 файла обновления. " +
                "Обновление без проверки хеша запрещено — опубликуйте манифест с sha256.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            Trace.TraceWarning(
                $"latest.json/обновление загружается по HTTP без TLS: {uri}. " +
                "Рекомендуется перейти на HTTPS.");
        }

        var version = string.IsNullOrWhiteSpace(manifest.Version) ? "latest" : manifest.Version.Trim();
        var targetPath = Path.Combine(_paths.UpdatesDir, "Launcher-" + SanitizeFileName(version) + ".exe");
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".download";

        try
        {
            Directory.CreateDirectory(_paths.UpdatesDir);
            ct.ThrowIfCancellationRequested();

            if (File.Exists(targetPath))
            {
                if (await HasExpectedHashAsync(targetPath, manifest.Sha256, ct).ConfigureAwait(false))
                {
                    progress?.Report(new LauncherProgress(
                        ProgressStage.Update,
                        $"Обновление {version} уже скачано.",
                        1,
                        1));

                    return targetPath;
                }

                // Старый файл с тем же именем не совпал по хешу — не оставляем его.
                TryDelete(targetPath);
            }

            TryDelete(tempPath);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var finalUri = response.RequestMessage?.RequestUri;
            if (uri.Scheme == Uri.UriSchemeHttps
                && finalUri is not null
                && finalUri.Scheme == Uri.UriSchemeHttp)
            {
                throw new LauncherException(
                    "Сервер обновлений перенаправил HTTPS-загрузку на HTTP. " +
                    "Обновление отклонено: возможна подмена файла.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(
                    $"Не удалось скачать обновление: сервер вернул {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            progress?.Report(new LauncherProgress(
                ProgressStage.Update,
                $"Скачивание лаунчера {version}…",
                0,
                total));

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long completed = 0;
                int read;

                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    completed += read;

                    progress?.Report(new LauncherProgress(
                        ProgressStage.Update,
                        $"Скачивание лаунчера {version}…",
                        completed,
                        total));
                }
            }

            var actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            var expected = NormalizeHash(manifest.Sha256);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                throw new LauncherException(
                    $"Контрольная сумма обновления не совпала. Ожидалось {expected}, получено {actual}.");
            }

            File.Move(tempPath, targetPath, overwrite: true);

            progress?.Report(new LauncherProgress(
                ProgressStage.Update,
                $"Обновление {version} готово к установке.",
                1,
                1));

            return targetPath;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            TryDelete(tempPath);
            throw new LauncherException("Не удалось скачать обновление: истекло время ожидания.", ex);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            TryDelete(tempPath);
            throw new LauncherException($"Не удалось скачать обновление: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            TryDelete(tempPath);
            throw new LauncherException($"Не удалось сохранить обновление: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(tempPath);
            throw new LauncherException(
                $"Нет доступа к папке обновлений {_paths.UpdatesDir}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Проверяет обновление и сразу скачивает его. Возвращает null, если обновления нет.
    /// </summary>
    public async Task<LauncherUpdateInfo?> CheckAndDownloadAsync(
        string manifestUrl,
        string currentVersion,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        var info = await CheckAsync(manifestUrl, currentVersion, ct).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        var path = await DownloadAsync(info.Manifest, progress, ct).ConfigureAwait(false);
        return info with { DownloadPath = path };
    }

    /// <summary>Упрощённый вариант проверки и загрузки — без отчёта о прогрессе.</summary>
    public Task<LauncherUpdateInfo?> CheckAndDownloadAsync(
        string manifestUrl,
        string currentVersion,
        CancellationToken ct) =>
        CheckAndDownloadAsync(manifestUrl, currentVersion, null, ct);

    /// <summary>
    /// Запускает Launcher.Updater.exe рядом с текущим exe. Если его нет — создаёт
    /// PowerShell-скрипт, который дождётся завершения текущего процесса (PID), заменит
    /// файл и при необходимости запустит лаунчер снова.
    /// Вызывающий код должен сразу после этого завершить работу лаунчера.
    /// </summary>
    public void ApplyAndRestart(string downloadedExe, bool restart)
    {
        if (string.IsNullOrWhiteSpace(downloadedExe))
        {
            throw new LauncherException("Не указан файл обновления.");
        }

        var source = Path.GetFullPath(downloadedExe);
        if (!File.Exists(source))
        {
            throw new LauncherException($"Файл обновления не найден: {source}");
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            using var current = Process.GetCurrentProcess();
            currentExe = current.MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(currentExe))
        {
            throw new LauncherException("Не удалось определить путь к текущему Launcher.exe.");
        }

        currentExe = Path.GetFullPath(currentExe);

        if (string.Equals(Path.GetFileName(currentExe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                "Автообновление недоступно при запуске через dotnet. Запустите собранный Launcher.exe.");
        }

        if (string.Equals(source, currentExe, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException("Скачанный файл совпадает с текущим Launcher.exe.");
        }

        var targetDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        var updaterPath = Path.Combine(targetDir, "Launcher.Updater.exe");
        var pid = Environment.ProcessId;

        if (File.Exists(updaterPath))
        {
            StartUpdater(updaterPath, targetDir, pid, source, currentExe, restart);
            return;
        }

        StartFallbackScript(source, currentExe, pid, restart);
    }

    private async Task<UpdateManifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(
                    $"Сервер обновлений вернул {(int)response.StatusCode} {response.ReasonPhrase} для {manifestUrl}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
                           ?? throw new LauncherException($"Пустой latest.json: {manifestUrl}");

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                throw new LauncherException($"В latest.json не указана версия: {manifestUrl}");
            }

            if (string.IsNullOrWhiteSpace(manifest.Url))
            {
                throw new LauncherException($"В latest.json не указан url файла обновления: {manifestUrl}");
            }

            if (!IsValidSha256(manifest.Sha256))
            {
                throw new LauncherException(
                    $"В latest.json не указан корректный sha256: {manifestUrl}. " +
                    "Обновление без проверки хеша запрещено.");
            }

            manifest.Url = ResolveUrl(manifestUrl, manifest.Url);

            if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri)
                && manifestUri.Scheme == Uri.UriSchemeHttp)
            {
                Trace.TraceWarning(
                    $"latest.json загружается по HTTP без TLS: {manifestUrl}. " +
                    "Рекомендуется перейти на HTTPS.");
            }

            return manifest;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LauncherException(
                $"Не удалось проверить обновления: истекло время ожидания ({manifestUrl}).",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LauncherException($"Не удалось проверить обновления: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Некорректный latest.json ({manifestUrl}): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException($"Не удалось прочитать latest.json ({manifestUrl}): {ex.Message}", ex);
        }
    }

    private static void ValidateManifestUrl(string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new LauncherException("Не задан адрес latest.json для проверки обновлений.");
        }

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new LauncherException("Адрес latest.json должен быть абсолютным http(s)-URL.");
        }
    }

    private static string ResolveUrl(string manifestUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(new Uri(manifestUrl, UriKind.Absolute), url, out var combined))
        {
            return combined.ToString();
        }

        throw new LauncherException($"Некорректный адрес файла обновления в latest.json: {url}");
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, NormalizeHash(expectedHash), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeHash(string value)
    {
        var hash = value.Trim();
        if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            hash = hash["sha256:".Length..];
        }

        return hash.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static bool IsValidSha256(string? value)
    {
        var hash = NormalizeHash(value ?? "");
        return hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');

        if (result.Length > 80)
        {
            result = result[..80];
        }

        return string.IsNullOrWhiteSpace(result) ? "latest" : result;
    }

    private static void StartUpdater(
        string updaterPath,
        string workingDirectory,
        int pid,
        string source,
        string target,
        bool restart)
    {
        var arguments = new List<string>
        {
            "--pid",
            pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--source",
            source,
            "--target",
            target
        };

        if (restart)
        {
            arguments.Add("--restart");
        }

        try
        {
            using var process = ProcessHost.StartDetachedHidden(
                updaterPath,
                arguments,
                workingDirectory);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException($"Не удалось запустить Launcher.Updater.exe: {ex.Message}", ex);
        }
    }

    private void StartFallbackScript(string source, string target, int pid, bool restart)
    {
        var scriptPath = Path.Combine(_paths.UpdatesDir, $"apply-update-{pid}.ps1");

        try
        {
            Directory.CreateDirectory(_paths.UpdatesDir);
            var script = BuildFallbackScript(source, target, pid, restart);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var arguments = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-WindowStyle",
                "Hidden",
                "-File",
                scriptPath
            };

            using var process = ProcessHost.StartDetachedHidden(
                "powershell.exe",
                arguments,
                _paths.UpdatesDir);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException($"Не удалось запустить скрипт обновления: {ex.Message}", ex);
        }
    }

    private static string BuildFallbackScript(string source, string target, int pid, bool restart)
    {
        var targetDir = Path.GetDirectoryName(target) ?? ".";
        var builder = new StringBuilder();

        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine("$pidToWait = " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("try {");
        builder.AppendLine("    $process = Get-Process -Id $pidToWait -ErrorAction Stop");
        builder.AppendLine("    if (-not $process.WaitForExit(60000)) { Start-Sleep -Seconds 2 }");
        builder.AppendLine("} catch { }");
        builder.AppendLine("Start-Sleep -Milliseconds 500");
        builder.AppendLine("$source = '" + EscapeSingleQuotes(source) + "'");
        builder.AppendLine("$target = '" + EscapeSingleQuotes(target) + "'");
        builder.AppendLine("if (Test-Path -LiteralPath $target) {");
        builder.AppendLine("    Copy-Item -LiteralPath $target -Destination ($target + '.old') -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("}");
        builder.AppendLine("$copied = $false");
        builder.AppendLine("for ($i = 0; $i -lt 30; $i++) {");
        builder.AppendLine("    try {");
        builder.AppendLine("        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop");
        builder.AppendLine("        $copied = $true");
        builder.AppendLine("        break");
        builder.AppendLine("    } catch {");
        builder.AppendLine("        Start-Sleep -Milliseconds 500");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine("if ($copied) {");

        if (restart)
        {
            builder.AppendLine(
                "    Start-Process -FilePath $target -WorkingDirectory '" +
                EscapeSingleQuotes(targetDir) + "'");
        }

        builder.AppendLine("    Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue");
        builder.AppendLine("}");
        builder.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");

        return builder.ToString();
    }

    private static string EscapeSingleQuotes(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

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
            // Временный файл мог остаться — при следующем запуске он будет перезаписан.
        }
        catch (UnauthorizedAccessException)
        {
            // То же самое: не мешаем основной ошибке.
        }
    }
}

/// <summary>Результат проверки обновления лаунчера.</summary>
public sealed record LauncherUpdateInfo
{
    public string Version { get; set; } = "";

    public string Notes { get; set; } = "";

    public bool Mandatory { get; set; }

    /// <summary>Путь к скачанному exe; null, если обновление ещё не скачано.</summary>
    public string? DownloadPath { get; set; }

    /// <summary>Исходный манифест с уже разрешённым абсолютным url.</summary>
    public UpdateManifest Manifest { get; set; } = new();
}
