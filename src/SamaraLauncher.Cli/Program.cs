using System.Text;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Launch;
using SamaraLauncher.Core.Models;
using LaunchPlan = SamaraLauncher.Core.Launch.LaunchPlan;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Вывод перенаправлен в файл — кодировка не важна.
        }
        catch (PlatformNotSupportedException)
        {
            // Платформа без консоли — остаёмся на кодировке по умолчанию.
        }

        try
        {
            return await RunAsync(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено.");
            return 130;
        }
        catch (LauncherException ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Непредвиденная ошибка: " + ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] rawArgs)
    {
        var parsed = ParsedArgs.Parse(rawArgs);
        var configPath = parsed.GetLast("config");
        var store = new InstanceStore(configPath);
        var config = store.Load();

        var command = parsed.Positional.FirstOrDefault()?.ToLowerInvariant();

        if (parsed.Has("help") || parsed.Has("h") || command is "help" or "-h" or "/?")
        {
            PrintHelp();
            return 0;
        }

        // Альтернативный синтаксис: SamaraLauncher.Cli --instance samara --launch
        if (command is null)
        {
            if (parsed.Has("launch"))
            {
                return await LaunchAsync(store, config, parsed);
            }

            // Флаговые псевдонимы команд из README.
            if (parsed.Has("install"))
            {
                return await InstallAsync(store, config, parsed);
            }

            if (parsed.Has("update"))
            {
                return await UpdateAsync(config, parsed);
            }

            if (parsed.Has("verify"))
            {
                return await VerifyAsync(config, parsed);
            }

            if (parsed.Has("backup"))
            {
                return await BackupAsync(config, parsed);
            }

            if (parsed.Has("list"))
            {
                return List(config, store);
            }
        }

        switch (command)
        {
            case null:
                PrintHelp();
                return 2;

            case "list":
                return List(config, store);

            case "add":
                return Add(store, config, parsed);

            case "remove":
                return Remove(store, config, parsed);

            case "info":
                return Info(config, store, parsed);

            case "doctor":
                return await DoctorAsync(config, parsed);

            case "launch":
                return await LaunchAsync(store, config, parsed);

            case "install":
                return await InstallAsync(store, config, parsed);

            case "update":
                return await UpdateAsync(config, parsed);

            case "verify":
                return await VerifyAsync(config, parsed);

            case "backup":
                return await BackupAsync(config, parsed);

            case "mods":
                return await ModsAsync(config, parsed);

            case "instances":
                return Instances(config, store, parsed);

            case "java":
                return await JavaAsync(config, parsed);

            case "worlds":
                return await WorldsAsync(config, parsed);

            case "servers":
                return await ServersAsync(config, parsed);

            case "resources":
                return Resources(config, parsed);

            case "shaders":
                return Shaders(config, parsed);

            case "crash":
                return Crash(config, parsed);

            case "logs":
                return await LogsAsync(config, parsed);

            default:
                Console.Error.WriteLine($"Неизвестная команда: {command}");
                PrintHelp();
                return 2;
        }
    }

    private static int List(LauncherConfig config, InstanceStore store)
    {
        Console.WriteLine($"Конфиг: {store.ConfigPath}");

        if (config.Instances.Count == 0)
        {
            Console.WriteLine("Инстансов нет. Добавьте: add samara --game-dir <путь>");
            return 0;
        }

        foreach (var instance in config.Instances)
        {
            var last = instance.LastPlayed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "никогда";
            Console.WriteLine($"{instance.Id,-16} {instance.Name,-24} {instance.GameDir}  (последний запуск: {last})");
        }

        return 0;
    }

    private static int Add(InstanceStore store, LauncherConfig config, ParsedArgs args)
    {
        var id = args.Positional.Skip(1).FirstOrDefault() ?? args.GetLast("instance");
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("Укажите id инстанса: add <id> --game-dir <путь>");
            return 2;
        }

        if (config.Find(id) is not null)
        {
            Console.Error.WriteLine($"Инстанс \"{id}\" уже существует. Удалите его или выберите другой id.");
            return 1;
        }

        var gameDir = args.GetLast("game-dir") ?? args.GetLast("gamedir");
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            Console.Error.WriteLine("Укажите папку игры: --game-dir <путь>");
            return 2;
        }

        gameDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(gameDir.Trim().Trim('"')));
        if (!Directory.Exists(gameDir))
        {
            Console.Error.WriteLine($"Папка игры не существует: {gameDir}");
            return 1;
        }

        var username = args.GetLast("username") ?? "Player";
        var uuid = args.GetLast("uuid");
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            uuid = OfflineIdentity.NormalizeUuid(uuid);
        }

        var instance = new InstanceProfile
        {
            Id = id,
            Name = args.GetLast("name") ?? id,
            GameDir = gameDir,
            JavaPath = args.GetLast("java") ?? "auto",
            MemoryMb = ParseInt(args.GetLast("memory"), 6144),
            MinMemoryMb = ParseInt(args.GetLast("min-memory"), 2048),
            VersionId = args.GetLast("version") ?? "",
            MinecraftVersion = args.GetLast("mc-version") ?? "",
            Username = username,
            Uuid = uuid,
            PackUrl = args.GetLast("pack-url") ?? ""
        };

        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            try
            {
                instance.VersionId = VersionManifestLoader.DetectVersionId(gameDir, instance.MinecraftVersion);
            }
            catch (LauncherException)
            {
                Console.Error.WriteLine("[!] Не удалось автоматически определить Forge-версию — укажите --version.");
            }
        }

        config.Instances.Add(instance);
        store.Save(config);

        Console.WriteLine($"Добавлен инстанс \"{instance.Id}\" ({instance.Name})");
        Console.WriteLine($"  Папка: {instance.GameDir}");
        Console.WriteLine($"  Версия: {(string.IsNullOrWhiteSpace(instance.VersionId) ? "не определена" : instance.VersionId)}");
        Console.WriteLine($"  Память: {instance.MinMemoryMb}–{instance.MemoryMb} МБ");
        Console.WriteLine($"  Ник: {instance.Username}, UUID: {(string.IsNullOrWhiteSpace(instance.Uuid) ? "авто (offline)" : instance.Uuid)}");
        return 0;
    }

    private static int Remove(InstanceStore store, LauncherConfig config, ParsedArgs args)
    {
        var id = args.Positional.Skip(1).FirstOrDefault() ?? args.GetLast("instance");
        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("Укажите id инстанса: remove <id>");
            return 2;
        }

        if (!config.Remove(id))
        {
            Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
            return 1;
        }

        store.Save(config);
        Console.WriteLine($"Инстанс \"{id}\" удалён из конфига (файлы игры не тронуты).");
        return 0;
    }

    private static int Info(LauncherConfig config, InstanceStore store, ParsedArgs args)
    {
        var instance = ResolveInstance(config, args);
        if (instance is null)
        {
            return 1;
        }

        Console.WriteLine($"Конфиг:            {store.ConfigPath}");
        Console.WriteLine($"Id:                {instance.Id}");
        Console.WriteLine($"Название:          {instance.Name}");
        Console.WriteLine($"Папка игры:        {instance.GameDir}");
        Console.WriteLine($"Версия:            {instance.VersionId}");
        Console.WriteLine($"Minecraft:         {instance.MinecraftVersion}");
        Console.WriteLine($"Java:              {instance.JavaPath}");
        Console.WriteLine($"Память:            {instance.MinMemoryMb}–{instance.MemoryMb} МБ");
        Console.WriteLine($"JVM-аргументы:     {string.Join(' ', instance.JvmArgs)}");
        Console.WriteLine($"Ник:               {instance.Username}");
        Console.WriteLine($"UUID:              {instance.Uuid}");
        Console.WriteLine($"pack.toml:         {instance.PackUrl}");
        Console.WriteLine($"Последний запуск:  {instance.LastPlayed?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        return 0;
    }

    private static int Doctor(LauncherConfig config, InstanceStore store, ParsedArgs args)
    {
        var instance = ResolveInstance(config, args);
        if (instance is null)
        {
            return 1;
        }

        var gameDir = Path.GetFullPath(instance.GameDir);
        var problems = 0;

        void Check(string title, bool ok, string? detail = null)
        {
            Console.WriteLine($"[{(ok ? "OK" : "!!")}] {title}{(detail is null ? "" : ": " + detail)}");
            if (!ok)
            {
                problems++;
            }
        }

        void Note(string title, string detail)
        {
            Console.WriteLine($"[--] {title}: {detail}");
        }

        Check("Конфиг", File.Exists(store.ConfigPath), store.ConfigPath);
        Check("Папка игры", Directory.Exists(gameDir), gameDir);

        var versionId = instance.VersionId;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            try
            {
                versionId = VersionManifestLoader.DetectVersionId(gameDir, instance.MinecraftVersion);
            }
            catch (LauncherException)
            {
                versionId = "";
            }
        }

        var mcVersion = string.IsNullOrWhiteSpace(instance.MinecraftVersion)
            ? versionId.Split("-forge-")[0]
            : instance.MinecraftVersion;

        var forgeJson = Path.Combine(gameDir, "versions", versionId, versionId + ".json");
        var vanillaJson = Path.Combine(gameDir, "versions", mcVersion, mcVersion + ".json");
        Check("Forge version json", File.Exists(forgeJson), forgeJson);
        Check("Vanilla version json", File.Exists(vanillaJson), vanillaJson);
        Check("Forge jar", File.Exists(Path.Combine(gameDir, "versions", versionId, versionId + ".jar"))
                          || Directory.Exists(Path.Combine(gameDir, "libraries", "net", "minecraftforge", "forge")));

        var java = "";
        try
        {
            java = JavaLocator.Resolve(gameDir, instance.JavaPath);
            Check("Java", java == "java" || File.Exists(java), java);
        }
        catch (LauncherException ex)
        {
            Check("Java", false, ex.Message);
        }

        Check("Assets", Directory.Exists(Path.Combine(gameDir, "assets")));
        Check("Natives", Directory.Exists(Path.Combine(gameDir, "versions", versionId, "natives")));

        var modsDir = Path.Combine(gameDir, "mods");
        var modCount = Directory.Exists(modsDir) ? Directory.EnumerateFiles(modsDir, "*.jar").Count() : 0;
        Check("Моды", modCount > 0, $"{modCount} jar в {modsDir}");

        var packCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.PackUrl) && !instance.PackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            packCandidates.Add(instance.PackUrl);
        }

        packCandidates.Add(Path.Combine(gameDir, "pack.toml"));
        packCandidates.Add(Path.GetFullPath(Path.Combine(gameDir, "..", "packwiz", "pack.toml")));
        var foundPack = packCandidates.FirstOrDefault(File.Exists);
        Check("pack.toml", foundPack is not null, foundPack ?? string.Join(" | ", packCandidates));

        Note("overrides.json", File.Exists(Path.Combine(gameDir, "overrides.json"))
            ? "есть"
            : "пока нет (появится на этапе 2)");

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "Проблем не найдено." : $"Проблем: {problems}.");
        return problems == 0 ? 0 : 1;
    }

    private static async Task<int> LaunchAsync(InstanceStore store, LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstance(config, args);
        if (instance is null)
        {
            return 1;
        }

        var options = new LaunchOptions
        {
            Username = args.GetLast("username"),
            Uuid = args.GetLast("uuid"),
            MemoryMb = ParseNullableInt(args.GetLast("memory")),
            MinMemoryMb = ParseNullableInt(args.GetLast("min-memory")),
            JavaPath = args.GetLast("java"),
            VersionId = args.GetLast("version")
        };

        foreach (var jvmArg in args.GetAll("jvm-arg"))
        {
            options.ExtraJvmArgs.Add(jvmArg);
        }

        LaunchPlan plan;
        try
        {
            plan = new LaunchPlanBuilder().Build(instance, options);
        }
        catch (LauncherException ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine("[!] " + warning);
        }

        if (args.Has("dry-run"))
        {
            Console.WriteLine($"Java:    {plan.JavaPath}");
            Console.WriteLine($"Папка:   {plan.WorkingDirectory}");
            Console.WriteLine($"Класс:   {plan.MainClass}");
            Console.WriteLine($"Аргументов: {plan.Arguments.Count}");
            Console.WriteLine();
            Console.WriteLine(plan.ToDisplayCommand());
            return 0;
        }

        instance.LastPlayed = DateTimeOffset.UtcNow;
        store.Save(config);

        var logDir = Path.Combine(Path.GetDirectoryName(store.ConfigPath)!, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        await using var file = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var tee = new TeeTextWriter(Console.Out, file);

        tee.WriteLine($"=== Лаунчер {LauncherBanner.Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        tee.WriteLine($"Инстанс: {instance.Id} ({instance.Name})");
        tee.WriteLine($"Java:     {plan.JavaPath}");
        tee.WriteLine($"Лог:      {logPath}");
        tee.WriteLine();

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Останавливаю игру...");
            cts.Cancel();
        };

        Console.CancelKeyPress += handler;

        try
        {
            var exitCode = await new GameProcessLauncher().RunAsync(
                plan,
                tee,
                pid => tee.WriteLine($"Java PID: {pid}"),
                cts.Token);

            tee.WriteLine();
            tee.WriteLine($"Игра завершилась с кодом {exitCode}.");
            if (exitCode != 0)
            {
                tee.WriteLine("Если игра упала сразу — смотрите crash-reports в папке игры и этот лог.");
            }

            return exitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            tee.Flush();
        }
    }

    private static InstanceProfile? ResolveInstance(LauncherConfig config, ParsedArgs args)
    {
        var id = args.GetLast("instance")
                 ?? args.Positional.Skip(1).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(id))
        {
            if (config.Instances.Count == 1)
            {
                return config.Instances[0];
            }

            Console.Error.WriteLine(config.Instances.Count == 0
                ? "Инстансов нет. Сначала добавьте: add samara --game-dir <путь>"
                : "Укажите инстанс: --instance <id>");
            return null;
        }

        var instance = config.Find(id);
        if (instance is null)
        {
            Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
        }

        return instance;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            Лаунчер CLI {LauncherBanner.Version}

            Использование:
              SamaraLauncher.Cli list
              SamaraLauncher.Cli add <id> --game-dir <путь> [--name "Сборка"] [--memory 6144]
              SamaraLauncher.Cli info [<id>] [--config <путь>]
              SamaraLauncher.Cli launch [<id>] [--dry-run] [--username ник] [--memory МБ]
              SamaraLauncher.Cli remove <id>
              SamaraLauncher.Cli --instance <id> --launch      (альтернативный синтаксис)

            Инстансы:
              SamaraLauncher.Cli instances list [--json]
              SamaraLauncher.Cli instances create <name> [--dir <путь>] [--template <id>]
              SamaraLauncher.Cli instances rename <id> <name>
              SamaraLauncher.Cli instances duplicate <id> [--name <название>] [--copy-data]
              SamaraLauncher.Cli instances delete <id> [--delete-dir]
              SamaraLauncher.Cli instances export <id> <zip>
              SamaraLauncher.Cli instances import <zip> [--name <название>]
              SamaraLauncher.Cli instances use <id>

            Java:
              SamaraLauncher.Cli java list [--json]
              SamaraLauncher.Cli java find [--instance <id>] [--json]
              SamaraLauncher.Cli java install <8|17|21> [--instance <id>] [--json]

            Установка и обслуживание:
              SamaraLauncher.Cli install [--instance <id>]
                  Доустановить/починить Java, Minecraft, Forge и natives,
                  затем установить packwiz-сборку, если задан PackUrl.
              SamaraLauncher.Cli update [--instance <id>] [--check]
                  Обновить packwiz-сборку (перед обновлением создаётся бэкап).
                  --check — только проверить наличие обновления.
              SamaraLauncher.Cli verify [--instance <id>] [--restore-protected]
                  Проверить целостность сборки; exit 1 при расхождениях.
                  --restore-protected — вернуть защищённые файлы из последнего бэкапа.
              SamaraLauncher.Cli backup [--instance <id>] [--list | --restore <файл>]
                  Создать бэкап mods/config/defaultconfigs, показать список или восстановить.
              SamaraLauncher.Cli doctor [<id>] [--fix] [--json]
                  Диагностика инстанса; exit 1 при найденных ошибках.
                  --fix — безопасные автоисправления и число исправленных пунктов.
              SamaraLauncher.Cli crash [--open] [--instance <id>]
                  Показать последний crash-report; --open — открыть папку crash-reports.
              SamaraLauncher.Cli logs [--lines N] [--follow] [--instance <id>]
                  Показать хвост latest.log; --follow — следить до Ctrl+C.

            Моды:
              SamaraLauncher.Cli mods list|enable <файл>|disable <файл>|import <jar...>|folder
                  Управление модами в папке mods (имя файла без учёта регистра).
              SamaraLauncher.Cli mods analyze [--json]
                  Дубликаты modId и проблемы зависимостей; exit 1 при проблемах.
              SamaraLauncher.Cli mods updates [--check] [--json]
                  Проверить обновления модов через Modrinth.
              SamaraLauncher.Cli mods update-all [--yes] [--json]
                  Скачать доступные обновления; --yes — без подтверждения.

            Миры:
              SamaraLauncher.Cli worlds list [--json]
              SamaraLauncher.Cli worlds backup <folder> [--to <папка>]
              SamaraLauncher.Cli worlds restore <zip> --world <folder> [--overwrite]
              SamaraLauncher.Cli worlds duplicate <folder> [--name <название>]
              SamaraLauncher.Cli worlds export <folder> <zip>
              SamaraLauncher.Cli worlds import <zip> [--name <название>] [--overwrite]
              SamaraLauncher.Cli worlds delete <folder> [--permanent]
              SamaraLauncher.Cli worlds folder

            Серверы:
              SamaraLauncher.Cli servers list [--json]
              SamaraLauncher.Cli servers ping <host[:port]> [--json]
              SamaraLauncher.Cli servers add <name> <host[:port]>
              SamaraLauncher.Cli servers remove <host[:port]>
              SamaraLauncher.Cli servers move <from> <to>

            Ресурсы:
              SamaraLauncher.Cli resources list|enable <файл>|disable <файл>|folder
              SamaraLauncher.Cli resources import <zip...>
              SamaraLauncher.Cli shaders list|enable <файл>|disable [<файл>]|folder
              SamaraLauncher.Cli shaders import <zip...>

            Общие опции:
              --config <путь>   свой config.json (по умолчанию %APPDATA%\SamaraLauncher\config.json)
              --instance <id>   id инстанса
              --json            машиночитаемый вывод (где поддерживается)
              --dry-run         показать команду, не запускать игру
              --java <путь>     переопределить java.exe
              --version <id>    переопределить id Forge-версии
              --check           только проверить обновление (update/mods updates)
              --restore-protected  восстановить защищённые файлы (verify)
              --list            показать список бэкапов (backup)
              --restore <файл>  восстановить бэкап (backup)
              --lines N         сколько строк лога показать (logs)
              --follow          следить за latest.log (logs)
              --open            открыть папку crash-reports (crash)
              --yes             подтвердить действие без вопроса (mods update-all)
              --overwrite       заменить существующий мир (worlds restore/import)
              --to <папка>      папка для zip-бэкапа мира (worlds backup)
              --permanent       удалить мир безвозвратно (worlds delete)
              --delete-dir      удалить папку игры (instances delete)
              --copy-data       копировать mods/config/defaultconfigs (instances duplicate)
              --dir <путь>      папка игры нового инстанса (instances create)
              --template <id>   шаблон настроек нового инстанса (instances create)
              --name <название> имя копии/импорта (instances/worlds)
              --world <folder>  папка мира для восстановления (worlds restore)

            Совместимость с README:
              --list, --install, --update, --verify, --backup
              (то же самое, что команды list/install/update/verify/backup)
            """);
    }

    /// <summary>
    /// Минимальный парсер: --key value, --flag, повторяемые --jvm-arg.
    /// </summary>
    private sealed class ParsedArgs
    {
        private readonly List<(string Name, string? Value)> _items = new();
        private readonly List<string> _positional = new();

        public IReadOnlyList<string> Positional => _positional;

        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith("-", StringComparison.Ordinal))
                {
                    var name = arg.TrimStart('-').ToLowerInvariant();
                    string? value = null;

                    // --jvm-arg почти всегда начинается с "-", поэтому забираем значение всегда.
                    var alwaysTakesValue = name is "jvm-arg";

                    if (i + 1 < args.Length
                        && (alwaysTakesValue || !args[i + 1].StartsWith("-", StringComparison.Ordinal)))
                    {
                        value = args[++i];
                    }

                    parsed._items.Add((name, value));
                }
                else
                {
                    parsed._positional.Add(arg);
                }
            }

            return parsed;
        }

        public bool Has(string name) =>
            _items.Any(item => item.Name == name);

        public string? GetLast(string name) =>
            _items.LastOrDefault(item => item.Name == name).Value;

        public IEnumerable<string> GetAll(string name) =>
            _items.Where(item => item.Name == name && item.Value is not null).Select(item => item.Value!);
    }
}

internal static class LauncherBanner
{
    public static readonly string Version = LauncherVersion.Current;
}
