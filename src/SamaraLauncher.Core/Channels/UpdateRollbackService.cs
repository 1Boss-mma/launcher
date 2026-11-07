using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;

namespace SamaraLauncher.Core.Channels;

/// <summary>
/// Хранит предыдущий Launcher.exe и восстанавливает его при откате.
/// Процессы не запускает и работающий exe не заменяет.
/// </summary>
public sealed class UpdateRollbackService
{
    private const int BufferSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public UpdateRollbackService(LauncherPaths? paths = null)
    {
        var launcherPaths = paths ?? new LauncherPaths();
        RollbackRoot = Path.Combine(launcherPaths.UpdatesDir, "rollback");
    }

    /// <summary>Каталог резервных копий: updates\rollback.</summary>
    public string RollbackRoot { get; }

    /// <summary>Файл с описанием последней резервной копии.</summary>
    public string StatePath => Path.Combine(RollbackRoot, "rollback.json");

    /// <summary>
    /// Сохраняет текущий Launcher.exe в
    /// <c>updates\rollback\Launcher-&lt;version&gt;.exe</c> и пишет rollback.json.
    /// </summary>
    public async Task SaveCurrentAsync(
        string launcherExePath,
        string version,
        UpdateChannel channel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(launcherExePath))
        {
            throw new LauncherException(
                "Не указан путь к текущему Launcher.exe для отката.");
        }

        var source = GetFullPathOrThrow(launcherExePath, "к текущему Launcher.exe");
        if (!File.Exists(source))
        {
            throw new LauncherException($"Текущий Launcher.exe не найден: {source}");
        }

        if (!SemVer.TryParse(version, out var parsedVersion))
        {
            throw new LauncherException(
                $"Некорректная версия лаунчера для отката: \"{version}\".");
        }

        var normalizedVersion = parsedVersion.ToString();
        var backupPath = GetBackupPath(normalizedVersion);

        if (IsSamePath(source, backupPath))
        {
            throw new LauncherException(
                "Путь к текущему Launcher.exe совпадает с путём резервной копии.");
        }

        ct.ThrowIfCancellationRequested();

        var tempPath = backupPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(RollbackRoot);
            await CopyToFileAsync(source, tempPath, ct).ConfigureAwait(false);

            var sha256 = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            File.Move(tempPath, backupPath, overwrite: true);

            var state = new RollbackState
            {
                Version = normalizedVersion,
                Channel = UpdateChannels.ToString(channel),
                SavedAt = DateTimeOffset.UtcNow,
                Sha256 = sha256
            };

            await WriteStateAsync(state, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить резервную копию Launcher.exe: {ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Возвращает путь к последней резервной копии или null, если откат не сохранён.
    /// </summary>
    public async Task<string?> GetRollbackAsync(CancellationToken ct = default)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var backupPath = GetBackupPath(state.Version);
        if (!File.Exists(backupPath))
        {
            throw new LauncherException(
                $"Резервная копия Launcher.exe не найдена: {backupPath}");
        }

        return backupPath;
    }

    /// <summary>
    /// Восстанавливает Launcher.exe из последней резервной копии. Текущий exe
    /// предварительно копируется в <c>rollback\previous</c>. Возвращает путь назначения.
    /// </summary>
    public async Task<string> RestoreAsync(
        string targetLauncherExePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetLauncherExePath))
        {
            throw new LauncherException(
                "Не указан путь, куда восстанавливать Launcher.exe.");
        }

        var target = GetFullPathOrThrow(targetLauncherExePath, "к Launcher.exe");
        var state = await ReadStateAsync(ct).ConfigureAwait(false)
            ?? throw new LauncherException("Резервная копия для отката не найдена.");

        var backupPath = GetBackupPath(state.Version);
        if (!File.Exists(backupPath))
        {
            throw new LauncherException(
                $"Резервная копия Launcher.exe не найдена: {backupPath}");
        }

        if (IsSamePath(target, backupPath))
        {
            throw new LauncherException(
                "Путь назначения совпадает с путём резервной копии.");
        }

        EnsureNotRunning(target);

        bool backupValid;
        try
        {
            backupValid = await DownloadService.IsFileValidAsync(
                    backupPath,
                    sha256: state.Sha256,
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
                $"Не удалось проверить резервную копию Launcher.exe: {ex.Message}",
                ex);
        }

