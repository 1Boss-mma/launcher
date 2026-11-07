using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Networking;
using SamaraLauncher.Core.Tools;
using JavaLocatorImpl = SamaraLauncher.Core.Java.JavaLocator;

namespace SamaraLauncher.Core.ServerHost;

/// <summary>
/// Установка, настройка и запуск собственного Forge-сервера Minecraft 1.20.1.
/// </summary>
public sealed class ServerHostService
{
    public const string MinecraftVersion = "1.20.1";
    public const string ForgeVersion = "47.4.28";
    public const string ForgeVersionId = "1.20.1-forge-47.4.28";

    public const string InstallerUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/" +
        "forge-1.20.1-47.4.28-installer.jar";

    private const int RequiredJavaMajor = 17;
    private const int MinMemoryMb = 512;
    private const int MaxMemoryMb = 32768;
    private const int OutputTailLines = 40;
    private const long MinInstallerBytes = 1024 * 1024;
    private const string ForgeArgsRelativePath =
        "libraries/net/minecraftforge/forge/1.20.1-47.4.28/win_args.txt";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PublicIpTimeout = TimeSpan.FromSeconds(3);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly JavaManager _java;
    private readonly ToolLocator _tools;
    private readonly DownloadService _downloads;

    public ServerHostService(
        LauncherPaths paths,
        JavaManager java,
        ToolLocator tools,
        DownloadService? downloads = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _downloads = downloads ?? DownloadService.Shared;
    }

    /// <summary>
    /// Проверяет папку сервера: Forge, EULA, моды, миры и занятость порта.
    /// Не бросает исключений — ошибки складываются в <see cref="ServerInspection.Error"/>.
    /// </summary>
    public ServerInspection Inspect(HostedServerProfile profile)
    {
        if (!TryResolveServerDirectory(profile, out var serverDir, out var pathError))
        {
            return EmptyInspection(pathError);
        }

        if (!Directory.Exists(serverDir))
        {
            return EmptyInspection($"Папка сервера не найдена: {serverDir}");
        }

        var errors = new List<string>();
        var (forgeInstalled, forgeVersion) = FindForgeInstall(serverDir);

        var eulaAccepted = false;

        try
        {
            eulaAccepted = ServerProperties
                .Load(Path.Combine(serverDir, "eula.txt"))
                .GetBool("eula");
        }
        catch (LauncherException ex)
        {
            errors.Add(ex.Message);
        }

        var levelName = ServerWorldService.DefaultWorldName;

        try
        {
            var configured = ServerProperties
                .Load(Path.Combine(serverDir, "server.properties"))
                .Get("level-name");

            if (!string.IsNullOrWhiteSpace(configured))
            {
                levelName = configured.Trim();
            }
        }
        catch (LauncherException ex)
        {
            errors.Add(ex.Message);
        }

        var modCount = 0;
        var modsDir = Path.Combine(serverDir, "mods");

        if (Directory.Exists(modsDir))
        {
            try
            {
                modCount = Directory
                    .EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                    .Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Не удалось прочитать папку mods: {ex.Message}");
            }
        }

        var worlds = new List<string>();

        try
        {
            foreach (var worldDir in Directory.EnumerateDirectories(serverDir))
            {
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    worlds.Add(Path.GetFileName(worldDir));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Не удалось прочитать миры: {ex.Message}");
        }

        worlds.Sort(StringComparer.OrdinalIgnoreCase);

        var portInUse = false;

        if (profile.Port is >= 1 and <= 65535)
        {
            portInUse = IsPortInUse(profile.Port);
        }
        else
        {
            errors.Add($"Некорректный порт сервера: {profile.Port}.");
        }

        return new ServerInspection(
            DirectoryExists: true,
            ForgeInstalled: forgeInstalled,
            EulaAccepted: eulaAccepted,
            ModCount: modCount,
            Worlds: worlds,
            PortInUse: portInUse,
            InstalledForgeVersion: forgeVersion,
            LevelName: levelName,
            Error: errors.Count == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>
    /// Подбирает Java 17+ для сервера: явный путь, runtime сервера, tools рядом
    /// с сервером, системные Java. Если ничего не найдено — скачивает Temurin 17.
    /// </summary>
    public async Task<JavaInfo> ResolveJavaAsync(
        HostedServerProfile profile,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        var serverDir = ResolveServerDirectory(profile);
        ct.ThrowIfCancellationRequested();

        if (!IsAutoJava(profile.JavaPath))
        {
            var explicitInfo = JavaLocatorImpl.TryProbe(profile.JavaPath);

            if (explicitInfo is null)
            {
                throw new LauncherException(
                    $"Указанная Java не найдена или не запускается: {profile.JavaPath}. " +
                    "Укажите путь к java.exe или \"auto\".");
            }

            if (explicitInfo.MajorVersion < RequiredJavaMajor)
            {
                throw new LauncherException(
                    $"Указана Java {explicitInfo.Version}, но для Minecraft {MinecraftVersion} " +
                    $"нужна Java {RequiredJavaMajor} или новее. Путь: {explicitInfo.Path}");
            }

            return explicitInfo;
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Java,
            $"Поиск Java {RequiredJavaMajor}+ для сервера..."));

        var candidates = new List<JavaInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Переносимая раскладка: <root>\server и <root>\tools\jdk21.
        AddJavaFromDirectory(Path.Combine(serverDir, "runtime"), candidates, seen);
        AddJavaFromDirectory(Path.Combine(serverDir, "..", "tools"), candidates, seen);

        foreach (var info in _java.Discover())
        {
            if (info.MajorVersion >= RequiredJavaMajor && seen.Add(info.Path))
            {
                candidates.Add(info);
            }
        }

        var best = candidates
            .OrderBy(info => info.MajorVersion)
            .ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (best is not null)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Найдена Java {best.Version}: {best.Path}"));

            return best;
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Java,
            $"Java {RequiredJavaMajor}+ не найдена — скачивание Eclipse Temurin 17"));

        var installed = await _java
            .EnsureAsync(
                new InstanceProfile
                {
                    GameDir = serverDir,
                    JavaPath = profile.JavaPath
                },
                RequiredJavaMajor,
                progress,
                ct)
            .ConfigureAwait(false);

        if (installed.MajorVersion < RequiredJavaMajor)
        {
            throw new LauncherException(
                $"После установки найдена Java {installed.Version}, но нужна Java " +
                $"{RequiredJavaMajor}+. Путь: {installed.Path}");
        }

        return installed;
    }

