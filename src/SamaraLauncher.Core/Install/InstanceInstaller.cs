using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Install;

/// <summary>
/// Полная установка инстанса сборки: Java, ванильный Minecraft, выбранный
/// загрузчик (Forge/Fabric/OptiFine) и natives.
/// </summary>
public sealed class InstanceInstaller : IInstanceInstaller
{
    private readonly IJavaLocator _javaLocator;
    private readonly JavaRuntimeInstaller _javaRuntimeInstaller;
    private readonly MinecraftInstaller _minecraftInstaller;
    private readonly ForgeInstaller _forgeInstaller;
    private readonly NativesExtractor _nativesExtractor;
    private readonly InstanceStore? _instanceStore;
    private readonly LauncherConfig? _config;
    private readonly FabricInstaller _fabricInstaller;
    private readonly ForgeVersionCatalog _forgeVersionCatalog;

    public InstanceInstaller(
        IJavaLocator? javaLocator = null,
        JavaRuntimeInstaller? javaRuntimeInstaller = null,
        MinecraftInstaller? minecraftInstaller = null,
        ForgeInstaller? forgeInstaller = null,
        NativesExtractor? nativesExtractor = null,
        InstanceStore? instanceStore = null,
        LauncherConfig? config = null,
        FabricInstaller? fabricInstaller = null,
        ForgeVersionCatalog? forgeVersionCatalog = null)
    {
        _javaLocator = javaLocator ?? new JavaLocator();
        _javaRuntimeInstaller = javaRuntimeInstaller ?? new JavaRuntimeInstaller();
        _minecraftInstaller = minecraftInstaller ?? new MinecraftInstaller();
        _forgeInstaller = forgeInstaller ?? new ForgeInstaller();
        _nativesExtractor = nativesExtractor ?? new NativesExtractor();
        _instanceStore = instanceStore;
        _config = config;
        _fabricInstaller = fabricInstaller ?? new FabricInstaller();
        _forgeVersionCatalog = forgeVersionCatalog ?? new ForgeVersionCatalog();
    }

    /// <inheritdoc />
    public async Task<string> EnsureInstalledAsync(
        InstanceProfile instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан профиль инстанса.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры (GameDir).");
        }