        if (!backupValid)
        {
            throw new LauncherException(
                $"Резервная копия Launcher.exe повреждена (не совпал sha256): {backupPath}");
        }

        var targetDirectory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            try
            {
                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку для Launcher.exe {targetDirectory}: {ex.Message}",
                    ex);
            }
        }

        if (File.Exists(target))
        {
            try
            {
                await BackupCurrentAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LauncherException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось сохранить текущий Launcher.exe перед откатом: {ex.Message}",
                    ex);
            }
        }

        var tempPath = target + ".rollback-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await CopyToFileAsync(backupPath, tempPath, ct).ConfigureAwait(false);

            if (!await DownloadService.IsFileValidAsync(
                    tempPath,
                    sha256: state.Sha256,
                    cancellationToken: ct).ConfigureAwait(false))
            {
                throw new LauncherException(
                    "Скопированная резервная копия не прошла проверку sha256.");
            }

            File.Move(tempPath, target, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось восстановить Launcher.exe из резервной копии: {ex.Message}. " +
                "Если лаунчер запущен, закройте его и повторите откат.",
                ex);
        }
        finally
        {
            TryDelete(tempPath);
        }

        return target;
    }

    private async Task<string> BackupCurrentAsync(string target, CancellationToken ct)
    {
        var directory = Path.Combine(RollbackRoot, "previous");
        Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"Launcher-{stamp}.exe");

        if (File.Exists(path))
        {
            path = Path.Combine(
                directory,
                $"Launcher-{stamp}-{Guid.NewGuid():N}.exe");
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await CopyToFileAsync(target, tempPath, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: false);
        }
        finally
        {
            TryDelete(tempPath);
        }

        return path;
    }

    private async Task<RollbackState?> ReadStateAsync(CancellationToken ct)
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(StatePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось прочитать rollback.json: {ex.Message}",
                ex);
        }

        RollbackState? state;
        try
        {
            state = JsonSerializer.Deserialize<RollbackState>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LauncherException(
                $"Файл rollback.json повреждён: {ex.Message}",
                ex);
        }

        if (state is null)
        {
            throw new LauncherException("Файл rollback.json пуст или повреждён.");
        }

        ValidateState(state);
        return state;
    }

    private async Task WriteStateAsync(RollbackState state, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tempPath = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(
                    tempPath,
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    ct)
                .ConfigureAwait(false);

            File.Move(tempPath, StatePath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void ValidateState(RollbackState state)
    {
        if (!SemVer.TryParse(state.Version, out var version))
        {
            throw new LauncherException(
                $"В rollback.json некорректная версия: \"{state.Version}\".");
        }

        state.Version = version.ToString();

        if (!UpdateChannels.TryParse(state.Channel, out var channel))
        {
            throw new LauncherException(
                $"В rollback.json неизвестный канал: \"{state.Channel}\".");
        }

        state.Channel = UpdateChannels.ToString(channel);

        if (state.SavedAt == default)
        {
            throw new LauncherException(
                "В rollback.json не указано время сохранения (savedAt).");
        }

        var sha256 = state.Sha256 ?? "";
        if (sha256.Length != 64 || !sha256.All(IsHex))
        {
            throw new LauncherException("В rollback.json некорректный sha256.");
        }

        state.Sha256 = sha256.ToLowerInvariant();
    }

    private string GetBackupPath(string version) =>
        Path.Combine(RollbackRoot, $"Launcher-{version}.exe");

    private static void EnsureNotRunning(string target)
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(current) && IsSamePath(current, target))
        {
            throw new LauncherException(
                "Нельзя заменить работающий Launcher.exe. " +
                "Закройте лаунчер и повторите откат.");
        }
    }

    private static string GetFullPathOrThrow(string path, string description)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Некорректный путь {description}: {path}",
                ex);
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task CopyToFileAsync(
        string source,
        string destination,
        CancellationToken ct)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await input.CopyToAsync(output, BufferSize, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

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
            // То же: не заслоняем основную ошибку.
        }
    }

    private sealed class RollbackState
    {
        public string Version { get; set; } = "";

        public string Channel { get; set; } = "";

        public DateTimeOffset SavedAt { get; set; }

        public string Sha256 { get; set; } = "";
    }
}
