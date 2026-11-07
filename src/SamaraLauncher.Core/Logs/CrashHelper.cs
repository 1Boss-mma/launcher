using System.ComponentModel;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Logs;

/// <summary>
/// Действия краш-хелпера: разбор последнего краша и открытие папок/логов в проводнике.
/// </summary>
public sealed class CrashHelper
{
    private readonly CrashReportParser _parser;

    public CrashHelper(CrashReportParser? parser = null)
    {
        _parser = parser ?? new CrashReportParser();
    }

    /// <summary>Разбор самого свежего crash-report. null — если крашей нет.</summary>
    public CrashSummary? GetSummary(InstanceProfile instance)
    {
        Validate(instance);
        return _parser.TryParseLatest(instance.GameDir);
    }

    /// <summary>Открывает &lt;gameDir&gt;\crash-reports в проводнике.</summary>
    public void OpenCrashFolder(InstanceProfile instance)
    {
        Validate(instance);

        var directory = Path.Combine(instance.GameDir, "crash-reports");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось создать папку крашей: {directory}", ex);
        }

        OpenPath(directory);
    }

    /// <summary>Открывает &lt;gameDir&gt;\logs\latest.log.</summary>
    public void OpenLatestLog(InstanceProfile instance)
    {
        Validate(instance);

        var path = Path.Combine(instance.GameDir, "logs", "latest.log");
        if (!File.Exists(path))
        {
            throw new LauncherException($"Лог не найден: {path}. Игра ещё не запускалась?");
        }

        OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        try
        {
            ProcessHost.ShellOpen(path);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new LauncherException($"Не удалось открыть: {path}", ex);
        }
    }

    private static void Validate(InstanceProfile instance)
    {
        if (instance is null)
        {
            throw new LauncherException("Профиль не задан.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("В профиле не указана папка игры.");
        }

        if (!Directory.Exists(instance.GameDir))
        {
            throw new LauncherException($"Папка игры не существует: {instance.GameDir}");
        }
    }
}
