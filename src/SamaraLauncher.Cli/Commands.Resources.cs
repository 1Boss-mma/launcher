using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Resources;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static int Resources(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintResourcesHelp(shaders: false);
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var resources = new ResourcePackService();
        var folder = resources.GetFolder(instance);

        switch (subcommand)
        {
            case "list":
                PrintPacks(resources.List(instance), folder, "Ресурспаки", WantsJson(args));
                return 0;

            case "enable":
            {
                var fileName = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    Console.Error.WriteLine("Укажите файл: resources enable <файл>");
                    return 2;
                }

                var pack = FindPack(resources.List(instance), fileName);
                resources.SetEnabled(instance, pack.FileName, true);

                if (WantsJson(args))
                {
                    PrintJson(new { fileName = pack.FileName, enabled = true });
                    return 0;
                }

                Console.WriteLine($"Ресурспак включён: {pack.FileName}");
                return 0;
            }

            case "disable":
            {
                var fileName = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    Console.Error.WriteLine("Укажите файл: resources disable <файл>");
                    return 2;
                }

                var pack = FindPack(resources.List(instance), fileName);
                resources.SetEnabled(instance, pack.FileName, false);

                if (WantsJson(args))
                {
                    PrintJson(new { fileName = pack.FileName, enabled = false });
                    return 0;
                }

                Console.WriteLine($"Ресурспак отключён: {pack.FileName}");
                return 0;
            }

            case "import":
            {
                if (rest.Count == 0)
                {
                    Console.Error.WriteLine("Укажите zip-файлы: resources import <zip...>");
                    return 2;
                }

                var imported = resources.Import(
                    instance,
                    rest.Select(ExpandPath),
                    WantsJson(args) ? null : new ConsoleProgress());

                if (WantsJson(args))
                {
                    PrintJson(imported.Select(ToPackJson).ToList());
                    return 0;
                }

                foreach (var pack in imported)
                {
                    Console.WriteLine($"Импортирован ресурспак: {pack.FileName} ({CliText.FormatSize(pack.Size)})");
                }

                Console.WriteLine($"Импортировано ресурспаков: {imported.Count}.");
                return 0;
            }

            case "folder":
                resources.OpenFolder(instance);
                Console.WriteLine($"Открыта папка ресурспаков: {folder}");
                return 0;

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда resources: {subcommand}");
                PrintResourcesHelp(shaders: false);
                return 2;
        }
    }

    private static int Shaders(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintResourcesHelp(shaders: true);
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var shaders = new ShaderPackService();
        var folder = shaders.GetFolder(instance);

        switch (subcommand)
        {
            case "list":
                PrintPacks(shaders.List(instance), folder, "Шейдерпаки", WantsJson(args));
                return 0;

            case "enable":
            {
                var fileName = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    Console.Error.WriteLine("Укажите файл: shaders enable <файл>");
                    return 2;
                }

                var pack = FindPack(shaders.List(instance), fileName);
                shaders.SetEnabled(instance, pack.FileName, true);

                if (WantsJson(args))
                {
                    PrintJson(new { fileName = pack.FileName, enabled = true });
                    return 0;
                }

                Console.WriteLine($"Шейдерпак включён: {pack.FileName}");
                return 0;
            }

            case "disable":
            {
                var active = shaders.GetEnabled(instance);
                var fileName = rest.FirstOrDefault() ?? active.FirstOrDefault();

                if (string.IsNullOrWhiteSpace(fileName))
                {
                    if (WantsJson(args))
                    {
                        PrintJson(new { enabled = false, changed = false });
                    }
                    else
                    {
                        Console.WriteLine("Шейдерпак не включён.");
                    }

                    return 0;
                }

                var pack = FindPack(shaders.List(instance), fileName);
                shaders.SetEnabled(instance, pack.FileName, false);

                if (WantsJson(args))
                {
                    PrintJson(new { fileName = pack.FileName, enabled = false });
                    return 0;
                }

                Console.WriteLine($"Шейдерпак отключён: {pack.FileName}");
                return 0;
            }

            case "import":
            {
                if (rest.Count == 0)
                {
                    Console.Error.WriteLine("Укажите zip-файлы: shaders import <zip...>");
                    return 2;
                }

                var imported = shaders.Import(
                    instance,
                    rest.Select(ExpandPath),
                    WantsJson(args) ? null : new ConsoleProgress());

                if (WantsJson(args))
                {
                    PrintJson(imported.Select(ToPackJson).ToList());
                    return 0;
                }

                foreach (var pack in imported)
                {
                    Console.WriteLine($"Импортирован шейдерпак: {pack.FileName} ({CliText.FormatSize(pack.Size)})");
                }

                Console.WriteLine($"Импортировано шейдерпаков: {imported.Count}.");
                return 0;
            }

            case "folder":
                shaders.OpenFolder(instance);
                Console.WriteLine($"Открыта папка шейдерпаков: {folder}");
                return 0;

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда shaders: {subcommand}");
                PrintResourcesHelp(shaders: true);
                return 2;
        }
    }

    private static void PrintPacks(
        IReadOnlyList<PackInfo> packs,
        string folder,
        string title,
        bool json)
    {
        if (json)
        {
            PrintJson(packs.Select(ToPackJson).ToList());
            return;
        }

        Console.WriteLine($"Папка: {folder}");

        if (packs.Count == 0)
        {
            Console.WriteLine($"{title}: нет.");
            return;
        }

        Console.WriteLine($"{title} ({packs.Count}):");

        foreach (var pack in packs)
        {
            var description = string.IsNullOrWhiteSpace(pack.Description)
                ? ""
                : " — " + pack.Description.Replace('\r', ' ').Replace('\n', ' ');

            Console.WriteLine(
                $"  [{(pack.Enabled ? "вкл" : "выкл")}] {pack.FileName,-40} " +
                $"{CliText.FormatSize(pack.Size),10}{description}");
        }

        Console.WriteLine($"Включено: {packs.Count(pack => pack.Enabled)}.");
    }

    private static PackJson ToPackJson(PackInfo pack) => new(
        pack.FileName,
        pack.Title,
        pack.Description,
        pack.Size,
        pack.Enabled,
        pack.Format,
        pack.FullPath,
        pack.ModifiedAt);

    private static PackInfo FindPack(IReadOnlyList<PackInfo> packs, string rawName)
    {
        var name = Path.GetFileName(rawName.Trim().Trim('"'));

        var exact = packs.FirstOrDefault(pack =>
            string.Equals(pack.FileName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pack.Title, name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var matches = packs
            .Where(pack => pack.FileName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new LauncherException(
                $"Под \"{name}\" подходит несколько паков: " +
                string.Join(", ", matches.Select(pack => pack.FileName)) +
                ". Укажите точное имя файла.");
        }

        throw new LauncherException($"Пак не найден: {name}.");
    }

    private static void PrintResourcesHelp(bool shaders)
    {
        var command = shaders ? "shaders" : "resources";
        var title = shaders ? "шейдерпаками" : "ресурспаками";

        Console.WriteLine($"""
            Использование:
              {command} list [--instance <id>] [--json]
              {command} enable <файл> [--instance <id>] [--json]
              {command} disable{(shaders ? " [<файл>]" : " <файл>")} [--instance <id>] [--json]
              {command} import <zip...> [--instance <id>] [--json]
              {command} folder [--instance <id>]

            Управление {title} инстанса. Имя файла ищется без учёта регистра;
            {(shaders ? "без файла disable выключает активный шейдерпак." : "для enable/disable укажите файл пака.")}
            """);
    }

    private sealed record PackJson(
        string FileName,
        string Title,
        string Description,
        long Size,
        bool Enabled,
        string Format,
        string FullPath,
        DateTimeOffset ModifiedAt);
}
