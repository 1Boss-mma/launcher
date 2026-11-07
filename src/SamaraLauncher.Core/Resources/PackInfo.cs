namespace SamaraLauncher.Core.Resources;

/// <summary>Тип пака в папке ресурсов инстанса.</summary>
public enum PackKind
{
    /// <summary>Ресурспак из папки <c>resourcepacks</c>.</summary>
    ResourcePack = 0,

    /// <summary>Шейдерпак из папки <c>shaderpacks</c>.</summary>
    ShaderPack = 1
}

/// <summary>
/// Элемент списка ресурспаков или шейдерпаков инстанса.
/// </summary>
public sealed class PackInfo
{
    /// <summary>Имя файла (zip) или папки пака.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Полный путь к zip-файлу или папке пака.</summary>
    public string FullPath { get; set; } = "";

    /// <summary>Размер zip-файла или суммарный размер файлов в папке пака.</summary>
    public long Size { get; set; }

    /// <summary>Включён ли пак в options.txt / optionsshaders.txt инстанса.</summary>
    public bool Enabled { get; set; }

    /// <summary>Отображаемое имя: имя файла без .zip или имя папки.</summary>
    public string Title { get; set; } = "";

    /// <summary>Описание из pack.mcmeta (если удалось прочитать).</summary>
    public string Description { get; set; } = "";

    /// <summary>Содержимое pack.png (не более 4 МБ) или null.</summary>
    public byte[]? IconPng { get; set; }

    /// <summary>Формат пака: <c>zip</c> или <c>folder</c>.</summary>
    public string Format { get; set; } = "zip";

    /// <summary>Время последнего изменения файла или папки (UTC).</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
