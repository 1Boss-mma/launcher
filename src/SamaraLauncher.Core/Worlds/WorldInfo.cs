namespace SamaraLauncher.Core.Worlds;

/// <summary>
/// Описание мира из папки saves. UI сам форматирует размер и дату.
/// </summary>
public sealed class WorldInfo
{
    /// <summary>Имя папки мира (уникальное внутри saves).</summary>
    public string Name { get; init; } = "";

    /// <summary>Название из level.dat (LevelName); для битого мира совпадает с Name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Полный путь к папке мира.</summary>
    public string Path { get; init; } = "";

    public long SizeBytes { get; init; }

    public DateTimeOffset LastPlayed { get; init; }

    public long Seed { get; init; }

    /// <summary>0 — survival, 1 — creative, 2 — adventure, 3 — spectator.</summary>
    public int GameType { get; init; }

    /// <summary>0 — peaceful, 1 — easy, 2 — normal, 3 — hard.</summary>
    public int Difficulty { get; init; }

    public bool Hardcore { get; init; }

    public string VersionName { get; init; } = "";

    /// <summary>false — level.dat отсутствует, повреждён или не читается.</summary>
    public bool IsValid { get; init; }

    /// <summary>Цвет иконки мира в формате #RRGGBB.</summary>
    public string IconColor { get; init; } = "#5FBE4A";
}

/// <summary>Архив бэкапа мира из папки бэкапов.</summary>
public sealed class WorldBackupArchiveInfo
{
    public string Path { get; init; } = "";

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Имя папки мира из манифеста; пусто у старых/чужих архивов.</summary>
    public string WorldName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public DateTimeOffset CreatedUtc { get; init; }

    public long SizeBytes { get; init; }

    public string? ServerId { get; init; }
}
