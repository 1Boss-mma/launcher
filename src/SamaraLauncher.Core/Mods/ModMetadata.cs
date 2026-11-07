namespace SamaraLauncher.Core.Mods;

/// <summary>Загрузчик, под который собран мод.</summary>
public enum ModLoader
{
    Unknown = 0,
    Forge,
    Fabric,
    NeoForge
}

/// <summary>Метаданные одного мода, прочитанные из jar.</summary>
public sealed class ModMetadata
{
    public string ModId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    public string Description { get; set; } = "";

    public List<string> Authors { get; set; } = new();

    public ModLoader Loader { get; set; } = ModLoader.Unknown;

    public string FileName { get; set; } = "";

    public string FullPath { get; set; } = "";

    public bool Enabled { get; set; }

    public List<ModDependency> Dependencies { get; set; } = new();

    /// <summary>Моды, вложенные в этот jar через JarJar (Forge/NeoForge) или META-INF/jars (Fabric).</summary>
    public List<ModMetadata> NestedMods { get; set; } = new();

    public bool IsClientSideOnly { get; set; }

    public bool IsServerSideOnly { get; set; }
}

/// <summary>Зависимость мода из mods.toml или fabric.mod.json.</summary>
public sealed class ModDependency
{
    public string ModId { get; set; } = "";

    public string VersionRange { get; set; } = "";

    public bool Mandatory { get; set; }

    /// <summary>"client", "server" или "both".</summary>
    public string Side { get; set; } = "both";

    /// <summary>"BEFORE", "AFTER" или "NONE".</summary>
    public string Ordering { get; set; } = "NONE";
}
