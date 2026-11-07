using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamaraLauncher.Core.Backups;
using SamaraLauncher.Core.Integrity;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Packwiz;

/// <summary>
/// Обновление сборки через packwiz-installer-bootstrap.jar с бэкапом и защитой патчей.
/// Перед обновлением дешёво проверяется pack.toml: если он не изменился, бэкап и
/// packwiz не запускаются (иначе каждое «Играть» занимало бы минуты).
/// </summary>
public sealed class PackwizService
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);
    private const int OutputTailLines = 40;
    private const int MaxPackTomlBytes = 4 * 1024 * 1024;
    private const string PackStateFileName = ".samara-pack-state.json";

    private static readonly HttpClient ManifestHttpClient = CreateManifestHttpClient();

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<InstanceProfile, string> _javaPathProvider;
    private readonly ToolLocator _toolLocator;
    private readonly BackupService _backups;
    private readonly IntegrityService _integrity;
    private readonly int _backupRetention;

    public PackwizService(
        IJavaLocator javaLocator,
        ToolLocator toolLocator,
        BackupService backups,
        IntegrityService integrity,
        int backupRetention = BackupService.DefaultRetention)
        : this(
            instance => javaLocator.Find(instance).Path,
            toolLocator,
            backups,
            integrity,
            backupRetention)
    {
        ArgumentNullException.ThrowIfNull(javaLocator);
    }

    public PackwizService(
        Func<string> javaPath,
        ToolLocator toolLocator,
        BackupService backups,
        IntegrityService integrity,
        int backupRetention = BackupService.DefaultRetention)
        : this(
            _ => javaPath(),
            toolLocator,
            backups,
            integrity,
            backupRetention)
    {
        ArgumentNullException.ThrowIfNull(javaPath);
    }

    public PackwizService(
        Func<InstanceProfile, string> javaPath,
        ToolLocator toolLocator,
        BackupService backups,
        IntegrityService integrity,
        int backupRetention = BackupService.DefaultRetention)
    {
        _javaPathProvider = javaPath ?? throw new ArgumentNullException(nameof(javaPath));
        _toolLocator = toolLocator ?? throw new ArgumentNullException(nameof(toolLocator));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _backupRetention = Math.Max(1, backupRetention);
    }

    /// <summary>
    /// Обновляет сборку. Если <paramref name="force"/> = false и pack.toml не изменился,
    /// возвращает успешный результат без бэкапа и запуска packwiz.
    /// </summary>
    public async Task<PackUpdateResult> UpdateAsync(
        InstanceProfile instance,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default,
        bool force = false)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан инстанс для обновления сборки.");
        }

        var gameDir = GetGameDirectory(instance);
        var packUri = ResolvePackUri(instance, gameDir);

        var storedState = LoadPackState(gameDir);
        var manifest = await TryFetchPackManifestAsync(packUri, storedState, ct).ConfigureAwait(false);

        if (!force && IsUnchanged(manifest, storedState, packUri))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Done,
                "Сборка уже актуальна: pack.toml не изменился."));

            return new PackUpdateResult
            {
                Success = true,
                Changed = false,
                Output = "pack.toml не изменился — обновление и бэкап не требуются."
            };
        }

        var bootstrap = _toolLocator.FindPackwizBootstrap();
        var javaPath = ResolveJavaPath(instance);

        var result = new PackUpdateResult();
        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка обновления сборки {instance.Name}..."));

        var before = await Task.Run(() => Snapshot(gameDir), ct).ConfigureAwait(false);

        var backupPath = await _backups.CreateBackupAsync(
            gameDir,
            instance.Id,
            _backupRetention,
            progress,
            ct).ConfigureAwait(false);

        result.BackupPath = backupPath;
        if (backupPath is null)
        {
            result.Warnings.Add(
                "Папки mods/config/defaultconfigs не найдены — бэкап перед обновлением не создан.");
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Pack,
            "Запуск packwiz-installer..."));

        var (exitCode, output) = await RunPackwizAsync(
            javaPath,
            bootstrap,
            packUri,
            gameDir,
            progress,
            ct).ConfigureAwait(false);

        result.Output = output;

        if (exitCode != 0)
        {
            var tail = Tail(output, OutputTailLines);
            var message = string.IsNullOrWhiteSpace(tail)
                ? $"packwiz завершился с кодом {exitCode}."
                : $"packwiz завершился с кодом {exitCode}. Хвост вывода:\n{tail}";

            result.Success = false;
            result.Warnings.Add(message);

            progress?.Report(new LauncherProgress(
                ProgressStage.Error,
                message));

            return result;
        }

        // Возвращаем локальные патчи, которые packwiz мог перезаписать.
        if (backupPath is not null)
        {
            try
            {
                var restored = await _integrity.RestoreProtectedAsync(
                    gameDir,
                    backupPath,
                    progress,
                    ct).ConfigureAwait(false);

                result.RestoredProtectedFiles.AddRange(restored);
            }
            catch (LauncherException ex)
            {
                result.Warnings.Add($"Не удалось вернуть защищённые файлы: {ex.Message}");
            }
        }
        else
        {
            var protectedManifest = ProtectedFilesManifest.Load(gameDir);
            if (protectedManifest.Files.Count > 0)
            {
                result.Warnings.Add(
                    "Бэкап не создан, поэтому защищённые файлы не восстановлены после обновления.");
            }
        }

        var after = await Task.Run(() => Snapshot(gameDir), ct).ConfigureAwait(false);
        result.ChangedFiles = DiffSnapshots(before, after);

        foreach (var file in result.RestoredProtectedFiles)
        {
            if (!result.ChangedFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                result.ChangedFiles.Add(file);
            }
        }

        result.Changed = result.ChangedFiles.Count > 0;
        result.Success = true;

        await SavePackStateAsync(gameDir, packUri, manifest, storedState, ct).ConfigureAwait(false);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            result.Changed
                ? $"Сборка обновлена, изменено файлов: {result.ChangedFiles.Count}."
                : "Сборка уже актуальна.",
            result.ChangedFiles.Count,
            result.ChangedFiles.Count));

        return result;
    }

    /// <summary>
    /// Дешёвая проверка обновления по содержимому pack.toml (ETag/Last-Modified + SHA-256).
    /// При любой ошибке сети/разбора возвращает true: лучше обновиться, чем навсегда
    /// застрять на старой сборке.
    /// </summary>
    public async Task<bool> IsUpdateAvailableAsync(
        InstanceProfile instance,
        CancellationToken ct = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан инстанс для проверки обновления.");
        }

        ct.ThrowIfCancellationRequested();
        var gameDir = GetGameDirectory(instance);

        if (string.IsNullOrWhiteSpace(instance.PackUrl))
        {
            return false;
        }

        string packUri;
        try
        {
            packUri = ResolvePackUri(instance, gameDir);
        }
        catch (LauncherException)
        {
            // Некорректный PackUrl должен всплыть в UpdateAsync с понятным текстом.
            return true;
        }

        var storedState = LoadPackState(gameDir);
        var manifest = await TryFetchPackManifestAsync(packUri, storedState, ct).ConfigureAwait(false);

        if (manifest is null)
        {
            return true;
        }

        if (manifest.NotModified)
        {
            return false;
        }

        return !IsSameState(manifest, storedState, packUri);
    }

    /// <summary>Сырой запуск bootstrap.jar с произвольными аргументами (для CLI).</summary>
    public async Task<string> RunRawAsync(
        string gameDir,
        string args,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для запуска packwiz.");
        }

        var root = Path.GetFullPath(gameDir);
        if (!Directory.Exists(root))
        {
            throw new LauncherException($"Папка игры не найдена: {root}");
        }

        var bootstrap = _toolLocator.FindPackwizBootstrap();
        var javaPath = ResolveJavaPath(new InstanceProfile { GameDir = root, Id = "raw" });
        var rawArguments = $"-jar \"{bootstrap}\" {args}".TrimEnd();
        var lastReport = 0L;

        var result = await ProcessHost.RunRawAsync(
            javaPath,
            rawArguments,
            root,
            UpdateTimeout,
            ct,
            line =>
            {
                var now = Environment.TickCount64;
                if (now - lastReport < 150)
                {
                    return;
                }

                lastReport = now;
                progress?.Report(new LauncherProgress(ProgressStage.Pack, line));
            },
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                $"Обновление сборки не завершилось за {UpdateTimeout.TotalMinutes:0} минут и было остановлено.");
        }

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"packwiz завершился с кодом {result.ExitCode}:\n{Tail(result.Output, OutputTailLines)}");
        }

        return result.Output;
    }

    private async Task<(int ExitCode, string Output)> RunPackwizAsync(
        string javaPath,
        string bootstrap,
        string packUri,
        string gameDir,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        var arguments = new[] { "-jar", bootstrap, "-g", packUri };
        var lastReport = 0L;

        var result = await ProcessHost.RunAsync(
            javaPath,
            arguments,
            gameDir,
            UpdateTimeout,
            ct,
            line =>
            {
                var now = Environment.TickCount64;

                // Тысячи строк packwiz не должны забивать очередь UI прогрессом.
                if (now - lastReport < 150
                    && !line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("exception", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                lastReport = now;
                progress?.Report(new LauncherProgress(ProgressStage.Pack, line));
            },
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                $"Обновление сборки не завершилось за {UpdateTimeout.TotalMinutes:0} минут и было остановлено.");
        }

        return (result.ExitCode, result.Output);
    }

    private string ResolveJavaPath(InstanceProfile instance)
    {
        string? path;

        try
        {
            path = _javaPathProvider(instance);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LauncherException(
                $"Не удалось определить Java для packwiz: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LauncherException(
                "Не удалось определить путь к Java для обновления сборки.");
        }

        return path.Trim().Trim('"');
    }

    private static string ResolvePackUri(InstanceProfile instance, string gameDir)
    {
        var raw = (instance.PackUrl ?? "").Trim().Trim('"');
        if (raw.Length == 0)
        {
            throw new LauncherException(
                "У инстанса не указан PackUrl (URL или путь к pack.toml).");
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                return uri.AbsoluteUri;
            }

            if (uri.IsFile)
            {
                var localPath = uri.LocalPath;
                if (Directory.Exists(localPath))
                {
                    localPath = Path.Combine(localPath, "pack.toml");
                }

                if (!File.Exists(localPath))
                {
                    throw new LauncherException($"Файл pack.toml не найден: {localPath}");
                }

                return new Uri(Path.GetFullPath(localPath)).AbsoluteUri;
            }

            if (raw.Contains("://", StringComparison.Ordinal))
            {
                throw new LauncherException(
                    $"Неподдерживаемый адрес pack.toml: {uri.Scheme}. Нужен http/https или локальный путь.");
            }
        }

        var path = Environment.ExpandEnvironmentVariables(raw);
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(gameDir, path);
        }

        path = Path.GetFullPath(path);

        if (Directory.Exists(path))
        {
            path = Path.Combine(path, "pack.toml");
        }

        if (!File.Exists(path))
        {
            throw new LauncherException(
                $"Файл pack.toml не найден: {path}. Укажите URL или локальный путь.");
        }

        return new Uri(path).AbsoluteUri;
    }

    private static string GetGameDirectory(InstanceProfile instance)
    {
        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        var gameDir = Path.GetFullPath(instance.GameDir);
        if (!Directory.Exists(gameDir))
        {
            throw new LauncherException($"Папка игры не найдена: {gameDir}");
        }

        return gameDir;
    }

    private static HttpClient CreateManifestHttpClient()
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
            Timeout = ManifestTimeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");
        return client;
    }

    private static async Task<PackManifestSnapshot?> TryFetchPackManifestAsync(
        string packUri,
        PackState? storedState,
        CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(packUri, UriKind.Absolute, out var uri))
            {
                return null;
            }

            if (uri.IsFile)
            {
                var path = uri.LocalPath;
                if (!File.Exists(path))
                {
                    return null;
                }

                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                return new PackManifestSnapshot(Hash(bytes), "", "", false);
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true
            };

            if (storedState is not null
                && string.Equals(storedState.PackUri, packUri, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(storedState.ETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", storedState.ETag);
                }

                if (!string.IsNullOrWhiteSpace(storedState.LastModified)
                    && DateTimeOffset.TryParse(
                        storedState.LastModified,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var lastModified))
                {
                    request.Headers.IfModifiedSince = lastModified;
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ManifestTimeout);

            using var response = await ManifestHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && storedState is not null)
            {
                return new PackManifestSnapshot(
                    storedState.Sha256,
                    storedState.ETag,
                    storedState.LastModified,
                    NotModified: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);

            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxPackTomlBytes)
                {
                    return null;
                }

                memory.Write(buffer, 0, read);
            }

            var etag = response.Headers.ETag?.ToString() ?? "";
            var modified = response.Content.Headers.LastModified?.ToString("O") ?? "";

            return new PackManifestSnapshot(
                Hash(memory.ToArray()),
                etag,
                modified,
                NotModified: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or NotSupportedException)
        {
            // Проверка не должна блокировать запуск: вызывающий решит обновиться.
            return null;
        }
    }

    private static bool IsUnchanged(
        PackManifestSnapshot? manifest,
        PackState? storedState,
        string packUri) =>
        manifest is not null && storedState is not null && IsSameState(manifest, storedState, packUri);

    private static bool IsSameState(
        PackManifestSnapshot manifest,
        PackState? storedState,
        string packUri) =>
        storedState is not null
        && string.Equals(storedState.PackUri, packUri, StringComparison.OrdinalIgnoreCase)
        && string.Equals(storedState.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task SavePackStateAsync(
        string gameDir,
        string packUri,
        PackManifestSnapshot? manifest,
        PackState? storedState,
        CancellationToken ct)
    {
        try
        {
            var snapshot = manifest;

            if (snapshot is null || snapshot.NotModified)
            {
                snapshot = await TryFetchPackManifestAsync(packUri, storedState, ct).ConfigureAwait(false);
            }

            if (snapshot is null || snapshot.NotModified || string.IsNullOrWhiteSpace(snapshot.Sha256))
            {
                return;
            }

            var state = new PackState
            {
                PackUri = packUri,
                Sha256 = snapshot.Sha256,
                ETag = snapshot.ETag,
                LastModified = snapshot.LastModified,
                CheckedUtc = DateTimeOffset.UtcNow
            };

            var path = Path.Combine(gameDir, PackStateFileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var json = JsonSerializer.Serialize(state, StateJsonOptions);
                await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Состояние — оптимизация; его отсутствие лишь означает следующий update.
        }
    }

    private static PackState? LoadPackState(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, PackStateFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<PackState>(json, StateJsonOptions);
            return string.IsNullOrWhiteSpace(state?.Sha256) ? null : state;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Dictionary<string, FileStamp> Snapshot(string gameDir)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        foreach (var folderName in new[] { "mods", "config", "defaultconfigs" })
        {
            var folder = Path.Combine(gameDir, folderName);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                var relative = Path
                    .GetRelativePath(gameDir, path)
                    .Replace('\\', '/');

                result[relative] = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            }
        }

        return result;
    }

    private static List<string> DiffSnapshots(
        Dictionary<string, FileStamp> before,
        Dictionary<string, FileStamp> after)
    {
        var changed = new List<string>();

        foreach (var (path, stamp) in after)
        {
            if (!before.TryGetValue(path, out var old)
                || old.Size != stamp.Size
                || old.LastWriteTicks != stamp.LastWriteTicks)
            {
                changed.Add(path);
            }
        }

        foreach (var path in before.Keys)
        {
            if (!after.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        changed.Sort(StringComparer.OrdinalIgnoreCase);
        return changed;
    }

    private static string Tail(string output, int lines)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "";
        }

        var all = output
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }

    private static void TryDeleteFile(string path)
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
            // То же самое.
        }
    }

    private sealed record FileStamp(long Size, long LastWriteTicks);

    private sealed record PackManifestSnapshot(
        string Sha256,
        string ETag,
        string LastModified,
        bool NotModified);

    private sealed class PackState
    {
        public string PackUri { get; set; } = "";

        public string Sha256 { get; set; } = "";

        public string ETag { get; set; } = "";

        public string LastModified { get; set; } = "";

        public DateTimeOffset CheckedUtc { get; set; }
    }
}
