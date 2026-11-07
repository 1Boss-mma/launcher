using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Models;

/// <summary>
/// Описание одного инстанса (профиля) сборки.
/// </summary>
public sealed class InstanceProfile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Папка игры (внутри неё лежат mods, config, versions, assets).</summary>
    public string GameDir { get; set; } = "";

    /// <summary>"auto" — искать java в runtime инстанса, JAVA_HOME и PATH.</summary>
    public string JavaPath { get; set; } = "auto";

    public int MemoryMb { get; set; } = 6144;

    public int MinMemoryMb { get; set; } = 2048;

    public List<string> JvmArgs { get; set; } = new() { "-XX:+UseG1GC" };

    /// <summary>Id версии, например "1.20.1-forge-47.4.28". Пусто — определить автоматически.</summary>
    public string VersionId { get; set; } = "";

    /// <summary>Версия Minecraft, например "1.20.1". Пусто — взять из VersionId.</summary>
    public string MinecraftVersion { get; set; } = "1.20.1";

    /// <summary>Версия Forge, например "47.4.28".</summary>
    public string ForgeVersion { get; set; } = "47.4.28";

    /// <summary>
    /// Загрузчик версии: "vanilla", "forge", "fabric" или "optifine".
    /// Пусто — старый профиль (совместимость с прежним поведением Forge).
    /// </summary>
    public string Loader { get; set; } = "";

    /// <summary>Версия Fabric loader, например "0.16.10".</summary>
    public string FabricVersion { get; set; } = "";

    /// <summary>Имя файла OptiFine, например "OptiFine_1.20.1_HD_U_I6.jar".</summary>
    public string OptiFineVersion { get; set; } = "";

    /// <summary>
    /// Fabric-профиль: Loader равен fabric или VersionId начинается с fabric-loader-.
    /// </summary>
    [JsonIgnore]
    public bool IsFabric =>
        string.Equals(MinecraftLoader.Normalize(Loader), MinecraftLoader.Fabric, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(VersionId) &&
         VersionId.Trim().StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// OptiFine-профиль: Loader равен optifine, задан OptiFineVersion или
    /// VersionId содержит optifine.
    /// </summary>
    [JsonIgnore]
    public bool IsOptiFine =>
        string.Equals(MinecraftLoader.Normalize(Loader), MinecraftLoader.OptiFine, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(OptiFineVersion) ||
        (!string.IsNullOrWhiteSpace(VersionId) &&
         VersionId.Contains("optifine", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Forge-профиль (OptiFine запускается поверх Forge): задан ForgeVersion,
    /// VersionId содержит -forge- или Loader равен forge/optifine.
    /// Fabric никогда не считается Forge. Пустой ForgeVersion — ванильный инстанс.
    /// </summary>
    [JsonIgnore]
    public bool IsForge =>
        !IsFabric &&
        (IsOptiFine ||
         !string.IsNullOrWhiteSpace(ForgeVersion) ||
         (!string.IsNullOrWhiteSpace(VersionId) &&
          VersionId.Contains("-forge-", StringComparison.OrdinalIgnoreCase)) ||
         string.Equals(MinecraftLoader.Normalize(Loader), MinecraftLoader.Forge, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public bool IsVanilla => !IsFabric && !IsForge && !IsOptiFine;

    /// <summary>Ключ загрузчика для UI/установщика.</summary>
    [JsonIgnore]
    public string LoaderKey =>
        IsFabric ? MinecraftLoader.Fabric :
        IsOptiFine ? MinecraftLoader.OptiFine :
        IsForge ? MinecraftLoader.Forge :
        MinecraftLoader.Vanilla;

    /// <summary>
    /// Базовая версия Minecraft. Если MinecraftVersion задана — она; иначе
    /// выводится из VersionId (Forge/OptiFine и Fabric-префиксы).
    /// </summary>
    [JsonIgnore]
    public string BaseMinecraftVersion
    {
        get
        {
            var minecraftVersion = MinecraftVersion?.Trim() ?? "";
            if (minecraftVersion.Length > 0)
            {
                return minecraftVersion;
            }

            return MinecraftLoader.GuessBaseVersion(VersionId);
        }
    }

    public string Username { get; set; } = "Player";

    /// <summary>UUID без дефисов. Пусто — вычислить оффлайн-UUID из ника.</summary>
    public string? Uuid { get; set; }

    /// <summary>URL или путь к pack.toml для установки/обновления сборки.</summary>
    public string PackUrl { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Цвет иконки инстанса в формате #RRGGBB.</summary>
    public string IconColor { get; set; } = "#5FBE4A";

    /// <summary>Порядок в списке инстансов: меньше — выше.</summary>
    public int SortOrder { get; set; }

    public bool IsFavorite { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Последняя запускавшаяся версия (например, после смены сборки).</summary>
    public string? LastVersionId { get; set; }

    [JsonIgnore]
    public string EffectiveUuid =>
        string.IsNullOrWhiteSpace(Uuid) ? OfflineUuid.FromUsername(Username) : OfflineUuid.Normalize(Uuid);

    [JsonIgnore]
    public bool IsInstalled =>
        !string.IsNullOrWhiteSpace(GameDir) &&
        Directory.Exists(Path.Combine(GameDir, "versions")) &&
        Directory.Exists(Path.Combine(GameDir, "libraries"));
}
