using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Фасад Mod Manager 2.0: список модов, анализ, метаданные и обновления.
/// </summary>
public sealed class ModManager
{
    public ModManager(
        ModService? mods = null,
        ModMetadataReader? metadataReader = null,
        ModAnalyzer? analyzer = null,
        ModUpdateService? updates = null)
    {
        Mods = mods ?? new ModService();
        MetadataReader = metadataReader ?? new ModMetadataReader();
        Analyzer = analyzer ?? new ModAnalyzer(Mods, MetadataReader);
        Updates = updates ?? new ModUpdateService(metadataReader: MetadataReader, mods: Mods);
    }

    public ModService Mods { get; }

    public ModMetadataReader MetadataReader { get; }

    public ModAnalyzer Analyzer { get; }

    public ModUpdateService Updates { get; }

    public IReadOnlyList<ModEntry> List(InstanceProfile instance) => Mods.List(instance);

    public ModMetadata? ReadMetadata(ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return MetadataReader.Read(mod.FullPath);
    }

    public ModAnalysisReport Analyze(InstanceProfile instance) => Analyzer.Analyze(instance);

    public Task<IReadOnlyList<ModUpdateInfo>> CheckUpdatesAsync(
        InstanceProfile instance,
        CancellationToken cancellationToken = default) =>
        Updates.CheckUpdatesAsync(instance, cancellationToken);

    public Task<string> DownloadUpdateAsync(
        ModUpdateInfo update,
        string modsDirectory,
        CancellationToken cancellationToken = default) =>
        Updates.DownloadAsync(update, modsDirectory, cancellationToken);

    public bool SetEnabled(ModEntry mod, bool enabled) => Mods.SetEnabled(mod, enabled);

    public bool Enable(ModEntry mod) => Mods.Enable(mod);

    public bool Disable(ModEntry mod) => Mods.Disable(mod);

    public string Delete(ModEntry mod) => Mods.Delete(mod);

    public IReadOnlyList<ModEntry> Import(InstanceProfile instance, IEnumerable<string> jarPaths) =>
        Mods.Import(instance, jarPaths);

    public void OpenModsFolder(InstanceProfile instance) => Mods.OpenModsFolder(instance);

    public ModEntry? FindByFileName(InstanceProfile instance, string fileName) =>
        Mods.FindByFileName(instance, fileName);
}
