using SamaraLauncher.Core;
using SamaraLauncher.Core.Backups;
using SamaraLauncher.Core.Install;
using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Integrity;
using SamaraLauncher.Core.Logs;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Packwiz;
using SamaraLauncher.Core.Tools;
using JavaRuntimeLocator = SamaraLauncher.Core.Java.JavaLocator;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> InstallAsync(InstanceStore store, LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        using var cancellation = new CliCancellation();
        var progress = new ConsoleProgress();

        Console.WriteLine($"Установка инстанса \"{instance.Id}\" ({instance.Name})");
        Console.WriteLine($"Папка игры: {instance.GameDir}");
        Console.WriteLine();

        var javaPath = await new InstanceInstaller().EnsureInstalledAsync(
            instance,
            progress,
            cancellation.Token);

        // Installer обновляет VersionId/MinecraftVersion/ForgeVersion — фиксируем в конфиге.
        store.Save(config);

        Console.WriteLine();
        Console.WriteLine($"Java: {javaPath}");
        Console.WriteLine($"Версия: {instance.VersionId}");

        if (!string.IsNullOrWhiteSpace(instance.PackUrl))
        {
            Console.WriteLine();
            Console.WriteLine("Установка/обновление сборки через packwiz...");

            var result = await CreatePackwizService(config).UpdateAsync(
                instance,
                progress,
                cancellation.Token);

            PrintPackUpdate(result);

            if (!result.Success)
            {
                return 1;
            }
        }
        else
        {
            Console.WriteLine("PackUrl не задан — установка сборки packwiz пропущена.");
        }

        Console.WriteLine();
        Console.WriteLine("Установка завершена.");
        return 0;
    }

    private static async Task<int> UpdateAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        using var cancellation = new CliCancellation();
        var service = CreatePackwizService(config);

        if (string.IsNullOrWhiteSpace(instance.PackUrl))
        {
            throw new LauncherException(
                "У инстанса не указан PackUrl (URL или путь к pack.toml).");
        }

        if (args.Has("check"))
        {
            Console.WriteLine($"Проверка обновления для \"{instance.Id}\"...");
            var available = await service.IsUpdateAvailableAsync(instance, cancellation.Token);

            Console.WriteLine(available
                ? "Обновление доступно."
                : "Обновлений нет.");

            return 0;
        }

        Console.WriteLine($"Обновление сборки \"{instance.Id}\"...");

        var result = await service.UpdateAsync(
            instance,
            new ConsoleProgress(),
            cancellation.Token,
            force: true);

        PrintPackUpdate(result);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> VerifyAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        using var cancellation = new CliCancellation();
        var progress = new ConsoleProgress();
        var integrity = new IntegrityService();

        if (args.Has("restore-protected"))
        {
            var backups = new BackupService();
            var backup = backups.ListBackups(instance.Id).FirstOrDefault();

            if (backup is null)
            {
                throw new LauncherException(
                    $"Для инстанса \"{instance.Id}\" нет бэкапов. Создайте: backup --instance {instance.Id}");
            }

            Console.WriteLine($"Восстановление защищённых файлов из {Path.GetFileName(backup)}...");

            var restored = await integrity.RestoreProtectedAsync(
                instance.GameDir,
                backup,
                progress,
                cancellation.Token);

            if (restored.Count == 0)
            {
                Console.WriteLine("Защищённые файлы не требуют восстановления.");
            }
            else
            {
                foreach (var file in restored)
                {
                    Console.WriteLine($"  восстановлен: {file}");
                }

                Console.WriteLine($"Восстановлено файлов: {restored.Count}.");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"Проверка целостности \"{instance.Id}\"...");

        var report = await integrity.ScanAsync(instance, progress, cancellation.Token);
        PrintIntegrityReport(report);

        return report.IsHealthy ? 0 : 1;
    }

    private static async Task<int> BackupAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var backups = new BackupService();

        if (args.Has("list"))
        {
            var list = backups.ListBackups(instance.Id);

            Console.WriteLine($"Папка бэкапов: {backups.BackupsRoot}");

            if (list.Count == 0)
            {
                Console.WriteLine("Бэкапов нет.");
                return 0;
            }

            Console.WriteLine($"Бэкапы \"{instance.Id}\" (новые первыми):");

            foreach (var path in list)
            {
                var info = new FileInfo(path);
                Console.WriteLine(
                    $"  {Path.GetFileName(path),-44} {CliText.FormatSize(info.Length),10}  " +
                    info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            return 0;
        }

        var restorePath = args.GetLast("restore");
        if (!string.IsNullOrWhiteSpace(restorePath))
        {
            var backupFile = ResolveBackupFile(backups, instance, restorePath);

            using var restoreCancellation = new CliCancellation();
            Console.WriteLine($"Восстановление {Path.GetFileName(backupFile)} в {instance.GameDir}...");

            await backups.RestoreBackupAsync(
                backupFile,
                instance.GameDir,
                restoreCancellation.Token);

            Console.WriteLine("Бэкап восстановлен. Файлы mods/config/defaultconfigs перезаписаны.");
            return 0;
        }

        using var cancellation = new CliCancellation();
        var created = await backups.CreateBackupAsync(
            instance.GameDir,
            instance.Id,
            config.BackupRetention,
            new ConsoleProgress(),
            cancellation.Token);

        if (created is null)
        {
            Console.Error.WriteLine(
                "Нечего бэкапить: папки mods, config и defaultconfigs не найдены в папке игры.");
            return 1;
        }

        Console.WriteLine($"Бэкап создан: {created}");
        return 0;
    }

    private static int Crash(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        var crashDirectory = Path.Combine(instance.GameDir, "crash-reports");

        if (args.Has("open"))
        {
            new CrashHelper().OpenCrashFolder(instance);
            Console.WriteLine($"Открыта папка: {crashDirectory}");
            return 0;
        }

        var summary = new CrashReportParser().TryParseLatest(instance.GameDir);

        if (summary is null)
        {
            Console.Error.WriteLine($"crash-report не найден: {crashDirectory}");
            return 1;
        }

        Console.WriteLine($"Файл:              {summary.CrashFile}");

        if (!string.IsNullOrWhiteSpace(summary.Time))
        {
            Console.WriteLine($"Время:             {summary.Time}");
        }

        if (!string.IsNullOrWhiteSpace(summary.Description))
        {
            Console.WriteLine($"Описание:          {summary.Description}");
        }

        if (!string.IsNullOrWhiteSpace(summary.SuspectedMod))
        {
            Console.WriteLine($"Подозрительный мод: {summary.SuspectedMod}");
        }

        if (!string.IsNullOrWhiteSpace(summary.Cause))
        {
            Console.WriteLine($"Причина:           {summary.Cause}");
        }

        if (!string.IsNullOrWhiteSpace(summary.Recommendation))
        {
            Console.WriteLine();
            Console.WriteLine(summary.Recommendation);
        }

        Console.WriteLine();
        Console.WriteLine("--- Фрагмент отчёта ---");
        Console.WriteLine(summary.RawExcerpt);
        Console.WriteLine("--- Конец фрагмента ---");

        return 0;
    }

    private static async Task<int> LogsAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var maxLines = LogService.DefaultTailLines;
        var linesValue = args.GetLast("lines");

        if (!string.IsNullOrWhiteSpace(linesValue)
            && (!int.TryParse(linesValue, out maxLines) || maxLines <= 0))
        {
            Console.Error.WriteLine("--lines должен быть положительным целым числом.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        var logPath = Path.Combine(instance.GameDir, "logs", "latest.log");
        var follow = args.Has("follow");

        using var service = new LogService();
        var tail = service.TailLatestLog(instance.GameDir, fromBeginning: false, maxLines: maxLines);

        if (tail.Count == 0 && !follow)
        {
            Console.Error.WriteLine($"latest.log не найден или пуст: {logPath}");
            return 1;
        }

        foreach (var line in tail)
        {
            Console.WriteLine(line.Text);
        }

        if (!follow)
        {
            return 0;
        }

        if (tail.Count == 0)
        {
            Console.WriteLine($"latest.log пока нет — жду появления: {logPath}");
        }

        Console.WriteLine($"Слежение за latest.log (Ctrl+C — выход): {logPath}");

        using var cancellation = new CliCancellation(announce: false);

        void OnLine(LogLine line) => Console.WriteLine(line.Text);

        service.LineAppended += OnLine;

        try
        {
            service.StartTail(instance.GameDir, fromBeginning: false);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Слежение остановлено.");
            }
        }
        finally
        {
            service.LineAppended -= OnLine;
        }

        return 0;
    }

    private static PackwizService CreatePackwizService(LauncherConfig config) =>
        new(
            new JavaRuntimeLocator(),
            new ToolLocator(),
            new BackupService(),
            new IntegrityService(),
            config.BackupRetention);

    private static InstanceProfile? ResolveInstanceOption(
        LauncherConfig config,
        ParsedArgs args,
        out int exitCode)
    {
        exitCode = 0;
        var id = args.GetLast("instance");

        if (string.IsNullOrWhiteSpace(id))
        {
            if (config.Instances.Count == 1)
            {
                return config.Instances[0];
            }

            Console.Error.WriteLine(config.Instances.Count == 0
                ? "Инстансов нет. Сначала добавьте: add samara --game-dir <путь>"
                : "Укажите инстанс: --instance <id>");

            exitCode = config.Instances.Count == 0 ? 1 : 2;
            return null;
        }

        var instance = config.Find(id);

        if (instance is null)
        {
            Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
            exitCode = 1;
        }

        return instance;
    }

    private static string ResolveBackupFile(
        BackupService backups,
        InstanceProfile instance,
        string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));

        try
        {
            if (File.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (!Path.IsPathRooted(expanded))
            {
                var local = Path.GetFullPath(expanded);
                if (File.Exists(local))
                {
                    return local;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь к бэкапу: {rawPath}", ex);
        }

        var fileName = Path.GetFileName(expanded);
        var match = backups
            .ListBackups(instance.Id)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                fileName,
                StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        throw new LauncherException(
            $"Файл бэкапа не найден: {rawPath}. Посмотрите список: backup --list");
    }

    private static void PrintPackUpdate(PackUpdateResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"Обновление: {(result.Success ? "успешно" : "ошибка")}");
        Console.WriteLine($"Изменено: {(result.Changed ? "да" : "нет")} ({result.ChangedFiles.Count} файлов)");
        Console.WriteLine($"Бэкап: {result.BackupPath ?? "не создан"}");
        Console.WriteLine($"Предупреждения: {result.Warnings.Count}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  [!] {warning}");
        }

        Console.WriteLine($"Восстановленные защищённые файлы: {result.RestoredProtectedFiles.Count}");

        foreach (var file in result.RestoredProtectedFiles)
        {
            Console.WriteLine($"  - {file}");
        }

        if (result.ChangedFiles.Count > 0)
        {
            Console.WriteLine("Изменённые файлы:");
            foreach (var file in result.ChangedFiles.Take(100))
            {
                Console.WriteLine($"  - {file}");
            }

            if (result.ChangedFiles.Count > 100)
            {
                Console.WriteLine($"  ... ещё {result.ChangedFiles.Count - 100}");
            }
        }
    }

    private static void PrintIntegrityReport(IntegrityReport report)
    {
        var brokenProtected = report.Protected.Count(file => !file.HashMatches);

        Console.WriteLine();
        Console.WriteLine($"Проверено файлов: {report.CheckedFiles}");
        Console.WriteLine($"Отсутствует:      {report.Missing.Count}");
        Console.WriteLine($"Изменено:         {report.Modified.Count}");
        Console.WriteLine($"Лишних:           {report.Extra.Count}");
        Console.WriteLine($"Защищённых:       {report.Protected.Count} (повреждено: {brokenProtected})");

        PrintFileStates("Отсутствующие файлы:", report.Missing);
        PrintFileStates("Изменённые файлы:", report.Modified);
        PrintFileStates("Лишние файлы:", report.Extra);
        PrintFileStates(
            "Повреждённые защищённые файлы:",
            report.Protected.Where(file => !file.HashMatches));

        Console.WriteLine();
        Console.WriteLine(report.IsHealthy
            ? "Целостность сборки в порядке."
            : "Обнаружены расхождения. Для восстановления используйте update или backup --restore.");
    }

    private static void PrintFileStates(string title, IEnumerable<FileState> files)
    {
        const int limit = 100;

        var list = files.ToList();
        if (list.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(title);

        foreach (var file in list.Take(limit))
        {
            var note = file.Kind switch
            {
                FileStateKind.Missing => " — файл отсутствует",
                FileStateKind.Modified => " — хеш не совпадает",
                FileStateKind.Extra => " — нет в packwiz",
                FileStateKind.Protected => string.IsNullOrWhiteSpace(file.Reason)
                    ? " — локальный патч изменён"
                    : $" — локальный патч изменён ({file.Reason})",
                _ => ""
            };

            Console.WriteLine($"  - {file.Path}{note}");
        }

        if (list.Count > limit)
        {
            Console.WriteLine($"  ... ещё {list.Count - limit}");
        }
    }
}
