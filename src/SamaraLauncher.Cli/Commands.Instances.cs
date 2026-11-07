using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static int Instances(LauncherConfig config, InstanceStore store, ParsedArgs args)
    {
        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintInstancesHelp();
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var manager = new InstanceManager(store);

        switch (subcommand)
        {
            case "list":
            {
                var instances = manager.List(config);

                if (WantsJson(args))
                {
                    PrintJson(instances.Select(instance => new
                    {
                        instance.Id,
                        instance.Name,
                        instance.GameDir,
                        instance.VersionId,
                        instance.MinecraftVersion,
                        memory = $"{instance.MinMemoryMb}-{instance.MemoryMb}",
                        instance.Username,
                        instance.LastPlayed,
                        instance.IsFavorite
                    }).ToList());
                    return 0;
                }

                Console.WriteLine($"Конфиг: {store.ConfigPath}");

                if (instances.Count == 0)
                {
                    Console.WriteLine("Инстансов нет. Создайте: instances create \"Название\"");
                    return 0;
                }

                foreach (var instance in instances)
                {
                    var last = instance.LastPlayed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "никогда";
                    var current = string.Equals(instance.Id, config.LastInstanceId, StringComparison.OrdinalIgnoreCase)
                        ? " *"
                        : "";
                    Console.WriteLine(
                        $"{instance.Id,-20} {instance.Name,-26} {instance.GameDir}  " +
                        $"(последний запуск: {last}){current}");
                }

                Console.WriteLine();
                Console.WriteLine($"Инстансов: {instances.Count}. Текущий отмечен *.");
                return 0;
            }

            case "create":
            {
                var name = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.Error.WriteLine("Укажите название инстанса: instances create <name> [--dir <путь>] [--template <id>]");
                    return 2;
                }

                var gameDir = args.GetLast("dir");
                if (!string.IsNullOrWhiteSpace(gameDir))
                {
                    gameDir = ExpandPath(gameDir);
                }

                var instance = manager.Create(config, name, gameDir, args.GetLast("template"));

                if (WantsJson(args))
                {
                    PrintJson(instance);
                    return 0;
                }

                Console.WriteLine($"Создан инстанс \"{instance.Id}\" ({instance.Name})");
                Console.WriteLine($"  Папка: {instance.GameDir}");
                Console.WriteLine($"  Шаблон: {args.GetLast("template") ?? "не использовался"}");
                return 0;
            }

            case "rename":
            {
                var id = rest.ElementAtOrDefault(0);
                var newName = rest.ElementAtOrDefault(1);

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(newName))
                {
                    Console.Error.WriteLine("Укажите id и новое название: instances rename <id> <name>");
                    return 2;
                }

                var instance = config.Find(id);
                if (instance is null)
                {
                    Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
                    return 1;
                }

                var busy = config.Instances.Any(other =>
                    !ReferenceEquals(other, instance) &&
                    string.Equals(other.Name, newName.Trim(), StringComparison.CurrentCultureIgnoreCase));

                if (busy)
                {
                    Console.Error.WriteLine($"Название \"{newName.Trim()}\" уже занято.");
                    return 1;
                }

                if (!manager.Rename(config, id, newName))
                {
                    Console.Error.WriteLine($"Не удалось переименовать инстанс \"{id}\".");
                    return 1;
                }

                if (WantsJson(args))
                {
                    PrintJson(new { id = instance.Id, name = instance.Name });
                    return 0;
                }

                Console.WriteLine($"Инстанс \"{instance.Id}\" переименован: {instance.Name}");
                return 0;
            }

            case "duplicate":
            {
                var id = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine(
                        "Укажите id инстанса: instances duplicate <id> [--name <название>] [--copy-data]");
                    return 2;
                }

                var copy = manager.Duplicate(
                    config,
                    id,
                    args.GetLast("name"),
                    args.Has("copy-data"));

                if (WantsJson(args))
                {
                    PrintJson(copy);
                    return 0;
                }

                Console.WriteLine($"Создана копия \"{copy.Id}\" ({copy.Name})");
                Console.WriteLine($"  Папка: {copy.GameDir}");
                Console.WriteLine($"  Данные (mods/config/defaultconfigs): {(args.Has("copy-data") ? "скопированы" : "не копировались")}");
                return 0;
            }

            case "delete":
            {
                var id = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine("Укажите id инстанса: instances delete <id> [--delete-dir]");
                    return 2;
                }

                var deleteDir = args.Has("delete-dir");

                if (!manager.Remove(config, id, deleteDir))
                {
                    Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
                    return 1;
                }

                if (WantsJson(args))
                {
                    PrintJson(new { id, deleted = true, deletedGameDir = deleteDir });
                    return 0;
                }

                Console.WriteLine(deleteDir
                    ? $"Инстанс \"{id}\" удалён вместе с папкой игры."
                    : $"Инстанс \"{id}\" удалён из конфига (файлы игры не тронуты).");
                return 0;
            }

            case "export":
            {
                var id = rest.ElementAtOrDefault(0);
                var zipPath = rest.ElementAtOrDefault(1);

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(zipPath))
                {
                    Console.Error.WriteLine("Укажите id и путь к zip: instances export <id> <zip>");
                    return 2;
                }

                var instance = config.Find(id);
                if (instance is null)
                {
                    Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
                    return 1;
                }

                var created = manager.Export(instance, zipPath);

                if (WantsJson(args))
                {
                    PrintJson(new { id = instance.Id, path = created });
                    return 0;
                }

                Console.WriteLine($"Инстанс \"{instance.Id}\" экспортирован: {created}");
                return 0;
            }

            case "import":
            {
                var zipPath = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(zipPath))
                {
                    Console.Error.WriteLine("Укажите zip-файл: instances import <zip> [--name <название>]");
                    return 2;
                }

                var instance = manager.Import(config, ExpandPath(zipPath), args.GetLast("name"));

                if (WantsJson(args))
                {
                    PrintJson(instance);
                    return 0;
                }

                Console.WriteLine($"Импортирован инстанс \"{instance.Id}\" ({instance.Name})");
                Console.WriteLine($"  Папка: {instance.GameDir}");
                return 0;
            }

            case "use":
            {
                var id = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine("Укажите id инстанса: instances use <id>");
                    return 2;
                }

                var instance = config.Find(id);
                if (instance is null)
                {
                    Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
                    return 1;
                }

                config.LastInstanceId = instance.Id;
                store.Save(config);

                if (WantsJson(args))
                {
                    PrintJson(new { current = instance.Id });
                    return 0;
                }

                Console.WriteLine($"Текущий инстанс: {instance.Id} ({instance.Name})");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда instances: {subcommand}");
                PrintInstancesHelp();
                return 2;
        }
    }

    private static void PrintInstancesHelp()
    {
        Console.WriteLine("""
            Использование:
              instances list [--json] [--config <путь>]
              instances create <name> [--dir <путь>] [--template <id>] [--json]
              instances rename <id> <name> [--json]
              instances duplicate <id> [--name <название>] [--copy-data] [--json]
              instances delete <id> [--delete-dir] [--json]
              instances export <id> <zip> [--json]
              instances import <zip> [--name <название>] [--json]
              instances use <id> [--json]

            --dir       папка игры нового инстанса (по умолчанию — внутри данных лаунчера).
            --template  id инстанса, из которого копируются Java, память, версия и PackUrl.
            --copy-data копировать mods, config и defaultconfigs при дублировании.
            --delete-dir удалить папку игры вместе с инстансом (только внутри папки инстансов).
            """);
    }
}
