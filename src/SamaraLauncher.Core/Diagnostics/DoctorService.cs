using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Logs;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Диагностика инстанса: Java, папка игры, версия, библиотеки и ассеты, диск,
/// моды, packwiz, настройки, crash-report и сеть.
/// Каждая проверка изолирована: её исключение превращается в Error/Warning, а не роняет отчёт.
/// </summary>
public sealed class DoctorService
{
    private const int RequiredJavaMajor = 17;
    private const long DiskErrorBytes = 2L * 1024 * 1024 * 1024;
    private const long DiskWarningBytes = 5L * 1024 * 1024 * 1024;
    private const int NetworkTimeoutSeconds = 10;
    private const int MaxDetailsLines = 12;
    private const string MojangManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly string Nl = Environment.NewLine;
    private static readonly TimeSpan FreshCrashAge = TimeSpan.FromDays(7);
    private static readonly HttpClient MojangHttp = new() { Timeout = TimeSpan.FromSeconds(NetworkTimeoutSeconds) };

    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly Regex DuplicateSuffixPattern = new(
        @"(?:\s*[\(\[](?:copy|копия|\d+)[\)\]]|\s*[-_]\s*(?:copy|копия))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionNumberPattern = new(
        @"\d+(?:[._-]\d+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeparatorPattern = new(
        @"[-_.\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly JavaManager _java;
    private readonly ModService _mods;
    private readonly ModAnalyzer _analyzer;
    private readonly CrashReportParser _crashReports;
    private readonly ToolLocator _tools;
    private readonly InstanceStore _store;

    public DoctorService(LauncherPaths? paths = null)
    {
        paths ??= new LauncherPaths();
        _java = new JavaManager(paths);
        _mods = new ModService();
        _analyzer = new ModAnalyzer(_mods);
        _crashReports = new CrashReportParser();
        _tools = new ToolLocator();
        _store = new InstanceStore(paths.ConfigPath);
    }

    /// <summary>
    /// Выполняет все проверки инстанса. Отчёт не бросает исключений из-за проблем клиента:
    /// каждая проверка сама превращает ошибку в <see cref="DoctorStatus.Error"/> или
    /// <see cref="DoctorStatus.Warning"/>. Исключение — отмена через <paramref name="ct"/>.
    /// </summary>
    public async Task<DoctorReport> RunAsync(
        InstanceProfile instance,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ct.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<DoctorCheck>();
        var gameDir = TryGetFullPath(instance.GameDir);

        var steps = new (string Name, Func<Task<IReadOnlyList<DoctorCheck>>> Run)[]
        {
            ("Java", () => CheckJavaAsync(instance, ct)),
            ("папка игры", () => CheckGameDirAsync(instance, gameDir, ct)),
            ("версия", () => CheckVersionsAsync(instance, gameDir, ct)),
            ("библиотеки и ассеты", () => CheckFilesAsync(gameDir, ct)),
            ("свободное место", () => CheckDiskAsync(gameDir, ct)),
            ("моды", () => CheckModsAsync(instance, gameDir, ct)),
            ("packwiz", () => CheckPackwizAsync(instance, gameDir, ct)),
            ("настройки", () => CheckConfigAsync(instance, ct)),
            ("crash-report", () => CheckLogsAsync(gameDir, ct)),
            ("сеть", () => CheckNetworkAsync(ct)),
            ("служебные папки", () => CheckDirectoriesAsync(gameDir, ct)),
            ("временные файлы", () => CheckTempFilesAsync(gameDir, ct))
        };

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            "Doctor: начало диагностики...",
            0,
            steps.Length));

