using System.ComponentModel;
using System.Globalization;
using SamaraLauncher.Core.Backups;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Управление модами в папке mods: включение, отключение, импорт и удаление в бэкап.
/// </summary>
public sealed class ModService
{
    private readonly BackupService _backups;

    public ModService(BackupService? backups = null)
    {
        _backups = backups ?? new BackupService();
    }

    public IReadOnlyList<ModEntry> List(InstanceProfile instance)
    {
        var modsDir = GetModsDirectory(instance, create: false);
        if (!Directory.Exists(modsDir))
        {
            return Array.Empty<ModEntry>();
        }

        var result = new List<ModEntry>();

        foreach (var path in Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (!IsModFile(fileName))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                continue;
            }

            result.Add(new ModEntry
            {
                FileName = fileName,
                FullPath = path,
                Size = info.Length,
                Enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
            });
        }

        return result
            .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Переименовывает .jar.disabled в .jar. Возвращает true, если файл был изменён.</summary>
    public bool Enable(ModEntry mod)
    {
        var entry = ValidateMod(mod);

        if (!entry.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = entry.FullPath[..^".disabled".Length];
        if (File.Exists(target))
        {
            throw new LauncherException(
                $"Нельзя включить {entry.FileName}: файл уже существует: {target}");
        }

        MoveFile(entry.FullPath, target);

        entry.FullPath = target;
        entry.FileName = Path.GetFileName(target);
        entry.Enabled = true;
        entry.ModifiedAt = DateTimeOffset.UtcNow;

        return true;
    }

    /// <summary>Переименовывает .jar в .jar.disabled. Возвращает true, если файл был изменён.</summary>
    public bool Disable(ModEntry mod)
    {
        var entry = ValidateMod(mod);

        if (entry.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = entry.FullPath + ".disabled";
        if (File.Exists(target))
        {
            throw new LauncherException(
                $"Нельзя отключить {entry.FileName}: файл уже существует: {target}");
        }

        MoveFile(entry.FullPath, target);

        entry.FullPath = target;
        entry.FileName = Path.GetFileName(target);
        entry.Enabled = false;
        entry.ModifiedAt = DateTimeOffset.UtcNow;

        return true;
    }

    public bool SetEnabled(ModEntry mod, bool enabled) =>
        enabled ? Enable(mod) : Disable(mod);

    /// <summary>Переносит мод в backups\removed\&lt;timestamp&gt;\, не удаляя безвозвратно.</summary>
    public string Delete(ModEntry mod)
    {
        var entry = ValidateMod(mod);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(_backups.RemovedModsRoot, stamp);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, entry.FileName);

        for (var index = 2; File.Exists(target) && index < 1000; index++)
        {
            var name = Path.GetFileNameWithoutExtension(entry.FileName);
            var extension = Path.GetExtension(entry.FileName);
            target = Path.Combine(directory, $"{name}-{index}{extension}");
        }

        MoveFile(entry.FullPath, target);
        entry.FullPath = target;
        entry.ModifiedAt = DateTimeOffset.UtcNow;

        return target;
    }

    /// <summary>Копирует jar'ы в mods. Существующие файлы не перезаписываются.</summary>
    public IReadOnlyList<ModEntry> Import(InstanceProfile instance, IEnumerable<string> jarPaths)
    {
        ArgumentNullException.ThrowIfNull(jarPaths);

        var modsDir = GetModsDirectory(instance, create: true);
        var sources = new List<string>();

        foreach (var rawPath in jarPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var source = Path.GetFullPath(rawPath.Trim().Trim('"'));
            if (!File.Exists(source))
            {
                throw new LauncherException($"Файл мода не найден: {source}");
            }

            if (!source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(
                    $"Можно импортировать только .jar (не .jar.disabled): {source}");
            }

            sources.Add(source);
        }

        var targets = new List<(string Source, string Target)>();
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var target = Path.Combine(modsDir, Path.GetFileName(source));

            if (!targetNames.Add(Path.GetFileName(target)))
            {
                throw new LauncherException(
                    $"В списке импорта несколько файлов с именем {Path.GetFileName(target)}.");
            }

            if (File.Exists(target))
            {
                throw new LauncherException($"Мод уже существует в папке mods: {Path.GetFileName(target)}");
            }

            targets.Add((source, target));
        }

        var imported = new List<ModEntry>();

        foreach (var (source, target) in targets)
        {
            try
            {
                File.Copy(source, target, overwrite: false);
            }
            catch (IOException ex)
            {
                throw new LauncherException(
                    $"Не удалось скопировать мод {Path.GetFileName(source)}: {ex.Message}",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new LauncherException(
                    $"Нет доступа для копирования мода {Path.GetFileName(source)}: {ex.Message}",
                    ex);
            }

            var info = new FileInfo(target);
            imported.Add(new ModEntry
            {
                FileName = Path.GetFileName(target),
                FullPath = target,
                Size = info.Length,
                Enabled = true,
                ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
            });
        }

        return imported;
    }

    public void OpenModsFolder(InstanceProfile instance)
    {
        var modsDir = GetModsDirectory(instance, create: true);

        try
        {
            ProcessHost.ShellOpen(modsDir);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            throw new LauncherException($"Не удалось открыть папку модов: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new LauncherException($"Не удалось открыть папку модов: {ex.Message}", ex);
        }
    }

    /// <summary>Ищет мод по имени файла (в том числе по пути из crash-report).</summary>
    public ModEntry? FindByFileName(InstanceProfile instance, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileName(fileName.Trim().Trim('"'));
        var baseName = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;

        return List(instance).FirstOrDefault(entry =>
            string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                entry.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? entry.FileName[..^".disabled".Length]
                    : entry.FileName,
                baseName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void MoveFile(string source, string target)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Не удалось переместить {Path.GetFileName(source)}: {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException(
                $"Нет доступа для перемещения {Path.GetFileName(source)}: {ex.Message}",
                ex);
        }
    }

    private static ModEntry ValidateMod(ModEntry? mod)
    {
        if (mod is null)
        {
            throw new LauncherException("Не передан мод.");
        }

        if (string.IsNullOrWhiteSpace(mod.FullPath) || !File.Exists(mod.FullPath))
        {
            throw new LauncherException($"Файл мода не найден: {mod.FullPath}");
        }

        return mod;
    }

    private static string GetModsDirectory(InstanceProfile instance, bool create)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан инстанс.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        var modsDir = Path.Combine(Path.GetFullPath(instance.GameDir), "mods");

        if (create)
        {
            Directory.CreateDirectory(modsDir);
        }

        return modsDir;
    }

    private static bool IsModFile(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
}
