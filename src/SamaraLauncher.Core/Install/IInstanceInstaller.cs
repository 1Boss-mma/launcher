using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Install;

public interface IInstanceInstaller
{
    /// <summary>
    /// Доустанавливает всё, чего не хватает: Java, vanilla 1.20.1, Forge 47.4.28, natives.
    /// Возвращает путь к java.
    /// </summary>
    Task<string> EnsureInstalledAsync(
        InstanceProfile instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Устанавливает произвольную ванильную версию в инстанс и возвращает путь к java.
    /// Forge-поля профиля очищаются, существующие данные игры не удаляются.
    /// </summary>
    Task<string> EnsureVanillaInstalledAsync(
        InstanceProfile instance,
        string versionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Устанавливает Forge для указанного релиза (или последнюю версию, если
    /// <paramref name="forgeVersion"/> пуст) и возвращает путь к java.
    /// </summary>
    Task<string> EnsureForgeInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        string? forgeVersion = null,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Устанавливает Fabric для указанного релиза и возвращает путь к java.
    /// </summary>
    Task<string> EnsureFabricInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Устанавливает OptiFine поверх Forge и возвращает путь к java.
    /// </summary>
    Task<string> EnsureOptiFineInstalledAsync(
        InstanceProfile instance,
        string minecraftVersion,
        string optifineJarPath,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