        for (var index = 0; index < steps.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, run) = steps[index];

            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                $"Doctor: проверка «{name}»...",
                index,
                steps.Length));

            IReadOnlyList<DoctorCheck> stepChecks;
            try
            {
                stepChecks = await run().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                stepChecks = new[] { UnexpectedFailure(name, ex) };
            }

            checks.AddRange(stepChecks);

            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                $"Doctor: «{name}» — готово",
                index + 1,
                steps.Length));
        }

        stopwatch.Stop();
        var report = new DoctorReport
        {
            StartedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Checks = checks
        };

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            $"Doctor: {report.Summary}",
            steps.Length,
            steps.Length));

        return report;
    }

    private Task<IReadOnlyList<DoctorCheck>> CheckJavaAsync(InstanceProfile instance, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var java = _java.FindFor(instance, RequiredJavaMajor);
                ct.ThrowIfCancellationRequested();

                if (java is null)
                {
                    return new[]
                    {
                        CreateCheck(
                            "java",
                            "Java",
                            DoctorCategory.Java,
                            DoctorStatus.Error,
                            "Java 17+ не найдена.",
                            BuildJavaSearchDetails(instance),
                            false,
                            "Установите Eclipse Temurin 17 или новее и укажите java.exe в настройках инстанса.")
                    };
                }

                if (!File.Exists(java.Path))
                {
                    return new[]
                    {
                        CreateCheck(
                            "java",
                            "Java",
                            DoctorCategory.Java,
                            DoctorStatus.Error,
                            $"Файл Java не найден: {java.Path}",
                            $"Java сообщила версию {java.Version}, но java.exe исчез.",
                            false,
                            "Проверьте, что Java не была удалена, или выберите другой путь в настройках инстанса.")
                    };
                }

                if (java.MajorVersion < RequiredJavaMajor)
                {
                    return new[]
                    {
                        CreateCheck(
                            "java",
                            "Java",
                            DoctorCategory.Java,
                            DoctorStatus.Error,
                            $"Найдена Java {java.MajorVersion}, а нужна {RequiredJavaMajor}+.",
                            $"Версия: {java.Version}{Nl}Путь: {java.Path}",
                            false,
                            "Установите Java 17+ и укажите её в настройках инстанса.")
                    };
                }

                if (string.IsNullOrWhiteSpace(java.Version))
                {
                    return new[]
                    {
                        CreateCheck(
                            "java",
                            "Java",
                            DoctorCategory.Java,
                            DoctorStatus.Warning,
                            "Java найдена, но версию прочитать не удалось.",
                            $"Путь: {java.Path}{Nl}Major: {java.MajorVersion}",
                            false,
                            "Проверьте целостность установки Java или переустановите её.")
                    };
                }

                return new[]
                {
                    CreateCheck(
                        "java",
                        "Java",
                        DoctorCategory.Java,
                        DoctorStatus.Ok,
                        $"Java {java.Version} (major {java.MajorVersion}).",
                        $"Версия: {java.Version}{Nl}Major: {java.MajorVersion}{Nl}Путь: {java.Path}")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new[]
                {
                    CreateCheck(
                        "java",
                        "Java",
                        DoctorCategory.Java,
                        DoctorStatus.Error,
                        $"Не удалось проверить Java: {ex.Message}",
                        ex.GetType().Name,
                        false,
                        "Проверьте, что java.exe запускается вручную, и повторите диагностику.")
                };
            }
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckGameDirAsync(
        InstanceProfile instance,
        string gameDir,
        CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var checks = new List<DoctorCheck>();

            if (string.IsNullOrWhiteSpace(instance.GameDir))
            {
                checks.Add(CreateCheck(
                    "gamedir.exists",
                    "Папка игры",
                    DoctorCategory.Files,
                    DoctorStatus.Error,
                    "У инстанса не указана папка игры.",
                    "",
                    false,
                    "Укажите папку игры в настройках инстанса."));
                checks.Add(Skipped(
                    "gamedir.writable",
                    "Запись в папку игры",
                    DoctorCategory.Files,
                    "Папка игры не указана."));
                checks.Add(Skipped(
                    "gamedir.system",
                    "Системная папка",
                    DoctorCategory.Files,
                    "Папка игры не указана."));
                return checks;
            }

            if (gameDir.Length == 0)
            {
                checks.Add(CreateCheck(
                    "gamedir.exists",
                    "Папка игры",
                    DoctorCategory.Files,
                    DoctorStatus.Error,
                    $"Некорректный путь к папке игры: {instance.GameDir}",
                    "",
                    false,
                    "Укажите корректный путь в настройках инстанса."));
                checks.Add(Skipped(
                    "gamedir.writable",
                    "Запись в папку игры",
                    DoctorCategory.Files,
                    "Путь к папке игры некорректен."));
                checks.Add(Skipped(
                    "gamedir.system",
                    "Системная папка",
                    DoctorCategory.Files,
                    "Путь к папке игры некорректен."));
                return checks;
            }

            var exists = Directory.Exists(gameDir);
            checks.Add(CreateCheck(
                "gamedir.exists",
                "Папка игры",
                DoctorCategory.Files,
                exists ? DoctorStatus.Ok : DoctorStatus.Error,
                exists
                    ? $"Папка игры существует: {gameDir}"
                    : $"Папка игры не найдена: {gameDir}",
                exists ? "" : "Автоисправление создаст папку.",
                !exists,
                exists ? "" : "Запустите автоисправление или создайте папку вручную."));

            if (!exists)
            {
                checks.Add(Skipped(
                    "gamedir.writable",
                    "Запись в папку игры",
                    DoctorCategory.Files,
                    "Папка игры отсутствует — проверить запись нельзя."));
            }
            else
            {
                var probe = Path.Combine(gameDir, ".samara-doctor-" + Guid.NewGuid().ToString("N") + ".tmp");

                try
                {
                    using (var stream = new FileStream(
                               probe,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               1,
                               FileOptions.DeleteOnClose))
                    {
                        stream.WriteByte(0);
                    }

                    checks.Add(CreateCheck(
                        "gamedir.writable",
                        "Запись в папку игры",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        "Запись в папку игры доступна.",
                        gameDir));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    checks.Add(CreateCheck(
                        "gamedir.writable",
                        "Запись в папку игры",
                        DoctorCategory.Files,
                        DoctorStatus.Error,
                        $"Нет доступа на запись в папку игры: {ex.Message}",
                        gameDir,
                        false,
                        "Проверьте права на папку, антивирус и атрибут «только чтение»."));
                }
                finally
                {
                    TryDeleteFile(probe);
                }
            }

            try
            {
                checks.Add(CheckSystemLocation(gameDir));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(CreateCheck(
                    "gamedir.system",
                    "Системная папка",
                    DoctorCategory.Files,
                    DoctorStatus.Warning,
                    $"Не удалось проверить расположение папки игры: {ex.Message}",
                    gameDir));
            }

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckVersionsAsync(
        InstanceProfile instance,
        string gameDir,
        CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var checks = new List<DoctorCheck>();

            if (gameDir.Length == 0)
            {
                checks.Add(Skipped(
                    "versions.main",
                    "Версия игры",
                    DoctorCategory.Files,
                    "Папка игры не определена."));
                checks.Add(Skipped(
                    "versions.forge",
                    "Forge",
                    DoctorCategory.Files,
                    "Папка игры не определена."));
                return checks;
            }

            var versionId = ResolveVersionId(instance);
            var versionDir = versionId.Length > 0
                ? Path.Combine(gameDir, "versions", versionId)
                : "";
            var jsonPath = versionDir.Length > 0
                ? Path.Combine(versionDir, versionId + ".json")
                : "";

            var versionJsonReadable = false;
            string? parentVersion = null;
            var forgeMarker = false;

            if (versionId.Length == 0)
            {
                checks.Add(CreateCheck(
                    "versions.main",
                    "Версия игры",
                    DoctorCategory.Files,
                    DoctorStatus.Warning,
                    "Не удалось определить версию игры.",
                    "VersionId, LastVersionId и MinecraftVersion не заданы.",
                    false,
                    "Укажите VersionId в настройках инстанса или переустановите версию."));
            }
            else if (!Directory.Exists(versionDir))
            {
                checks.Add(CreateCheck(
                    "versions.main",
                    "Версия игры",
                    DoctorCategory.Files,
                    DoctorStatus.Error,
                    $"Папка версии не найдена: {versionDir}",
                    $"Ожидался файл: {jsonPath}",
                    false,
                    "Переустановите версию Minecraft или исправьте VersionId."));
            }
            else if (!File.Exists(jsonPath))
            {
                checks.Add(CreateCheck(
                    "versions.main",
                    "Версия игры",
                    DoctorCategory.Files,
                    DoctorStatus.Error,
                    $"Файл версии не найден: {jsonPath}",
                    "",
                    false,
                    "Переустановите версию Minecraft — файл версии повреждён или удалён."));
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(jsonPath);
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;

                    if (root.TryGetProperty("inheritsFrom", out var parentElement) &&
                        parentElement.ValueKind == JsonValueKind.String)
                    {
                        parentVersion = parentElement.GetString();
                    }

                    if (root.TryGetProperty("mainClass", out var mainClassElement) &&
                        mainClassElement.ValueKind == JsonValueKind.String)
                    {
                        var mainClass = mainClassElement.GetString() ?? "";
                        forgeMarker = mainClass.Contains("forge", StringComparison.OrdinalIgnoreCase);
                    }

                    forgeMarker = forgeMarker ||
                                  json.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                                  json.Contains("forgeclient", StringComparison.OrdinalIgnoreCase);

                    versionJsonReadable = true;
                    var size = new FileInfo(jsonPath).Length;

                    checks.Add(CreateCheck(
                        "versions.main",
                        "Версия игры",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        $"Версия {versionId} найдена.",
                        $"{jsonPath}{Nl}Размер: {size} байт"));
                }
                catch (JsonException ex)
                {
                    checks.Add(CreateCheck(
                        "versions.main",
                        "Версия игры",
                        DoctorCategory.Files,
                        DoctorStatus.Error,
                        "Файл версии содержит некорректный JSON.",
                        $"{jsonPath}{Nl}{ex.Message}",
                        false,
                        "Переустановите версию — файл версии повреждён."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    checks.Add(CreateCheck(
                        "versions.main",
                        "Версия игры",
                        DoctorCategory.Files,
                        DoctorStatus.Error,
                        $"Не удалось прочитать файл версии: {ex.Message}",
                        jsonPath,
                        false,
                        "Проверьте права на файл и повторите диагностику."));
                }
            }

            if (parentVersion is not null && versionJsonReadable)
            {
                var parentJson = Path.Combine(gameDir, "versions", parentVersion, parentVersion + ".json");

                checks.Add(File.Exists(parentJson)
                    ? CreateCheck(
                        "versions.parent",
                        "Родительская версия",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        $"Родительская версия {parentVersion} найдена.",
                        parentJson)
                    : CreateCheck(
                        "versions.parent",
                        "Родительская версия",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        $"Не найдена родительская версия {parentVersion}.",
                        parentJson,
                        false,
                        "Переустановите версию — без родительской версии игра не запустится."));
            }
            else if (versionJsonReadable)
            {
                checks.Add(Skipped(
                    "versions.parent",
                    "Родительская версия",
                    DoctorCategory.Files,
                    "Версия не наследует другую."));
            }

            var forgeExpected = !string.IsNullOrWhiteSpace(instance.ForgeVersion) ||
                                versionId.Contains("forge", StringComparison.OrdinalIgnoreCase);

            if (!forgeExpected)
            {
                checks.Add(Skipped(
                    "versions.forge",
                    "Forge",
                    DoctorCategory.Files,
                    "Forge не используется в этом инстансе."));
                return checks;
            }

            try
            {
                var forgeLib = Path.Combine(gameDir, "libraries", "net", "minecraftforge", "forge");
                var forgeJars = 0;

                if (Directory.Exists(forgeLib))
                {
                    foreach (var _ in Directory.EnumerateFiles(
                                 forgeLib,
                                 "*.jar",
                                 SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        forgeJars++;
                    }
                }

                var hasForgeJson = false;
                if (versionDir.Length > 0 && Directory.Exists(versionDir))
                {
                    hasForgeJson = Directory
                        .EnumerateFiles(versionDir, "*.json", SearchOption.TopDirectoryOnly)
                        .Any(path => Path.GetFileName(path)
                            .Contains("forge", StringComparison.OrdinalIgnoreCase));
                }

                if (forgeJars > 0)
                {
                    checks.Add(CreateCheck(
                        "versions.forge",
                        "Forge",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        $"Библиотеки Forge найдены (jar: {forgeJars}).",
                        forgeLib));
                }
                else if (hasForgeJson || forgeMarker)
                {
                    checks.Add(CreateCheck(
                        "versions.forge",
                        "Forge",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        "Forge JSON найден; отдельные библиотеки Forge не обнаружены.",
                        $"Версия: {versionId}{Nl}Папка библиотек: {forgeLib}"));
                }
                else
                {
                    checks.Add(CreateCheck(
                        "versions.forge",
                        "Forge",
                        DoctorCategory.Files,
                        DoctorStatus.Error,
                        "Forge указан в профиле, но файлы Forge не найдены.",
                        $"Ожидалась папка: {forgeLib}{Nl}Версия: {versionId}",
                        false,
                        "Переустановите Forge для этой версии Minecraft."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(CreateCheck(
                    "versions.forge",
                    "Forge",
                    DoctorCategory.Files,
                    DoctorStatus.Warning,
                    $"Не удалось проверить Forge: {ex.Message}",
                    $"Версия: {versionId}"));
            }

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckFilesAsync(string gameDir, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped("files.libraries", "Библиотеки", DoctorCategory.Files, "Папка игры не определена."),
                    Skipped("files.assets", "Ассеты", DoctorCategory.Files, "Папка игры не определена.")
                };
            }

            var checks = new List<DoctorCheck>();

            var librariesDir = Path.Combine(gameDir, "libraries");
            try
            {
                var libraryFiles = CountFiles(librariesDir, ct);
                checks.Add(libraryFiles == 0
                    ? CreateCheck(
                        "files.libraries",
                        "Библиотеки",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        "Папка libraries пуста или отсутствует.",
                        librariesDir,
                        false,
                        "Переустановите версию — без библиотек игра не запустится.")
                    : CreateCheck(
                        "files.libraries",
                        "Библиотеки",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        $"Файлов библиотек: {libraryFiles}.",
                        librariesDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add(CreateCheck(
                    "files.libraries",
                    "Библиотеки",
                    DoctorCategory.Files,
                    DoctorStatus.Warning,
                    $"Не удалось посчитать библиотеки: {ex.Message}",
                    librariesDir));
            }

            var assetsDir = Path.Combine(gameDir, "assets");
            try
            {
                var indexesDir = Path.Combine(assetsDir, "indexes");
                var objectsDir = Path.Combine(assetsDir, "objects");
                var indexCount = CountTopLevelFiles(indexesDir, "*.json", ct);
                var objectCount = CountFiles(objectsDir, ct);

                if (indexCount == 0 && objectCount == 0)
                {
                    checks.Add(CreateCheck(
                        "files.assets",
                        "Ассеты",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        "Ассеты Minecraft не найдены.",
                        $"{indexesDir}{Nl}{objectsDir}",
                        false,
                        "Переустановите версию — без ассетов игра не запустится."));
                }
                else if (indexCount == 0)
                {
                    checks.Add(CreateCheck(
                        "files.assets",
                        "Ассеты",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        "В assets\\indexes нет index.json.",
                        $"{indexesDir}{Nl}Объектов: {objectCount}",
                        false,
                        "Переустановите версию — отсутствует индекс ассетов."));
                }
                else if (objectCount == 0)
                {
                    checks.Add(CreateCheck(
                        "files.assets",
                        "Ассеты",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        "Папка assets\\objects пуста.",
                        $"{objectsDir}{Nl}Индексов: {indexCount}",
                        false,
                        "Переустановите версию — файлы ассетов отсутствуют."));
                }
                else
                {
                    checks.Add(CreateCheck(
                        "files.assets",
                        "Ассеты",
                        DoctorCategory.Files,
                        DoctorStatus.Ok,
                        $"Индексов ассетов: {indexCount}, объектов: {objectCount}.",
                        $"{indexesDir}{Nl}{objectsDir}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add(CreateCheck(
                    "files.assets",
                    "Ассеты",
                    DoctorCategory.Files,
                    DoctorStatus.Warning,
                    $"Не удалось посчитать ассеты: {ex.Message}",
                    assetsDir));
            }

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckDiskAsync(string gameDir, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped("disk.free", "Свободное место", DoctorCategory.Disk, "Папка игры не определена.")
                };
            }

            try
            {
                var root = Path.GetPathRoot(gameDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return new[]
                    {
                        Skipped("disk.free", "Свободное место", DoctorCategory.Disk, "Не удалось определить диск.")
                    };
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return new[]
                    {
                        CreateCheck(
                            "disk.free",
                            "Свободное место",
                            DoctorCategory.Disk,
                            DoctorStatus.Warning,
                            $"Диск {drive.Name} не готов.",
                            gameDir)
                    };
                }

                var freeBytes = drive.AvailableFreeSpace;
                var freeGb = freeBytes / (1024d * 1024d * 1024d);
                var details = $"Диск: {drive.Name}{Nl}Свободно: {freeGb:0.0} ГБ";

                if (freeBytes < DiskErrorBytes)
                {
                    return new[]
                    {
                        CreateCheck(
                            "disk.free",
                            "Свободное место",
                            DoctorCategory.Disk,
                            DoctorStatus.Error,
                            $"Критически мало места: {freeGb:0.0} ГБ.",
                            details,
                            false,
                            "Освободите хотя бы 5 ГБ — обновлению сборки нужно место для файлов.")
                    };
                }

                return new[]
                {
                    freeBytes < DiskWarningBytes
                        ? CreateCheck(
                            "disk.free",
                            "Свободное место",
                            DoctorCategory.Disk,
                            DoctorStatus.Warning,
                            $"Мало свободного места: {freeGb:0.0} ГБ.",
                            details,
                            false,
                            "Освободите место на диске — для обновления сборки рекомендуется 5 ГБ и больше.")
                        : CreateCheck(
                            "disk.free",
                            "Свободное место",
                            DoctorCategory.Disk,
                            DoctorStatus.Ok,
                            $"Свободно {freeGb:0.0} ГБ.",
                            details)
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or
                                           UnauthorizedAccessException or NotSupportedException)
            {
                return new[]
                {
                    CreateCheck(
                        "disk.free",
                        "Свободное место",
                        DoctorCategory.Disk,
                        DoctorStatus.Warning,
                        $"Не удалось определить свободное место: {ex.Message}",
                        gameDir)
                };
            }
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckModsAsync(
        InstanceProfile instance,
        string gameDir,
        CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped("mods.present", "Моды", DoctorCategory.Mods, "Папка игры не определена."),
                    Skipped("mods.duplicates", "Дубликаты модов", DoctorCategory.Mods, "Папка игры не определена."),
                    Skipped("mods.analysis", "Анализ модов", DoctorCategory.Mods, "Папка игры не определена.")
                };
            }

            var modsDir = Path.Combine(gameDir, "mods");
            IReadOnlyList<ModEntry> mods;

            try
            {
                mods = _mods.List(instance);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new[]
                {
                    CreateCheck(
                        "mods.present",
                        "Моды",
                        DoctorCategory.Mods,
                        DoctorStatus.Error,
                        $"Не удалось прочитать папку mods: {ex.Message}",
                        modsDir,
                        false,
                        "Проверьте права на папку mods и повторите диагностику."),
                    Skipped("mods.duplicates", "Дубликаты модов", DoctorCategory.Mods, "Список модов недоступен."),
                    Skipped("mods.analysis", "Анализ модов", DoctorCategory.Mods, "Список модов недоступен.")
                };
            }

            ct.ThrowIfCancellationRequested();
            var checks = new List<DoctorCheck>();
            var enabled = mods.Count(mod => mod.Enabled);

            checks.Add(mods.Count == 0
                ? CreateCheck(
                    "mods.present",
                    "Моды",
                    DoctorCategory.Mods,
                    DoctorStatus.Warning,
                    "В папке mods нет jar-файлов.",
                    $"{modsDir}{Nl}Для ванильной сборки это нормально.",
                    false,
                    "Добавьте моды в папку mods или проверьте, что сборка установлена полностью.")
                : CreateCheck(
                    "mods.present",
                    "Моды",
                    DoctorCategory.Mods,
                    DoctorStatus.Ok,
                    $"Модов: {mods.Count} (включено: {enabled}, отключено: {mods.Count - enabled}).",
                    modsDir));

            if (mods.Count == 0)
            {
                checks.Add(Skipped(
                    "mods.duplicates",
                    "Дубликаты модов",
                    DoctorCategory.Mods,
                    "Модов нет."));
            }
            else
            {
                var groups = mods
                    .GroupBy(mod => NormalizeModKey(mod.FileName), StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Key.Length > 0)
                    .Select(group => new
                    {
                        Key = group.Key,
                        Files = group
                            .Select(mod => mod.FileName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    })
                    .Where(group => group.Files.Count > 1)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (groups.Count == 0)
                {
                    checks.Add(CreateCheck(
                        "mods.duplicates",
                        "Дубликаты модов",
                        DoctorCategory.Mods,
                        DoctorStatus.Ok,
                        "Дубликаты модов не найдены.",
                        modsDir));
                }
                else
                {
                    var details = string.Join(
                        Nl,
                        groups
                            .Take(MaxDetailsLines)
                            .Select(group => string.Join(" / ", group.Files)));

                    if (groups.Count > MaxDetailsLines)
                    {
                        details += Nl + $"... и ещё {groups.Count - MaxDetailsLines}.";
                    }

                    checks.Add(CreateCheck(
                        "mods.duplicates",
                        "Дубликаты модов",
                        DoctorCategory.Mods,
                        DoctorStatus.Warning,
                        $"Найдены возможные дубликаты модов: {groups.Count}.",
                        details,
                        false,
                        "Удалите или отключите лишний файл вручную — автоматически моды не удаляются."));
                }
            }

            if (mods.Count == 0)
            {
                checks.Add(Skipped(
                    "mods.analysis",
                    "Анализ модов",
                    DoctorCategory.Mods,
                    "Модов нет."));
            }
            else
            {
                try
                {
                    var analysis = _analyzer.Analyze(instance);
                    ct.ThrowIfCancellationRequested();

                    var notes = new List<string>();

                    foreach (var duplicate in analysis.DuplicateModIds)
                    {
                        notes.Add(
                            $"Дубликат modId \"{duplicate.ModId}\": " +
                            $"{string.Join(", ", duplicate.FileNames)}.");
                    }

                    foreach (var file in analysis.DuplicateFileNames)
                    {
                        notes.Add($"Дубликат имени файла: {file}.");
                    }

                    foreach (var issue in analysis.MissingDependencies)
                    {
                        var suffix = string.IsNullOrWhiteSpace(issue.VersionRange)
                            ? ""
                            : $" {issue.VersionRange}";

                        notes.Add(string.IsNullOrWhiteSpace(issue.FoundFileName)
                            ? $"Мод {issue.RequiredBy} требует {issue.ModId}{suffix}, но он не найден."
                            : $"Мод {issue.RequiredBy} требует {issue.ModId} {issue.VersionRange}, " +
                              $"но найдена версия {issue.FoundVersion} ({issue.FoundFileName}).");
                    }

                    foreach (var issue in analysis.DisabledDependencies)
                    {
                        notes.Add(
                            $"Зависимость {issue.ModId} мода {issue.RequiredBy} отключена " +
                            $"({issue.FoundFileName ?? "файл не найден"}).");
                    }

                    notes.AddRange(analysis.Warnings);

                    if (notes.Count == 0)
                    {
                        checks.Add(CreateCheck(
                            "mods.analysis",
                            "Анализ модов",
                            DoctorCategory.Mods,
                            DoctorStatus.Ok,
                            "Проблем с зависимостями не найдено.",
                            $"Проверено модов: {analysis.Mods.Count}."));
                    }
                    else
                    {
                        var details = string.Join(
                            Nl,
                            notes.Take(MaxDetailsLines));

                        if (notes.Count > MaxDetailsLines)
                        {
                            details += Nl + $"... и ещё {notes.Count - MaxDetailsLines}.";
                        }

                        checks.Add(CreateCheck(
                            "mods.analysis",
                            "Анализ модов",
                            DoctorCategory.Mods,
                            DoctorStatus.Warning,
                            $"Анализатор модов нашёл замечаний: {notes.Count}.",
                            details,
                            false,
                            "Проверьте зависимости и отключите несовместимые моды."));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checks.Add(CreateCheck(
                        "mods.analysis",
                        "Анализ модов",
                        DoctorCategory.Mods,
                        DoctorStatus.Warning,
                        $"Не удалось выполнить анализ модов: {ex.Message}",
                        modsDir,
                        false,
                        "Проверьте, что jar-файлы не повреждены и доступны для чтения."));
                }
            }

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckPackwizAsync(
        InstanceProfile instance,
        string gameDir,
        CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var checks = new List<DoctorCheck>();

            try
            {
                var bootstrap = _tools.TryFindPackwizBootstrap();
                checks.Add(bootstrap is null
                    ? CreateCheck(
                        "config.packwiz",
                        "Packwiz bootstrap",
                        DoctorCategory.Config,
                        DoctorStatus.Warning,
                        "Не найден packwiz-installer-bootstrap.jar.",
                        "Искали в папке tools рядом с лаунчером и выше по дереву каталогов.",
                        false,
                        "Положите packwiz-installer-bootstrap.jar в папку tools рядом с лаунчером.")
                    : CreateCheck(
                        "config.packwiz",
                        "Packwiz bootstrap",
                        DoctorCategory.Config,
                        DoctorStatus.Ok,
                        "Packwiz bootstrap найден.",
                        bootstrap));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(CreateCheck(
                    "config.packwiz",
                    "Packwiz bootstrap",
                    DoctorCategory.Config,
                    DoctorStatus.Warning,
                    $"Не удалось проверить packwiz: {ex.Message}",
                    ""));
            }

            var raw = (instance.PackUrl ?? "").Trim();

            if (raw.Length == 0)
            {
                checks.Add(CreateCheck(
                    "config.packurl",
                    "PackUrl",
                    DoctorCategory.Config,
                    DoctorStatus.Warning,
                    "В профиле не указан PackUrl.",
                    "Сборка не будет обновляться через packwiz.",
                    false,
                    "Укажите URL или локальный путь к pack.toml в настройках инстанса."));
            }
            else if (IsHttpUrl(raw))
            {
                checks.Add(CreateCheck(
                    "config.packurl",
                    "PackUrl",
                    DoctorCategory.Config,
                    DoctorStatus.Ok,
                    "PackUrl задан.",
                    raw));
            }
            else
            {
                var path = TryResolveLocalPack(raw, gameDir);

                if (path.Length == 0)
                {
                    checks.Add(CreateCheck(
                        "config.packurl",
                        "PackUrl",
                        DoctorCategory.Config,
                        DoctorStatus.Warning,
                        "Не удалось разобрать локальный PackUrl.",
                        raw,
                        false,
                        "Укажите корректный путь к pack.toml или URL."));
                }
                else if (File.Exists(path) || Directory.Exists(path))
                {
                    checks.Add(CreateCheck(
                        "config.packurl",
                        "PackUrl",
                        DoctorCategory.Config,
                        DoctorStatus.Ok,
                        "Локальный pack.toml найден.",
                        path));
                }
                else
                {
                    checks.Add(CreateCheck(
                        "config.packurl",
                        "PackUrl",
                        DoctorCategory.Config,
                        DoctorStatus.Warning,
                        "Локальный PackUrl не найден.",
                        path,
                        false,
                        "Проверьте путь к pack.toml или укажите URL сборки."));
                }
            }

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckConfigAsync(InstanceProfile instance, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var checks = new List<DoctorCheck>();
            var configPath = _store.ConfigPath;

            if (!File.Exists(configPath))
            {
                checks.Add(CreateCheck(
                    "config.json",
                    "Файл настроек",
                    DoctorCategory.Config,
                    DoctorStatus.Warning,
                    "config.json не найден.",
                    configPath,
                    false,
                    "Файл будет создан при сохранении настроек лаунчера."));
            }
            else
            {
                try
                {
                    var config = _store.Load();
                    checks.Add(CreateCheck(
                        "config.json",
                        "Файл настроек",
                        DoctorCategory.Config,
                        DoctorStatus.Ok,
                        $"config.json прочитан (инстансов: {config.Instances.Count}).",
                        configPath));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checks.Add(CreateCheck(
                        "config.json",
                        "Файл настроек",
                        DoctorCategory.Config,
                        DoctorStatus.Error,
                        $"Не удалось прочитать config.json: {ex.Message}",
                        configPath,
                        false,
                        "Исправьте JSON вручную или переименуйте файл — лаунчер создаст новый."));
                }
            }

            var memoryIssues = new List<string>();

            if (instance.MemoryMb < 1024 || instance.MemoryMb > 32768)
            {
                memoryIssues.Add($"MemoryMb={instance.MemoryMb}: допустимый диапазон 1024..32768.");
            }

            if (instance.MinMemoryMb < 0 || instance.MinMemoryMb > instance.MemoryMb)
            {
                memoryIssues.Add(
                    $"MinMemoryMb={instance.MinMemoryMb} должен быть не больше MemoryMb={instance.MemoryMb}.");
            }

            checks.Add(memoryIssues.Count > 0
                ? CreateCheck(
                    "config.memory",
                    "Память",
                    DoctorCategory.Config,
                    DoctorStatus.Warning,
                    "Параметры памяти выглядят некорректно.",
                    string.Join(Nl, memoryIssues),
                    false,
                    "Укажите MemoryMb от 1024 до 32768 и MinMemoryMb не больше MemoryMb.")
                : CreateCheck(
                    "config.memory",
                    "Память",
                    DoctorCategory.Config,
                    DoctorStatus.Ok,
                    $"Память: {instance.MemoryMb} МБ (минимум {instance.MinMemoryMb} МБ).",
                    ""));

            return checks;
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckLogsAsync(string gameDir, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped("logs.crash", "Crash-report", DoctorCategory.Logs, "Папка игры не определена.")
                };
            }

            try
            {
                var latest = _crashReports.FindLatest(gameDir);

                if (latest is null)
                {
                    return new[]
                    {
                        CreateCheck(
                            "logs.crash",
                            "Crash-report",
                            DoctorCategory.Logs,
                            DoctorStatus.Ok,
                            "Crash-report не найден.",
                            Path.Combine(gameDir, "crash-reports"))
                    };
                }

                var info = new FileInfo(latest);
                var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                var date = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                if (age <= FreshCrashAge)
                {
                    return new[]
                    {
                        CreateCheck(
                            "logs.crash",
                            "Crash-report",
                            DoctorCategory.Logs,
                            DoctorStatus.Warning,
                            $"Есть свежий crash-report: {info.Name}",
                            $"{latest}{Nl}Изменён: {date}",
                            false,
                            "Откройте отчёт и проверьте причину краша; при необходимости отключите проблемный мод.")
                    };
                }

                return new[]
                {
                    CreateCheck(
                        "logs.crash",
                        "Crash-report",
                        DoctorCategory.Logs,
                        DoctorStatus.Ok,
                        $"Свежих crash-report нет (последний: {info.Name}, {date}).",
                        latest)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new[]
                {
                    CreateCheck(
                        "logs.crash",
                        "Crash-report",
                        DoctorCategory.Logs,
                        DoctorStatus.Warning,
                        $"Не удалось проверить crash-reports: {ex.Message}",
                        Path.Combine(gameDir, "crash-reports"))
                };
            }
        }, ct);

    private async Task<IReadOnlyList<DoctorCheck>> CheckNetworkAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(NetworkTimeoutSeconds));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var headStatus = await SendMojangRequestAsync(HttpMethod.Head, timeout.Token).ConfigureAwait(false);

            if (IsSuccessStatus(headStatus))
            {
                return new[] { NetworkOk(headStatus, stopwatch.Elapsed) };
            }

            var getStatus = await SendMojangRequestAsync(HttpMethod.Get, timeout.Token).ConfigureAwait(false);

            if (IsSuccessStatus(getStatus))
            {
                return new[] { NetworkOk(getStatus, stopwatch.Elapsed) };
            }

            var status = getStatus ?? headStatus;
            var code = status is null ? "нет ответа" : $"HTTP {(int)status.Value}";

            return new[]
            {
                CreateCheck(
                    "network.mojang",
                    "Mojang API",
                    DoctorCategory.Network,
                    DoctorStatus.Warning,
                    $"Mojang API недоступен ({code}).",
                    MojangManifestUrl,
                    false,
                    "Проверьте подключение к интернету, VPN и блокировщики.")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new[]
            {
                CreateCheck(
                    "network.mojang",
                    "Mojang API",
                    DoctorCategory.Network,
                    DoctorStatus.Warning,
                    $"Таймаут {NetworkTimeoutSeconds} с при проверке Mojang API.",
                    MojangManifestUrl,
                    false,
                    "Проверьте подключение к интернету и повторите диагностику.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new[]
            {
                CreateCheck(
                    "network.mojang",
                    "Mojang API",
                    DoctorCategory.Network,
                    DoctorStatus.Warning,
                    $"Mojang API недоступен: {ex.Message}",
                    MojangManifestUrl,
                    false,
                    "Проверьте подключение к интернету, VPN и блокировщики.")
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new[]
            {
                CreateCheck(
                    "network.mojang",
                    "Mojang API",
                    DoctorCategory.Network,
                    DoctorStatus.Warning,
                    $"Не удалось проверить сеть: {ex.Message}",
                    MojangManifestUrl)
            };
        }
    }

    private Task<IReadOnlyList<DoctorCheck>> CheckDirectoriesAsync(string gameDir, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped(
                        "files.directories",
                        "Служебные папки",
                        DoctorCategory.Files,
                        "Папка игры не определена.")
                };
            }

            try
            {
                var required = new[] { "logs", "crash-reports", "mods", "config" };
                var missing = required
                    .Where(name => !Directory.Exists(Path.Combine(gameDir, name)))
                    .ToList();

                if (missing.Count == 0)
                {
                    return new[]
                    {
                        CreateCheck(
                            "files.directories",
                            "Служебные папки",
                            DoctorCategory.Files,
                            DoctorStatus.Ok,
                            "Служебные папки на месте.",
                            gameDir)
                    };
                }

                return new[]
                {
                    CreateCheck(
                        "files.directories",
                        "Служебные папки",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        $"Отсутствуют папки: {string.Join(", ", missing)}.",
                        string.Join(Nl, missing.Select(name => Path.Combine(gameDir, name))),
                        true,
                        "Запустите автоисправление — пустые служебные папки будут созданы.")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new[]
                {
                    CreateCheck(
                        "files.directories",
                        "Служебные папки",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        $"Не удалось проверить служебные папки: {ex.Message}",
                        gameDir)
                };
            }
        }, ct);

    private Task<IReadOnlyList<DoctorCheck>> CheckTempFilesAsync(string gameDir, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DoctorCheck>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (gameDir.Length == 0)
            {
                return new[]
                {
                    Skipped(
                        "files.temp",
                        "Временные файлы",
                        DoctorCategory.Files,
                        "Папка игры не определена.")
                };
            }

            try
            {
                var count = 0;
                var samples = new List<string>();

                foreach (var rootName in new[] { "versions", "libraries", "assets" })
                {
                    var root = Path.Combine(gameDir, rootName);
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(root, "*.tmp", RecursiveFiles))
                    {
                        ct.ThrowIfCancellationRequested();
                        count++;

                        if (samples.Count < MaxDetailsLines)
                        {
                            samples.Add(file);
                        }
                    }
                }

                if (count == 0)
                {
                    return new[]
                    {
                        CreateCheck(
                            "files.temp",
                            "Временные файлы",
                            DoctorCategory.Files,
                            DoctorStatus.Ok,
                            "Временных файлов *.tmp нет.",
                            "")
                    };
                }

                return new[]
                {
                    CreateCheck(
                        "files.temp",
                        "Временные файлы",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        $"Найдены временные файлы: {count}.",
                        string.Join(Nl, samples) +
                        (count > samples.Count ? Nl + $"... и ещё {count - samples.Count}." : ""),
                        true,
                        "Запустите автоисправление — файлы *.tmp будут удалены.")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new[]
                {
                    CreateCheck(
                        "files.temp",
                        "Временные файлы",
                        DoctorCategory.Files,
                        DoctorStatus.Warning,
                        $"Не удалось проверить временные файлы: {ex.Message}",
                        "")
                };
            }
        }, ct);

    private static DoctorCheck CheckSystemLocation(string gameDir)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        if ((!string.IsNullOrWhiteSpace(windows) && IsSameOrUnder(gameDir, windows)) ||
            (!string.IsNullOrWhiteSpace(system) && IsSameOrUnder(gameDir, system)))
        {
            return CreateCheck(
                "gamedir.system",
                "Системная папка",
                DoctorCategory.Files,
                DoctorStatus.Error,
                "Папка игры находится внутри системного каталога Windows.",
                gameDir,
                false,
                "Перенесите игру в отдельную папку, например в %LOCALAPPDATA%\\SamaraLauncher\\instances.");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if ((!string.IsNullOrWhiteSpace(programFiles) && IsSameOrUnder(gameDir, programFiles)) ||
            (!string.IsNullOrWhiteSpace(programFilesX86) && IsSameOrUnder(gameDir, programFilesX86)))
        {
            return CreateCheck(
                "gamedir.system",
                "Системная папка",
                DoctorCategory.Files,
                DoctorStatus.Warning,
                "Папка игры находится в Program Files.",
                gameDir,
                false,
                "Для обновления модов может потребоваться запуск от администратора. " +
                "Лучше перенести игру в папку пользователя.");
        }

        var root = Path.GetPathRoot(gameDir);
        if (!string.IsNullOrWhiteSpace(root) && PathsEqual(gameDir, root))
        {
            return CreateCheck(
                "gamedir.system",
                "Системная папка",
                DoctorCategory.Files,
                DoctorStatus.Warning,
                "Папка игры — корень диска.",
                gameDir,
                false,
                "Используйте отдельную папку, например %LOCALAPPDATA%\\SamaraLauncher\\instances.");
        }

        return CreateCheck(
            "gamedir.system",
            "Системная папка",
            DoctorCategory.Files,
            DoctorStatus.Ok,
            "Папка игры расположена вне системных каталогов.",
            gameDir);
    }

    private static DoctorCheck NetworkOk(HttpStatusCode? status, TimeSpan elapsed)
    {
        var code = status is null ? 0 : (int)status.Value;
        var milliseconds = (int)elapsed.TotalMilliseconds;

        return CreateCheck(
            "network.mojang",
            "Mojang API",
            DoctorCategory.Network,
            DoctorStatus.Ok,
            $"Mojang API доступен (HTTP {code}, {milliseconds} мс).",
            MojangManifestUrl);
    }

    private static async Task<HttpStatusCode?> SendMojangRequestAsync(
        HttpMethod method,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, MojangManifestUrl);
        using var response = await MojangHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        return response.StatusCode;
    }

    private static bool IsSuccessStatus(HttpStatusCode? status) =>
        status.HasValue && (int)status.Value is >= 200 and < 400;

    private static DoctorCheck UnexpectedFailure(string step, Exception ex) =>
        CreateCheck(
            "doctor.unexpected",
            $"Проверка «{step}»",
            DoctorCategory.Files,
            DoctorStatus.Error,
            $"Проверка не выполнена: {ex.Message}",
            ex.GetType().Name,
            false,
            "Повторите диагностику; если ошибка повторяется — сообщите разработчику.");

    private static DoctorCheck CreateCheck(
        string id,
        string title,
        DoctorCategory category,
        DoctorStatus status,
        string message,
        string details = "",
        bool canAutoFix = false,
        string fixHint = "") =>
        new()
        {
            Id = id,
            Title = title,
            Category = category,
            Status = status,
            Message = message,
            Details = details,
            CanAutoFix = canAutoFix,
            FixHint = fixHint
        };

    private static DoctorCheck Skipped(
        string id,
        string title,
        DoctorCategory category,
        string message) =>
        CreateCheck(id, title, category, DoctorStatus.Skipped, message);

    private static string ResolveVersionId(InstanceProfile instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.VersionId))
        {
            return instance.VersionId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(instance.LastVersionId))
        {
            return instance.LastVersionId.Trim();
        }

        var minecraft = (instance.MinecraftVersion ?? "").Trim();
        var forge = (instance.ForgeVersion ?? "").Trim();

        if (minecraft.Length > 0 && forge.Length > 0)
        {
            return $"{minecraft}-forge-{forge}";
        }

        return minecraft;
    }

    private static string BuildJavaSearchDetails(InstanceProfile instance)
    {
        var javaPath = (instance.JavaPath ?? "").Trim();
        var configured = javaPath.Length == 0 ||
                         string.Equals(javaPath, "auto", StringComparison.OrdinalIgnoreCase)
            ? "Поиск: runtime инстанса, JAVA_HOME, PATH, Eclipse Adoptium."
            : $"Указанный путь: {instance.JavaPath}";

        return $"Требуется Java {RequiredJavaMajor}+.{Nl}{configured}";
    }

    private static string NormalizeModKey(string fileName)
    {
        var name = fileName.Trim();

        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".disabled".Length];
        }

        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".jar".Length];
        }

        name = DuplicateSuffixPattern.Replace(name, "");
        name = VersionNumberPattern.Replace(name, "");
        name = SeparatorPattern.Replace(name, "-").Trim('-');

        return name.ToLowerInvariant();
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string TryResolveLocalPack(string raw, string gameDir)
    {
        try
        {
            var trimmed = raw.Trim().Trim('"');

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return Path.GetFullPath(uri.LocalPath);
            }

            var expanded = Environment.ExpandEnvironmentVariables(trimmed);

            if (Path.IsPathRooted(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            return gameDir.Length > 0
                ? Path.GetFullPath(Path.Combine(gameDir, expanded))
                : "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "";
        }
    }

    private static string TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                       PathTooLongException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static int CountFiles(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var count = 0;

        foreach (var _ in Directory.EnumerateFiles(directory, "*", RecursiveFiles))
        {
            ct.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static int CountTopLevelFiles(string directory, string pattern, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var count = 0;

        foreach (var _ in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            count++;
        }

        return count;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
