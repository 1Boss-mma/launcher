using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> ModsAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var subcommand = GetSubcommand(args);
        if (string.IsNullOrWhiteSpace(subcommand))
        {
            PrintModsHelp();
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var mods = new ModManager();

        switch (subcommand)
        {
            case "list":
            {
                var list = mods.List(instance);

                if (WantsJson(args))
                {
                    PrintJson(list.Select(mod => new
                    {
                        mod.FileName,
                        mod.FullPath,
                        mod.Size,
                        mod.Enabled,
                        mod.ModifiedAt
                    }).ToList());
                    return 0;
                }

                if (list.Count == 0)
                {
                    Console.WriteLine($"Модов нет: {Path.Combine(instance.GameDir, "mods")}");
                    return 0;
                }

                Console.WriteLine($"Моды ({list.Count}):");

                foreach (var mod in list)
                {
                    Console.WriteLine(
                        $"  [{(mod.Enabled ? "вкл" : "выкл")}] {mod.FileName}  {CliText.FormatSize(mod.Size)}");
                }

                Console.WriteLine(
                    $"Включено: {list.Count(mod => mod.Enabled)}, " +
                    $"отключено: {list.Count(mod => !mod.Enabled)}.");
                return 0;
            }

            case "enable":
            case "disable":
            {
                var fileName = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    Console.Error.WriteLine($"Укажите файл мода: mods {subcommand} <файл>");
                    return 2;
                }

                var mod = FindMod(mods.Mods, instance, fileName);
                var enable = subcommand == "enable";
                var changed = enable ? mods.Enable(mod) : mods.Disable(mod);

                if (WantsJson(args))
                {
                    PrintJson(new { fileName = mod.FileName, enabled = enable, changed });
                    return 0;
                }

                if (changed)
                {
                    Console.WriteLine($"{(enable ? "Включён" : "Отключён")}: {mod.FileName}");
                }
                else
                {
                    Console.WriteLine($"Мод уже {(enable ? "включён" : "отключён")}: {mod.FileName}");
                }

                return 0;
            }

            case "import":
            {
                if (rest.Count == 0)
                {
                    Console.Error.WriteLine("Укажите jar-файлы: mods import <jar...>");
                    return 2;
                }

                var imported = mods.Import(instance, rest.Select(ExpandPath));

                if (WantsJson(args))
                {
                    PrintJson(imported.Select(mod => new
                    {
                        mod.FileName,
                        mod.Size,
                        mod.Enabled
                    }).ToList());
                    return 0;
                }

                foreach (var mod in imported)
                {
                    Console.WriteLine($"Импортирован: {mod.FileName} ({CliText.FormatSize(mod.Size)})");
                }

                Console.WriteLine($"Импортировано модов: {imported.Count}.");
                return 0;
            }

            case "folder":
            {
                mods.OpenModsFolder(instance);
                Console.WriteLine($"Открыта папка модов: {Path.Combine(instance.GameDir, "mods")}");
                return 0;
            }

            case "analyze":
            {
                var report = mods.Analyze(instance);

                if (WantsJson(args))
                {
                    PrintJson(report);
                    return report.IsHealthy ? 0 : 1;
                }

                var enabled = report.Mods.Count(mod => mod.Enabled);
                Console.WriteLine($"Модов: {report.Mods.Count} (включено: {enabled}, отключено: {report.Mods.Count - enabled}).");

                if (report.DuplicateModIds.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Дубликаты modId:");

                    foreach (var duplicate in report.DuplicateModIds)
                    {
                        Console.WriteLine($"  - {duplicate.ModId}: {string.Join(", ", duplicate.FileNames)}");
                    }
                }

                if (report.DuplicateFileNames.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Дубликаты имён файлов:");

                    foreach (var duplicate in report.DuplicateFileNames)
                    {
                        Console.WriteLine($"  - {duplicate}");
                    }
                }

                if (report.MissingDependencies.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Отсутствующие зависимости:");

                    foreach (var issue in report.MissingDependencies)
                    {
                        var version = string.IsNullOrWhiteSpace(issue.VersionRange)
                            ? ""
                            : " " + issue.VersionRange;
                        var found = string.IsNullOrWhiteSpace(issue.FoundVersion)
                            ? ""
                            : $" (найдено: {issue.FoundVersion})";

                        Console.WriteLine(
                            $"  - {issue.ModId}{version} требуется для {issue.RequiredByFileName}{found}");
                    }
                }

                if (report.DisabledDependencies.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Отключённые зависимости:");

                    foreach (var issue in report.DisabledDependencies)
                    {
                        Console.WriteLine(
                            $"  - {issue.ModId} требуется для {issue.RequiredByFileName} " +
                            $"({issue.FoundFileName ?? "файл отключён"})");
                    }
                }

                if (report.Warnings.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Замечания ({report.Warnings.Count}):");

                    foreach (var warning in report.Warnings)
                    {
                        Console.WriteLine($"  - {warning}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine(report.IsHealthy
                    ? "Проблем с модами не найдено."
                    : "Обнаружены проблемы с модами.");
                return report.IsHealthy ? 0 : 1;
            }

            case "updates":
            {
                var json = WantsJson(args);

                if (!json)
                {
                    Console.WriteLine($"Проверка обновлений модов для \"{instance.Id}\" через Modrinth...");
                }

                using var cancellation = new CliCancellation();

                IReadOnlyList<ModUpdateInfo> updates;
                try
                {
                    updates = await mods.CheckUpdatesAsync(instance, cancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not LauncherException)
                {
                    Console.Error.WriteLine($"Не удалось проверить обновления: {ex.Message}");
                    return 1;
                }

                if (json)
                {
                    PrintJson(updates.Select(update => new
                    {
                        update.FileName,
                        update.ModId,
                        update.CurrentVersion,
                        update.LatestVersion,
                        update.ProjectId,
                        update.PublishedAt,
                        update.DownloadUrl
                    }).ToList());
                    return 0;
                }

                if (updates.Count == 0)
                {
                    Console.WriteLine("Обновлений не найдено.");
                    return 0;
                }

                Console.WriteLine($"Доступно обновлений: {updates.Count}");

                foreach (var update in updates)
                {
                    Console.WriteLine(
                        $"  - {update.FileName}: {update.CurrentVersion} -> {update.LatestVersion}");
                }

                return 0;
            }

            case "update-all":
            {
                var json = WantsJson(args);

                if (!json)
                {
                    Console.WriteLine($"Проверка обновлений модов для \"{instance.Id}\"...");
                }

                using var cancellation = new CliCancellation();
                var updates = await mods.CheckUpdatesAsync(instance, cancellation.Token);

                if (updates.Count == 0)
                {
                    if (json)
                    {
                        PrintJson(Array.Empty<object>());
                    }
                    else
                    {
                        Console.WriteLine("Обновлений не найдено.");
                    }

                    return 0;
                }

                if (!json)
                {
                    Console.WriteLine($"Доступно обновлений: {updates.Count}");

                    foreach (var update in updates)
                    {
                        Console.WriteLine(
                            $"  - {update.FileName}: {update.CurrentVersion} -> {update.LatestVersion}");
                    }
                }

                if (!args.Has("yes") && !Confirm($"Обновить моды ({updates.Count})?"))
                {
                    if (!json)
                    {
                        Console.WriteLine("Обновление отменено.");
                    }

                    return 0;
                }

                var modsDirectory = Path.Combine(instance.GameDir, "mods");
                var results = new List<object>();
                var failed = 0;

                foreach (var update in updates)
                {
                    try
                    {
                        var path = await mods.DownloadUpdateAsync(
                            update,
                            modsDirectory,
                            cancellation.Token);

                        results.Add(new
                        {
                            fileName = update.FileName,
                            currentVersion = update.CurrentVersion,
                            latestVersion = update.LatestVersion,
                            success = true,
                            path,
                            error = (string?)null
                        });

                        if (!json)
                        {
                            Console.WriteLine($"  обновлён: {update.FileName} -> {update.LatestVersion}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (LauncherException ex)
                    {
                        failed++;
                        results.Add(new
                        {
                            fileName = update.FileName,
                            currentVersion = update.CurrentVersion,
                            latestVersion = update.LatestVersion,
                            success = false,
                            path = (string?)null,
                            error = ex.Message
                        });

                        if (!json)
                        {
                            Console.Error.WriteLine($"  ошибка {update.FileName}: {ex.Message}");
                        }
                    }
                }

                if (json)
                {
                    PrintJson(results);
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"Обновлено: {updates.Count - failed}, ошибок: {failed}.");
                }

                return failed == 0 ? 0 : 1;
            }

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда mods: {subcommand}");
                PrintModsHelp();
                return 2;
        }
    }

    private static ModEntry FindMod(ModService mods, InstanceProfile instance, string rawName)
    {
        var name = Path.GetFileName(rawName.Trim().Trim('"'));
        var list = mods.List(instance);

        var exact = list.FirstOrDefault(mod =>
            string.Equals(mod.FileName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripDisabledSuffix(mod.FileName), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mod.FileName, name + ".disabled", StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var matches = list
            .Where(mod => mod.FileName.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new LauncherException(
                $"Под \"{name}\" подходит несколько модов: " +
                string.Join(", ", matches.Select(mod => mod.FileName)) +
                ". Укажите точное имя файла.");
        }

        throw new LauncherException($"Мод не найден: {name}. Список: mods list");
    }

    private static string StripDisabledSuffix(string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    private static void PrintModsHelp()
    {
        Console.WriteLine("""
            Использование:
              mods list [--instance <id>] [--json]
              mods enable <файл> [--instance <id>] [--json]
              mods disable <файл> [--instance <id>] [--json]
              mods import <jar...> [--instance <id>] [--json]
              mods folder [--instance <id>]
              mods analyze [--instance <id>] [--json]
              mods updates [--check] [--instance <id>] [--json]
              mods update-all [--yes] [--instance <id>] [--json]

            Имя файла ищется без учёта регистра; можно указать часть имени.
            analyze        дубликаты modId и проблемы зависимостей.
            updates        проверка обновлений через Modrinth (--check — то же самое).
            update-all     скачать доступные обновления; --yes — без подтверждения.
            """);
    }
}