        var gameDir = Path.GetFullPath(instance.GameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка инстанса {instance.Name}",
            0,
            0));

        var java = await EnsureJavaAsync(instance, gameDir, progress, cancellationToken);

        await _minecraftInstaller.EnsureVanillaAsync(gameDir, progress, cancellationToken);
        await _forgeInstaller.EnsureForgeAsync(gameDir, java.Path, progress, cancellationToken);
        await _nativesExtractor.ExtractAsync(
            gameDir,
            ForgeInstaller.VersionId,
            progress,
            cancellationToken);

        instance.Loader = MinecraftLoader.Forge;
        instance.VersionId = ForgeInstaller.VersionId;
        instance.MinecraftVersion = ForgeInstaller.MinecraftVersion;
        instance.ForgeVersion = ForgeInstaller.ForgeVersion;
        instance.FabricVersion = "";
        instance.OptiFineVersion = "";

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Инстанс {instance.Name} готов к запуску",
            0,
            0));

        return java.Path;
    }

    /// <summary>
    /// Устанавливает произвольную ванильную версию в существующий (обычно новый)
    /// инстанс: version json, client, библиотеки, ассеты, подходящую Java и natives.
    /// Поля загрузчиков профиля очищаются. Возвращает путь к java.
    /// </summary>
    public async Task<string> EnsureVanillaInstalledAsync(
        InstanceProfile instance,
        string versionId,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан профиль инстанса.");
        }

        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new LauncherException("Не указана версия Minecraft для установки.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры (GameDir).");
        }

        versionId = versionId.Trim();
        var gameDir = Path.GetFullPath(instance.GameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка ванильного инстанса {instance.Name}",
            0,
            0));

        await _minecraftInstaller.EnsureVanillaAsync(
            gameDir,
            versionId,
            progress,
            cancellationToken);

        var paths = new VersionPaths(gameDir);
        var metadata = VersionMetadata.Load(paths.VersionJson(versionId));
        var requiredMajor = MinecraftJavaVersion.GetRequiredMajor(
            versionId,
            metadata.JavaMajorVersion);

        var java = await EnsureJavaForMajorAsync(
            instance,
            gameDir,
            requiredMajor,
            progress,
            cancellationToken);

        await _nativesExtractor.ExtractAsync(
            gameDir,
            versionId,
            progress,
            cancellationToken);

        instance.Loader = MinecraftLoader.Vanilla;
        instance.VersionId = versionId;
        instance.MinecraftVersion = versionId;
        instance.ForgeVersion = "";
        instance.FabricVersion = "";
        instance.OptiFineVersion = "";
        instance.LastVersionId = versionId;

        SaveProfileIfConfigured(instance);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Инстанс {instance.Name} готов к запуску (Minecraft {versionId})",
            0,
            0));

        return java.Path;
    }

    /// <summary>
    /// Устанавливает Forge для указанного релиза Minecraft. Если версия Forge не
    /// задана, выбирается последняя из каталога Forge. Возвращает путь к java.
    /// </summary>
    public async Task<string> EnsureForgeInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        string? forgeVersion = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан профиль инстанса.");
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new LauncherException("Не указана версия Minecraft для установки Forge.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры (GameDir).");
        }

        var mcVersion = minecraftVersion.Trim();
        var gameDir = Path.GetFullPath(instance.GameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка Forge для Minecraft {mcVersion}",
            0,
            0));

        await _minecraftInstaller.EnsureVanillaAsync(
            gameDir,
            mcVersion,
            progress,
            cancellationToken);

        var paths = new VersionPaths(gameDir);
        var metadata = VersionMetadata.Load(paths.VersionJson(mcVersion));
        var requiredMajor = MinecraftJavaVersion.GetRequiredMajor(
            mcVersion,
            metadata.JavaMajorVersion);

        var java = await EnsureJavaForMajorAsync(
            instance,
            gameDir,
            requiredMajor,
            progress,
            cancellationToken);

        var forge = string.IsNullOrWhiteSpace(forgeVersion)
            ? await _forgeVersionCatalog.GetLatestAsync(mcVersion, cancellationToken)
            : forgeVersion.Trim();

        var versionId = await _forgeInstaller.EnsureForgeAsync(
            gameDir,
            java.Path,
            mcVersion,
            forge,
            progress,
            cancellationToken);

        await _nativesExtractor.ExtractAsync(
            gameDir,
            versionId,
            progress,
            cancellationToken);

        instance.Loader = MinecraftLoader.Forge;
        instance.VersionId = versionId;
        instance.MinecraftVersion = mcVersion;
        instance.ForgeVersion = forge;
        instance.FabricVersion = "";
        instance.OptiFineVersion = "";
        instance.LastVersionId = versionId;

        SaveProfileIfConfigured(instance);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Инстанс {instance.Name} готов к запуску (Forge {forge})",
            0,
            0));

        return java.Path;
    }

    /// <summary>
    /// Устанавливает Fabric для указанного релиза Minecraft. Возвращает путь к java.
    /// </summary>
    public async Task<string> EnsureFabricInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан профиль инстанса.");
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new LauncherException("Не указана версия Minecraft для установки Fabric.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры (GameDir).");
        }

        var mcVersion = minecraftVersion.Trim();
        var gameDir = Path.GetFullPath(instance.GameDir);
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку игры {gameDir}: {ex.Message}", ex);
        }

        progress?.Report(new LauncherProgress(
            ProgressStage.Preparing,
            $"Подготовка Fabric для Minecraft {mcVersion}",
            0,
            0));

        await _minecraftInstaller.EnsureVanillaAsync(
            gameDir,
            mcVersion,
            progress,
            cancellationToken);

        var paths = new VersionPaths(gameDir);
        var metadata = VersionMetadata.Load(paths.VersionJson(mcVersion));
        var requiredMajor = MinecraftJavaVersion.GetRequiredMajor(
            mcVersion,
            metadata.JavaMajorVersion);

        var java = await EnsureJavaForMajorAsync(
            instance,
            gameDir,
            requiredMajor,
            progress,
            cancellationToken);

        var result = await _fabricInstaller.EnsureFabricAsync(
            gameDir,
            java.Path,
            mcVersion,
            progress,
            cancellationToken);

        await _nativesExtractor.ExtractAsync(
            gameDir,
            result.VersionId,
            progress,
            cancellationToken);

        instance.Loader = MinecraftLoader.Fabric;
        instance.VersionId = result.VersionId;
        instance.MinecraftVersion = mcVersion;
        instance.ForgeVersion = "";
        instance.FabricVersion = result.LoaderVersion;
        instance.OptiFineVersion = "";
        instance.LastVersionId = result.VersionId;

        SaveProfileIfConfigured(instance);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Инстанс {instance.Name} готов к запуску (Fabric {result.LoaderVersion})",
            0,
            0));

        return java.Path;
    }

    /// <summary>
    /// Устанавливает OptiFine поверх последнего Forge для указанного релиза:
    /// копирует jar в mods, не перезаписывая существующий файл. Возвращает путь к java.
    /// </summary>
    public async Task<string> EnsureOptiFineInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        string optifineJarPath,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан профиль инстанса.");
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new LauncherException("Не указана версия Minecraft для установки OptiFine.");
        }

        if (string.IsNullOrWhiteSpace(optifineJarPath))
        {
            throw new LauncherException("Не указан файл OptiFine для установки.");
        }

        var mcVersion = minecraftVersion.Trim();
        var jarPath = Path.GetFullPath(optifineJarPath.Trim().Trim('"'));

        if (!File.Exists(jarPath))
        {
            throw new LauncherException($"Файл OptiFine не найден: {jarPath}");
        }

        var fileName = Path.GetFileName(jarPath);
        if (!fileName.Contains(mcVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Файл OptiFine \"{fileName}\" не подходит для Minecraft {mcVersion}: " +
                "в имени файла должна быть версия Minecraft.");
        }

        var javaPath = await EnsureForgeInstalledAsync(
            instance,
            mcVersion,
            forgeVersion: null,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var gameDir = Path.GetFullPath(instance.GameDir);
        var modsDir = Path.Combine(gameDir, "mods");

        try
        {
            Directory.CreateDirectory(modsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось создать папку mods {modsDir}: {ex.Message}", ex);
        }

        var destination = Path.Combine(modsDir, fileName);

        if (File.Exists(destination))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Preparing,
                $"OptiFine уже лежит в mods: {fileName}",
                0,
                0));
        }
        else
        {
            try
            {
                File.Copy(jarPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Файл успел появиться — сохраняем пользовательский вариант.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось скопировать OptiFine в {modsDir}: {ex.Message}", ex);
            }

            progress?.Report(new LauncherProgress(
                ProgressStage.Preparing,
                $"OptiFine скопирован в mods: {fileName}",
                0,
                0));
        }

        instance.Loader = MinecraftLoader.OptiFine;
        instance.OptiFineVersion = fileName;
        instance.LastVersionId = instance.VersionId;

        SaveProfileIfConfigured(instance);

        progress?.Report(new LauncherProgress(
            ProgressStage.Done,
            $"Инстанс {instance.Name} готов к запуску (OptiFine {fileName})",
            0,
            0));

        return javaPath;
    }

    /// <summary>
    /// Вариант для UI/CLI: после установки сохраняет переданный config.json.
    /// Если инстанса ещё нет в конфиге, он добавляется.
    /// </summary>
    public async Task<string> EnsureVanillaInstalledAsync(
        LauncherConfig config,
        InstanceProfile instance,
        string versionId,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var javaPath = await EnsureVanillaInstalledAsync(
            instance,
            versionId,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (ReferenceEquals(_config, config))
        {
            return javaPath;
        }

        var stored = config.Find(instance.Id);
        if (stored is null)
        {
            config.Instances.Add(instance);
        }
        else if (!ReferenceEquals(stored, instance))
        {
            CopyLoaderFields(stored, instance);
        }

        (_instanceStore ?? new InstanceStore()).Save(config);
        return javaPath;
    }

    private async Task<JavaInfo> EnsureJavaAsync(
        InstanceProfile instance,
        string gameDir,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var found = _javaLocator.Find(instance);
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Найдена Java {found.Version}",
                0,
                0));

            return found;
        }
        catch (LauncherException) when (
            string.IsNullOrWhiteSpace(instance.JavaPath) ||
            string.Equals(instance.JavaPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                "Java 17+ не найдена — скачиваю Eclipse Temurin 17",
                0,
                0));

            return await _javaRuntimeInstaller.EnsureInstalledAsync(
                gameDir,
                progress,
                cancellationToken);
        }
    }

    private async Task<JavaInfo> EnsureJavaForMajorAsync(
        InstanceProfile instance,
        string gameDir,
        int requiredMajor,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var found = _javaLocator.Find(instance, requiredMajor, allowNewer: false);
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Найдена Java {found.Version}",
                0,
                0));

            return found;
        }
        catch (LauncherException) when (
            string.IsNullOrWhiteSpace(instance.JavaPath) ||
            string.Equals(instance.JavaPath.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Java,
                $"Java {requiredMajor} не найдена — скачиваю Eclipse Temurin {requiredMajor}",
                0,
                0));

            return await _javaRuntimeInstaller.EnsureInstalledAsync(
                gameDir,
                requiredMajor,
                progress,
                cancellationToken);
        }
    }

    private void SaveProfileIfConfigured(InstanceProfile instance)
    {
        if (_config is null)
        {
            return;
        }

        var stored = _config.Find(instance.Id);
        if (stored is null)
        {
            return;
        }

        if (!ReferenceEquals(stored, instance))
        {
            CopyLoaderFields(stored, instance);
        }

        (_instanceStore ?? new InstanceStore()).Save(_config);
    }

    private static void CopyLoaderFields(InstanceProfile target, InstanceProfile source)
    {
        target.Loader = source.Loader;
        target.VersionId = source.VersionId;
        target.MinecraftVersion = source.MinecraftVersion;
        target.ForgeVersion = source.ForgeVersion;
        target.FabricVersion = source.FabricVersion;
        target.OptiFineVersion = source.OptiFineVersion;
        target.LastVersionId = source.LastVersionId;
    }
}
