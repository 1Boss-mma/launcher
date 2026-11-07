using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Worlds;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> WorldsAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintWorldsHelp();
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var worlds = new WorldService();

        switch (subcommand)
        {
            case "list":
            {
                var list = worlds.List(instance);

                if (WantsJson(args))
                {
                    PrintJson(list.Select(world => new
                    {
                        world.Name,
                        world.DisplayName,
                        world.Path,
                        world.SizeBytes,
                        world.LastPlayed,
                        world.GameType,
                        world.Difficulty,
                        world.Hardcore,
                        world.VersionName,
                        world.IsValid
                    }).ToList());
                    return 0;
                }

                Console.WriteLine($"Папка миров: {worlds.GetSavesDirectory(instance)}");

                if (list.Count == 0)
                {
                    Console.WriteLine("Миров нет.");
                    return 0;
                }

                foreach (var world in list)
                {
                    var mark = world.IsValid ? "OK" : "!!";
                    var last = world.LastPlayed == DateTimeOffset.MinValue
                        ? "неизвестно"
                        : world.LastPlayed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                    Console.WriteLine(
                        $"[{mark}] {world.Name,-28} {world.DisplayName,-30} " +
                        $"{CliText.FormatSize(world.SizeBytes),10}  {last}");
                }

                Console.WriteLine();
                Console.WriteLine($"Миров: {list.Count}.");
                return 0;
            }

            case "backup":
            {
                var folder = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("Укажите папку мира: worlds backup <folder>");
                    return 2;
                }

                using var cancellation = new CliCancellation();
                var json = WantsJson(args);
                var destination = args.GetLast("to");
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    destination = ExpandPath(destination);
                }

                var zipPath = await worlds.CreateBackupAsync(
                    instance,
                    folder,
                    destination,
                    json ? null : new ConsoleProgress(),
                    cancellation.Token);

                if (json)
                {
                    PrintJson(new { world = folder, backup = zipPath });
                    return 0;
                }

                Console.WriteLine($"Бэкап мира создан: {zipPath}");
                return 0;
            }

            case "restore":
            {
                var zipPath = rest.FirstOrDefault();
                var worldName = args.GetLast("world");

                if (string.IsNullOrWhiteSpace(zipPath) || string.IsNullOrWhiteSpace(worldName))
                {
                    Console.Error.WriteLine(
                        "Укажите архив и мир: worlds restore <zip> --world <folder> [--overwrite]");
                    return 2;
                }

                using var cancellation = new CliCancellation();
                var restored = await RestoreWorldToFolderAsync(
                    worlds,
                    instance,
                    ExpandPath(zipPath),
                    worldName,
                    args.Has("overwrite"),
                    WantsJson(args) ? null : new ConsoleProgress(),
                    cancellation.Token);

                if (WantsJson(args))
                {
                    PrintJson(new { world = worldName, path = restored });
                    return 0;
                }

                Console.WriteLine($"Мир восстановлен: {restored}");
                return 0;
            }

            case "duplicate":
            {
                var folder = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("Укажите папку мира: worlds duplicate <folder> [--name <название>]");
                    return 2;
                }

                using var cancellation = new CliCancellation();
                var created = await worlds.DuplicateAsync(
                    instance,
                    folder,
                    args.GetLast("name"),
                    cancellation.Token);

                if (WantsJson(args))
                {
                    PrintJson(new { source = folder, path = created });
                    return 0;
                }

                Console.WriteLine($"Создана копия мира: {created}");
                return 0;
            }

            case "export":
            {
                var folder = rest.ElementAtOrDefault(0);
                var target = rest.ElementAtOrDefault(1);

                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(target))
                {
                    Console.Error.WriteLine("Укажите мир и zip-файл: worlds export <folder> <zip>");
                    return 2;
                }

                using var cancellation = new CliCancellation();
                var json = WantsJson(args);

                try
                {
                    var targetPath = ExpandPath(target);
                    var isDirectory = Directory.Exists(targetPath)
                                      || target.EndsWith('\\')
                                      || target.EndsWith('/');
                    var destination = isDirectory
                        ? targetPath
                        : Path.GetDirectoryName(targetPath)
                          ?? throw new LauncherException($"Некорректный путь экспорта: {target}");

                    if (!isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                    }

                    var created = await worlds.ExportAsync(
                        instance,
                        folder,
                        destination,
                        json ? null : new ConsoleProgress(),
                        cancellation.Token);

                    if (!isDirectory)
                    {
                        if (!string.Equals(created, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(created, targetPath, overwrite: true);
                        }

                        created = targetPath;
                    }

                    if (json)
                    {
                        PrintJson(new { world = folder, path = created });
                        return 0;
                    }

                    Console.WriteLine($"Мир экспортирован: {created}");
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Не удалось экспортировать мир: {ex.Message}");
                    return 1;
                }
            }

            case "import":
            {
                var zipPath = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(zipPath))
                {
                    Console.Error.WriteLine("Укажите zip-файл: worlds import <zip> [--name <название>]");
                    return 2;
                }

                using var cancellation = new CliCancellation();
                var name = args.GetLast("name");
                var fullZip = ExpandPath(zipPath);

                var created = string.IsNullOrWhiteSpace(name)
                    ? await worlds.ImportAsync(
                        fullZip,
                        instance,
                        args.Has("overwrite"),
                        cancellation.Token)
                    : await RestoreWorldToFolderAsync(
                        worlds,
                        instance,
                        fullZip,
                        name,
                        args.Has("overwrite"),
                        WantsJson(args) ? null : new ConsoleProgress(),
                        cancellation.Token);

                if (WantsJson(args))
                {
                    PrintJson(new { path = created });
                    return 0;
                }

                Console.WriteLine($"Мир импортирован: {created}");
                return 0;
            }

            case "delete":
            {
                var folder = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(folder))
                {
                    Console.Error.WriteLine("Укажите папку мира: worlds delete <folder> [--permanent]");
                    return 2;
                }

                var permanent = args.Has("permanent");

                if (!worlds.Delete(instance, folder, moveToBackups: !permanent))
                {
                    Console.Error.WriteLine($"Мир не найден: {folder}");
                    return 1;
                }

                if (WantsJson(args))
                {
                    PrintJson(new { world = folder, deleted = true, permanent });
                    return 0;
                }

                Console.WriteLine(permanent
                    ? $"Мир \"{folder}\" удалён безвозвратно."
                    : $"Мир \"{folder}\" перенесён в backups\\removed-worlds.");
                return 0;
            }

            case "folder":
            {
                worlds.OpenFolder(instance);
                Console.WriteLine($"Открыта папка миров: {worlds.GetSavesDirectory(instance)}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда worlds: {subcommand}");
                PrintWorldsHelp();
                return 2;
        }
    }

    /// <summary>
    /// Восстанавливает zip в папку с заданным именем: сначала безопасно распаковывает
    /// во временную папку через WorldService, затем переносит её в saves.
    /// </summary>
    private static async Task<string> RestoreWorldToFolderAsync(
        WorldService worlds,
        InstanceProfile instance,
        string zipPath,
        string targetName,
        bool overwrite,
        IProgress<LauncherProgress>? progress,
        CancellationToken ct)
    {
        var name = RequireWorldFolderName(targetName);
        var savesRoot = worlds.GetSavesDirectory(instance);

        try
        {
            Directory.CreateDirectory(savesRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось создать папку миров {savesRoot}: {ex.Message}", ex);
        }

        var target = Path.GetFullPath(Path.Combine(savesRoot, name));

        if (!IsPathInside(target, savesRoot) ||
            string.Equals(target, Path.GetFullPath(savesRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"Недопустимое имя папки мира: {targetName}");
        }

        if (File.Exists(target))
        {
            throw new LauncherException($"По пути {target} находится файл — восстановление отменено.");
        }

        if (Directory.Exists(target) && !overwrite)
        {
            throw new LauncherException(
                $"Мир «{name}» уже существует. Добавьте --overwrite для замены.");
        }

        var stagingRoot = Path.Combine(savesRoot, ".samara-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var temporary = new InstanceProfile
            {
                Id = instance.Id,
                Name = instance.Name,
                GameDir = stagingRoot
            };

            var extracted = await worlds.ImportAsync(zipPath, temporary, overwrite: true, ct);
            string? displaced = null;

            try
            {
                if (Directory.Exists(target))
                {
                    displaced = target + ".replaced-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, displaced);
                }

                Directory.Move(extracted, target);

                if (displaced is not null)
                {
                    TryDeleteDirectory(displaced);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (displaced is not null && !Directory.Exists(target))
                {
                    try
                    {
                        Directory.Move(displaced, target);
                    }
                    catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                    {
                        // Вернуть старую папку не удалось — сообщим об основной ошибке.
                    }
                }

                throw new LauncherException(
                    $"Не удалось переместить мир в {target}: {ex.Message}", ex);
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Backup,
                $"Мир восстановлен: {name}"));

            return target;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static string RequireWorldFolderName(string raw)
    {
        var name = raw.Trim().Trim('"');

        if (name.Length == 0 ||
            name is "." or ".." ||
            name.EndsWith('.') ||
            name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') ||
            name.Contains('\\') ||
            Path.IsPathRooted(name))
        {
            throw new LauncherException($"Недопустимое имя папки мира: {raw}");
        }

        return name;
    }

    private static void PrintWorldsHelp()
    {
        Console.WriteLine("""
            Использование:
              worlds list [--instance <id>] [--json]
              worlds backup <folder> [--to <папка>] [--instance <id>] [--json]
              worlds restore <zip> --world <folder> [--overwrite] [--instance <id>] [--json]
              worlds duplicate <folder> [--name <название>] [--instance <id>] [--json]
              worlds export <folder> <zip> [--instance <id>] [--json]
              worlds import <zip> [--name <название>] [--overwrite] [--instance <id>] [--json]
              worlds delete <folder> [--permanent] [--instance <id>] [--json]
              worlds folder [--instance <id>]

            folder      имя папки внутри saves.
            --permanent удалить мир безвозвратно (по умолчанию — перенос в removed-worlds).
            --overwrite заменить существующий мир при restore/import.
            """);
    }
}
