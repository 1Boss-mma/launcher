using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;
using JavaLocatorImpl = SamaraLauncher.Core.Java.JavaLocator;
using LaunchPlanModel = SamaraLauncher.Core.Models.LaunchPlan;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Готовит план запуска и запускает игру отдельным процессом.
/// </summary>
public sealed class GameLauncher
{
    private readonly IJavaLocator _javaLocator;
    private readonly LaunchCommandBuilder _commandBuilder;
    private readonly VanillaLaunchCommandBuilder _vanillaCommandBuilder;

    public GameLauncher(IJavaLocator? javaLocator = null)
    {
        _javaLocator = javaLocator ?? new JavaLocatorImpl();
        _commandBuilder = new LaunchCommandBuilder();
        _vanillaCommandBuilder = new VanillaLaunchCommandBuilder();
    }

    /// <summary>Находит Java и собирает команду запуска (Forge или ванильной версии).</summary>
    public LaunchPlanModel CreatePlan(InstanceProfile instance) => CreatePlan(instance, serverAddress: null);

    /// <summary>
    /// Находит Java и собирает команду запуска Forge или ванильной версии. Если задан
    /// <paramref name="serverAddress"/> (host, host:port или [ipv6]:port), в план
    /// добавляется быстрое подключение к серверу.
    /// </summary>
    public LaunchPlanModel CreatePlan(InstanceProfile instance, string? serverAddress)
    {
        ValidateInstance(instance);

        var effectiveInstance = instance;

        if (!IsLegacyForgeProfile(instance) &&
            !instance.IsFabric &&
            string.IsNullOrWhiteSpace(instance.VersionId))
        {
            // Старые профили могли не хранить VersionId — смотрим versions.
            var detected = VersionPaths.DetectForgeVersionId(
                Path.GetFullPath(instance.GameDir));

            if (!string.IsNullOrWhiteSpace(detected))
            {
                effectiveInstance = CloneWithVersionId(instance, detected);
            }
        }

        var isLegacyForge = IsLegacyForgeProfile(effectiveInstance);

        JavaInfo java;
        try
        {
            if (isLegacyForge)
            {
                java = _javaLocator.Find(effectiveInstance);
            }
            else
            {
                var requiredMajor = VanillaLaunchCommandBuilder.GetRequiredJavaMajor(effectiveInstance);
                java = _javaLocator.Find(
                    effectiveInstance,
                    requiredMajor,
                    allowNewer: false);
            }
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new LauncherException($"Не удалось найти Java: {ex.Message}", ex);
        }

        if (java is null || string.IsNullOrWhiteSpace(java.Path))
        {
            throw new LauncherException("Java не найдена: локатор вернул пустой путь.");
        }

        if (!File.Exists(java.Path))
        {
            throw new LauncherException($"Java не найдена: {java.Path}");
        }

        var plan = isLegacyForge
            ? _commandBuilder.Build(effectiveInstance, java.Path, serverAddress)
            : _vanillaCommandBuilder.Build(effectiveInstance, java.Path, serverAddress);

        if (string.IsNullOrWhiteSpace(plan.MainClass))
        {
            throw new LauncherException(
                "В плане запуска нет main class — проверьте version json версии.");
        }

        return plan;
    }

    /// <summary>
    /// Старый проверенный Forge 1.20.1 47.x (в том числе OptiFine поверх него),
    /// который запускается отдельным Forge-сборщиком. Для остальных версий
    /// используется общий сборщик по version json.
    /// </summary>
    public static bool IsLegacyForgeProfile(InstanceProfile instance)
    {
        if (instance is null || instance.IsFabric || !instance.IsForge)
        {
            return false;
        }

        var baseVersion = instance.BaseMinecraftVersion;
        var forgeVersion = instance.ForgeVersion?.Trim() ?? "";
        var versionId = instance.VersionId?.Trim() ?? "";

        var isBase1201 = string.Equals(baseVersion, "1.20.1", StringComparison.OrdinalIgnoreCase);
        var isForge47 =
            forgeVersion.StartsWith("47.", StringComparison.OrdinalIgnoreCase) ||
            versionId.Contains("1.20.1-forge-47.", StringComparison.OrdinalIgnoreCase);

        return isBase1201 && isForge47;
    }

    /// <summary>
    /// Запускает игру. Лог по умолчанию: &lt;gameDir&gt;\logs\launcher-&lt;timestamp&gt;.log.
    /// Если задан <paramref name="serverAddress"/>, игра сразу подключится к серверу.
    /// </summary>
    public GameProcess Start(InstanceProfile instance, string? logPath = null, string? serverAddress = null)
    {
        var plan = CreatePlan(instance, serverAddress);

        if (!Directory.Exists(plan.WorkingDirectory))
        {
            throw new LauncherException($"Папка игры не существует: {plan.WorkingDirectory}");
        }

        if (plan.Arguments.Count == 0)
        {
            throw new LauncherException("План запуска пуст — нечего передавать Java.");
        }

        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = Path.Combine(
                plan.WorkingDirectory,
                "logs",
                $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
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
                throw new LauncherException($"Не удалось создать папку логов: {logDirectory}", ex);
            }
        }

        try
        {
            var header = string.Join(Environment.NewLine, new[]
            {
                $"=== Лаунчер · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Инстанс: {instance.Id} ({instance.Name})",
                $"Java:     {plan.JavaPath}",
                $"Версия:   {instance.VersionId}",
                $"Память:   {instance.MinMemoryMb}–{instance.MemoryMb} МБ",
                $"Аргументов: {plan.Arguments.Count}",
                ""
            });

            using var stream = new FileStream(fullLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(header + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось записать лог запуска: {fullLogPath}", ex);
        }

        var startInfo = ProcessHost.CreateHidden(
            plan.JavaPath,
            plan.Arguments,
            plan.WorkingDirectory,
            redirectOutput: true,
            outputEncoding: new UTF8Encoding(false));

        foreach (var (key, value) in plan.Environment)
        {
            startInfo.Environment[key] = value;
        }

        GameProcess process;
        try
        {
            process = new GameProcess(startInfo, fullLogPath);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            throw new LauncherException($"Не удалось запустить игру: {ex.Message}", ex);
        }

        // Обновляем только в памяти: сохранение конфига — задача InstanceStore/UI.
        instance.LastPlayed = DateTimeOffset.UtcNow;

        return process;
    }

    private static InstanceProfile CloneWithVersionId(InstanceProfile source, string versionId) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            GameDir = source.GameDir,
            JavaPath = source.JavaPath,
            MemoryMb = source.MemoryMb,
            MinMemoryMb = source.MinMemoryMb,
            JvmArgs = source.JvmArgs is null
                ? new List<string>()
                : new List<string>(source.JvmArgs),
            VersionId = versionId,
            MinecraftVersion = source.MinecraftVersion,
            ForgeVersion = source.ForgeVersion,
            Loader = source.Loader,
            FabricVersion = source.FabricVersion,
            OptiFineVersion = source.OptiFineVersion,
            Username = source.Username,
            Uuid = source.Uuid,
            PackUrl = source.PackUrl,
            CreatedAt = source.CreatedAt,
            LastPlayed = source.LastPlayed,
            IconColor = source.IconColor,
            SortOrder = source.SortOrder,
            IsFavorite = source.IsFavorite,
            Notes = source.Notes,
            LastVersionId = source.LastVersionId
        };

    private static void ValidateInstance(InstanceProfile instance)
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