    /// <summary>
    /// Устанавливает Forge-сервер, принимает EULA (только при явном согласии),
    /// применяет server.properties и при необходимости обновляет сборку.
    /// </summary>
    public async Task InstallAsync(
        HostedServerProfile profile,
        bool acceptEula,
        bool updatePack = true,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        var serverDir = ResolveServerDirectory(profile);

        try
        {
            Directory.CreateDirectory(serverDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку сервера {serverDir}: {ex.Message}", ex);
        }

        profile.ServerDir = serverDir;

        if (!acceptEula)
        {
            throw new LauncherException(
                "Установка сервера остановлена: нужно явно принять EULA Minecraft " +
                "(https://aka.ms/MinecraftEULA). Лаунчер не принимает её автоматически.");
        }

        ct.ThrowIfCancellationRequested();

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка сервера {profile.Name}..."));

        var java = await ResolveJavaAsync(profile, progress, ct).ConfigureAwait(false);

        if (FindForgeInstall(serverDir).Installed)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Forge {ForgeVersion} уже установлен"));
        }
        else
        {
            await InstallForgeAsync(serverDir, java, progress, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        // eula.txt пишется только после явного acceptEula=true.
        var eulaPath = Path.Combine(serverDir, "eula.txt");
        var eula = ServerProperties.Load(eulaPath);
        eula.Set("eula", true);
        eula.Save(eulaPath);

        ApplySettings(profile);

        if (updatePack && !string.IsNullOrWhiteSpace(profile.PackUrl))
        {
            await UpdatePackAsync(profile, progress, ct).ConfigureAwait(false);
        }

        profile.LastInstalledAt = DateTimeOffset.UtcNow;

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Сервер {profile.Name} готов к запуску."));
    }

    /// <summary>
    /// Обновляет серверную сборку через packwiz-installer-bootstrap.jar.
    /// Серверный packwiz ставит только both/server-моды.
    /// </summary>
    public async Task UpdatePackAsync(
        HostedServerProfile profile,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        if (string.IsNullOrWhiteSpace(profile.PackUrl))
        {
            throw new LauncherException(
                "У сервера не указан PackUrl (URL или путь к pack.toml).");
        }

        var serverDir = ResolveServerDirectory(profile);

        if (!Directory.Exists(serverDir))
        {
            throw new LauncherException($"Папка сервера не найдена: {serverDir}");
        }

        var java = await ResolveJavaAsync(profile, progress, ct).ConfigureAwait(false);
        var bootstrap = _tools.FindPackwizBootstrap();

        progress?.Report(new LauncherProgress(
            ProgressStage.Pack,
            "Обновление серверной сборки через packwiz..."));

        var result = await ProcessHost.RunAsync(
            java.Path,
            new[] { "-jar", bootstrap, "-g", "-s", "server", profile.PackUrl.Trim().Trim('"') },
            serverDir,
            InstallTimeout,
            ct,
            onLine: line => progress?.Report(new LauncherProgress(ProgressStage.Pack, line)),
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                $"Обновление серверной сборки не завершилось за " +
                $"{InstallTimeout.TotalMinutes:0} минут и было остановлено.");
        }

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"packwiz завершился с кодом {result.ExitCode}.{Environment.NewLine}" +
                Tail(result.Output));
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Pack,
            "Серверная сборка обновлена"));
    }

    /// <summary>
    /// Запускает сервер. Лог по умолчанию:
    /// &lt;serverDir&gt;\logs\launcher-server-&lt;timestamp&gt;.log.
    /// </summary>
    public ServerProcess Start(HostedServerProfile profile, JavaInfo java, string? logPath = null)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        if (java is null || string.IsNullOrWhiteSpace(java.Path))
        {
            throw new LauncherException("Java для сервера не найдена.");
        }

        var serverDir = ResolveServerDirectory(profile);

        if (!Directory.Exists(serverDir))
        {
            throw new LauncherException($"Папка сервера не найдена: {serverDir}");
        }

        var inspection = Inspect(profile);

        if (!inspection.ForgeInstalled)
        {
            throw new LauncherException(
                $"Forge {ForgeVersion} не установлен в {serverDir}. " +
                "Установите сервер перед запуском.");
        }

        if (!inspection.EulaAccepted)
        {
            throw new LauncherException(
                "EULA Minecraft не принята. Примите EULA и установите сервер заново.");
        }

        if (inspection.PortInUse)
        {
            throw new LauncherException(
                $"Порт {profile.Port} уже занят. Закройте другой сервер или выберите другой порт.");
        }

        if (!File.Exists(java.Path))
        {
            throw new LauncherException($"Java не найдена: {java.Path}");
        }

        var arguments = BuildStartArguments(profile);

        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = Path.Combine(
                serverDir,
                "logs",
                $"launcher-server-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }

        var fullLogPath = Path.GetFullPath(logPath);
        var logDirectory = Path.GetDirectoryName(fullLogPath);

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку логов: {logDirectory}", ex);
            }
        }

        NormalizeMemory(profile, out var minMemory, out var maxMemory);

        try
        {
            var header = string.Join(Environment.NewLine, new[]
            {
                $"=== Лаунчер · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Сервер:   {profile.Id} ({profile.Name})",
                $"Java:     {java.Path}",
                $"Версия:   {ForgeVersionId}",
                $"Память:   {minMemory}–{maxMemory} МБ",
                $"Порт:     {profile.Port}",
                ""
            });

            using var stream = new FileStream(fullLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(header + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось записать лог запуска: {fullLogPath}", ex);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = java.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = serverDir
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ServerProcess process;

        try
        {
            process = new ServerProcess(startInfo, fullLogPath);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidOperationException)
        {
            throw new LauncherException($"Не удалось запустить сервер: {ex.Message}", ex);
        }

        profile.LastStartedAt = DateTimeOffset.UtcNow;
        return process;
    }

    /// <summary>
    /// Аргументы Java для запуска сервера. Память клампится в 512..32768 МБ,
    /// минимальная не превышает максимальную.
    /// </summary>
    public static IReadOnlyList<string> BuildStartArguments(HostedServerProfile profile)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        ValidatePort(profile.Port);
        NormalizeMemory(profile, out var minMemory, out var maxMemory);

        return new[]
        {
            $"-Xms{minMemory}M",
            $"-Xmx{maxMemory}M",
            "-Dfile.encoding=UTF-8",
            "@" + ForgeArgsRelativePath,
            "nogui"
        };
    }

    /// <summary>
    /// Обновляет известные ключи server.properties, сохраняя комментарии,
    /// порядок строк и незнакомые ключи. level-name не изменяется.
    /// </summary>
    public void ApplySettings(HostedServerProfile profile)
    {
        if (profile is null)
        {
            throw new LauncherException("Профиль сервера не задан.");
        }

        var serverDir = ResolveServerDirectory(profile);
        ValidatePort(profile.Port);

        try
        {
            Directory.CreateDirectory(serverDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку сервера {serverDir}: {ex.Message}", ex);
        }

        var path = Path.Combine(serverDir, "server.properties");
        var properties = ServerProperties.Load(path);

        properties.Set("server-port", profile.Port);
        properties.Set("online-mode", profile.OnlineMode);
        properties.Set("motd", profile.Motd ?? "");
        properties.Set("max-players", profile.MaxPlayers);
        properties.Set("view-distance", 12);
        properties.Set("simulation-distance", 8);
        properties.Set("white-list", profile.Whitelist);
        properties.Set("enforce-whitelist", false);

        properties.Save(path);
    }

    /// <summary>
    /// Возвращает локальные IPv4-адреса и публичный IP (если удалось получить).
    /// Ошибки сети не считаются критичными.
    /// </summary>
    public async Task<ServerAddressInfo> GetAddressInfoAsync(int port, CancellationToken ct = default)
    {
        var localAddresses = GetLocalIpv4Addresses();
        string? publicAddress = null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PublicIpTimeout);

            var text = await _downloads
                .GetStringAsync("https://api.ipify.org", timeoutCts.Token)
                .ConfigureAwait(false);

            text = text.Trim();

            if (text.Length > 0 && IPAddress.TryParse(text, out _))
            {
                publicAddress = text;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Публичный IP не критичен: таймаут просто скрывает адрес.
        }
        catch (Exception ex) when (
            ex is LauncherException
                or HttpRequestException
                or IOException
                or OperationCanceledException
                or NotSupportedException)
        {
            // Сеть недоступна — покажем только локальные адреса.
        }

        return new ServerAddressInfo(port, localAddresses, publicAddress);
    }

    /// <summary>
    /// Ищет уже установленный сервер рядом с инстансом или в типовых папках.
    /// Возвращает первый подходящий полный путь или null.
    /// </summary>
    public static string? FindExistingServer(InstanceProfile instance)
    {
        if (instance is null)
        {
            return null;
        }

        var candidates = new List<string>();

        void Add(string parent, params string[] parts)
        {
            try
            {
                var path = parts.Aggregate(parent, Path.Combine);
                AddCandidate(candidates, path);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Некорректный путь пропускаем.
            }
        }

        if (!string.IsNullOrWhiteSpace(instance.GameDir))
        {
            Add(instance.GameDir, "server");
            Add(instance.GameDir, "..", "server");
        }

        var packDirectory = TryResolveLocalPackDirectory(instance);

        if (!string.IsNullOrWhiteSpace(packDirectory))
        {
            Add(packDirectory, "..", "server");
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(documents))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                documents = Path.Combine(userProfile, "Documents");
            }
        }

        if (!string.IsNullOrWhiteSpace(documents))
        {
            Add(documents, "Default Project", "minecraft-1.20.1", "server");
            Add(documents, "minecraft-1.20.1", "server");
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && LooksLikeServer(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task InstallForgeAsync(
        string serverDir,
        JavaInfo java,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        var installerPath = Path.Combine(
            serverDir,
            $"forge-{MinecraftVersion}-{ForgeVersion}-installer.jar");

        if (!IsInstallerUsable(installerPath))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Forge,
                $"Скачивание Forge installer {ForgeVersion}"));

            await _downloads.DownloadFileAsync(
                InstallerUrl,
                installerPath,
                progress: progress,
                stage: ProgressStage.Forge,
                displayName: $"Forge {ForgeVersion} installer",
                cancellationToken: ct).ConfigureAwait(false);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Forge,
            $"Установка Forge {ForgeVersion} (это может занять несколько минут)"));

        var result = await ProcessHost.RunAsync(
            java.Path,
            new[] { "-jar", installerPath, "--installServer", serverDir },
            serverDir,
            InstallTimeout,
            ct,
            onLine: line => progress?.Report(new LauncherProgress(ProgressStage.Forge, line)),
            outputEncoding: Encoding.UTF8).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new LauncherException(
                "Forge installer не завершился за 30 минут и был остановлен " +
                "вместе с дочерними процессами.");
        }

        if (result.ExitCode != 0)
        {
            throw new LauncherException(
                $"Forge installer завершился с кодом {result.ExitCode}.{Environment.NewLine}" +
                Tail(result.Output));
        }

        if (!FindForgeInstall(serverDir).Installed)
        {
            throw new LauncherException(
                $"Forge installer завершился успешно, но {ForgeArgsRelativePath} не найден. " +
                $"Проверьте лог установки.{Environment.NewLine}{Tail(result.Output)}");
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Forge,
            $"Forge {ForgeVersion} установлен"));
    }

    private static (bool Installed, string? Version) FindForgeInstall(string serverDir)
    {
        var forgeRoot = Path.Combine(
            serverDir,
            "libraries",
            "net",
            "minecraftforge",
            "forge");

        var exactArgs = Path.Combine(
            forgeRoot,
            MinecraftVersion + "-" + ForgeVersion,
            "win_args.txt");

        if (File.Exists(exactArgs))
        {
            return (true, ForgeVersion);
        }

        if (Directory.Exists(forgeRoot))
        {
            try
            {
                foreach (var directory in Directory
                             .EnumerateDirectories(forgeRoot)
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(directory, "win_args.txt")))
                    {
                        return (true, NormalizeForgeVersion(Path.GetFileName(directory)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Часть libraries может быть недоступна — проверим корень сервера.
            }
        }

        if (File.Exists(Path.Combine(serverDir, "win_args.txt")))
        {
            return (true, ForgeVersion);
        }

        return (false, null);
    }

    private static string NormalizeForgeVersion(string directoryName)
    {
        const string forgeMarker = "-forge-";
        var marker = directoryName.IndexOf(forgeMarker, StringComparison.OrdinalIgnoreCase);

        if (marker >= 0)
        {
            return directoryName[(marker + forgeMarker.Length)..];
        }

        var prefix = MinecraftVersion + "-";

        return directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? directoryName[prefix.Length..]
            : directoryName;
    }

    private static void AddJavaFromDirectory(
        string directory,
        List<JavaInfo> candidates,
        HashSet<string> seen)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            return;
        }

        try
        {
            foreach (var path in Directory
                         .EnumerateFiles(fullPath, "java.exe", SearchOption.AllDirectories)
                         .OrderBy(file => file.Length)
                         .ThenBy(file => file, StringComparer.OrdinalIgnoreCase))
            {
                var info = JavaLocatorImpl.TryProbe(path);

                if (info is not null
                    && info.MajorVersion >= RequiredJavaMajor
                    && seen.Add(info.Path))
                {
                    candidates.Add(info);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Часть runtime/tools может быть недоступна — проверяем остальные.
        }
    }

    private static bool IsAutoJava(string? javaPath) =>
        string.IsNullOrWhiteSpace(javaPath)
        || string.Equals(javaPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerUsable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length >= MinInstallerBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikeServer(string directory)
    {
        if (FindForgeInstall(directory).Installed)
        {
            return true;
        }

        return File.Exists(Path.Combine(directory, "server.properties"))
               && Directory.Exists(Path.Combine(directory, "mods"));
    }

    private static void AddCandidate(List<string> candidates, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fullPath);
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Некорректный путь пропускаем.
        }
    }

    private static string? TryResolveLocalPackDirectory(InstanceProfile instance)
    {
        var raw = (instance.PackUrl ?? "").Trim().Trim('"');

        if (raw.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                raw = uri.LocalPath;
            }
            else if (uri.Scheme is "http" or "https")
            {
                return null;
            }
            else if (raw.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }
        }

        try
        {
            var path = Environment.ExpandEnvironmentVariables(raw);

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(instance.GameDir))
            {
                path = Path.Combine(instance.GameDir, path);
            }

            path = Path.GetFullPath(path);

            if (Directory.Exists(path))
            {
                return path;
            }

            return string.Equals(
                Path.GetFileName(path),
                "pack.toml",
                StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path)
                : path;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties;

                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch (Exception ex) when (
                    ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;

                    if (address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    var bytes = address.GetAddressBytes();

                    if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                    {
                        // Link-local 169.254.* бесполезен для подключения друга.
                        continue;
                    }

                    var text = address.ToString();

                    if (seen.Add(text))
                    {
                        result.Add(text);
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Сетевые интерфейсы недоступны — вернём пустой список.
        }

        return result
            .OrderBy(address => IsPrivateAddress(address) ? 0 : 1)
            .ThenBy(address => address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrivateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();

        return bytes.Length == 4
               && (bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168));
    }

    private static bool IsPortInUse(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Any, port)
            {
                ExclusiveAddressUse = true
            };

            listener.Start();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or ObjectDisposedException
                or ArgumentOutOfRangeException
                or NotSupportedException
                or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch (SocketException)
            {
                // Слушатель уже закрыт.
            }
        }
    }

    private static void NormalizeMemory(HostedServerProfile profile, out int minMemory, out int maxMemory)
    {
        maxMemory = Math.Clamp(profile.MemoryMb, MinMemoryMb, MaxMemoryMb);
        minMemory = Math.Clamp(profile.MinMemoryMb, MinMemoryMb, MaxMemoryMb);

        if (minMemory > maxMemory)
        {
            minMemory = maxMemory;
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new LauncherException(
                $"Некорректный порт сервера: {port}. Допустимый диапазон: 1–65535.");
        }
    }

    /// <summary>Полный путь к папке сервера из профиля.</summary>
    public static string GetServerDirectory(HostedServerProfile profile) =>
        ResolveServerDirectory(profile);

    private static string ResolveServerDirectory(HostedServerProfile profile)
    {
        if (!TryResolveServerDirectory(profile, out var directory, out var error))
        {
            throw new LauncherException(error);
        }

        return directory;
    }

    private static bool TryResolveServerDirectory(
        HostedServerProfile? profile,
        out string directory,
        out string error)
    {
        directory = "";
        error = "";

        if (profile is null)
        {
            error = "Профиль сервера не задан.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.ServerDir))
        {
            error = "В профиле сервера не указана папка сервера.";
            return false;
        }

        try
        {
            directory = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(profile.ServerDir.Trim().Trim('"')));
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Некорректный путь к папке сервера: {profile.ServerDir}";
            return false;
        }
    }

    private static ServerInspection EmptyInspection(string error) =>
        new(
            DirectoryExists: false,
            ForgeInstalled: false,
            EulaAccepted: false,
            ModCount: 0,
            Worlds: Array.Empty<string>(),
            PortInUse: false,
            InstalledForgeVersion: null,
            LevelName: ServerWorldService.DefaultWorldName,
            Error: error);

    private static string Tail(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "Вывод пуст.";
        }

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(Environment.NewLine, lines.TakeLast(OutputTailLines));
    }
}
