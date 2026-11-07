using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SamaraLauncher.Core.Resources;

/// <summary>
/// Чтение иконки и базовых метаданных пака. Для zip-архивов действует
/// защита от zip-bomb: ограничено число записей и суммарный распакованный размер.
/// </summary>
public static class PackIconReader
{
    /// <summary>Максимальный размер pack.png — 4 МБ.</summary>
    public const int MaxIconBytes = 4 * 1024 * 1024;

    /// <summary>Максимальное число записей в zip-архиве пака.</summary>
    public const int MaxArchiveEntries = 100_000;

    /// <summary>Максимальный суммарный распакованный размер zip-архива пака.</summary>
    public const long MaxArchiveUncompressedBytes = 8L * 1024 * 1024 * 1024;

    internal const int MaxMetadataBytes = 1024 * 1024;

    /// <summary>
    /// Возвращает содержимое pack.png (до <see cref="MaxIconBytes"/>) или null,
    /// если пака, иконки или корректного zip нет.
    /// </summary>
    public static byte[]? ReadIcon(string packPath, PackKind kind) =>
        Inspect(packPath, kind).IconPng;

    internal static PackInspection Inspect(
        string packPath,
        PackKind kind,
        bool includeIcon = true)
    {
        if (string.IsNullOrWhiteSpace(packPath))
        {
            return PackInspection.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(packPath.Trim().Trim('"'));

            if (Directory.Exists(fullPath))
            {
                return InspectFolder(fullPath, kind, includeIcon);
            }

            if (File.Exists(fullPath))
            {
                return InspectZip(fullPath, kind, includeIcon);
            }
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return PackInspection.Empty;
        }

        return PackInspection.Empty;
    }

    private static PackInspection InspectFolder(
        string directory,
        PackKind kind,
        bool includeIcon)
    {
        var packMetaPath = Path.Combine(directory, "pack.mcmeta");
        var shaderPropertiesPath = Path.Combine(directory, "shaders", "shaders.properties");
        var shadersDirectory = Path.Combine(directory, "shaders");

        var hasPackMcmeta = File.Exists(packMetaPath);
        var hasShaderProperties = File.Exists(shaderPropertiesPath);
        var hasShadersDirectory = Directory.Exists(shadersDirectory);
        var hasShaders = hasShaderProperties || hasShadersDirectory;

        var description = hasPackMcmeta
            ? ReadPackDescription(ReadTextFileBounded(packMetaPath, MaxMetadataBytes))
            : "";

        byte[]? icon = null;
        if (includeIcon)
        {
            icon = ReadFileBounded(Path.Combine(directory, "pack.png"), MaxIconBytes);

            if (icon is null && kind == PackKind.ShaderPack)
            {
                icon = ReadFileBounded(Path.Combine(shadersDirectory, "pack.png"), MaxIconBytes);
            }

            if (icon is not null && icon.Length == 0)
            {
                icon = null;
            }
        }

        var isValid = kind == PackKind.ResourcePack
            ? hasPackMcmeta
            : hasShaders || hasPackMcmeta;

        return new PackInspection(icon, description, hasPackMcmeta, hasShaders, isValid);
    }

    private static PackInspection InspectZip(
        string zipPath,
        PackKind kind,
        bool includeIcon)
    {
        try
        {
            using var stream = new FileStream(
                zipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entries = archive.Entries;

            if (entries.Count > MaxArchiveEntries)
            {
                return PackInspection.Empty;
            }

            long totalLength = 0;
            foreach (var entry in entries)
            {
                var length = entry.Length;
                if (length < 0 || length > MaxArchiveUncompressedBytes - totalLength)
                {
                    return PackInspection.Empty;
                }

                totalLength += length;
            }

            ZipArchiveEntry? packMetaEntry = null;
            ZipArchiveEntry? shaderPropertiesEntry = null;
            ZipArchiveEntry? packIconEntry = null;
            ZipArchiveEntry? shaderIconEntry = null;
            var hasShaders = false;

            foreach (var entry in entries)
            {
                var name = NormalizeEntryName(entry.FullName);
                var isDirectory = IsDirectoryEntry(entry);

                if (name.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
                    || (isDirectory && string.Equals(name, "shaders", StringComparison.OrdinalIgnoreCase)))
                {
                    hasShaders = true;
                }

                if (isDirectory)
                {
                    continue;
                }

                if (string.Equals(name, "pack.mcmeta", StringComparison.OrdinalIgnoreCase))
                {
                    packMetaEntry ??= entry;
                }
                else if (string.Equals(name, "shaders/shaders.properties", StringComparison.OrdinalIgnoreCase))
                {
                    shaderPropertiesEntry ??= entry;
                }
                else if (string.Equals(name, "pack.png", StringComparison.OrdinalIgnoreCase))
                {
                    packIconEntry ??= entry;
                }
                else if (string.Equals(name, "shaders/pack.png", StringComparison.OrdinalIgnoreCase))
                {
                    shaderIconEntry ??= entry;
                }
            }

            hasShaders |= shaderPropertiesEntry is not null;

            var description = packMetaEntry is null
                ? ""
                : ReadPackDescription(ReadEntryText(packMetaEntry));

            byte[]? icon = null;
            if (includeIcon)
            {
                icon = packIconEntry is null
                    ? null
                    : ReadEntryBounded(packIconEntry, MaxIconBytes);

                if ((icon is null || icon.Length == 0) && kind == PackKind.ShaderPack)
                {
                    icon = shaderIconEntry is null
                        ? null
                        : ReadEntryBounded(shaderIconEntry, MaxIconBytes);
                }

                if (icon is not null && icon.Length == 0)
                {
                    icon = null;
                }
            }

            var isValid = kind == PackKind.ResourcePack
                ? packMetaEntry is not null
                : hasShaders || packMetaEntry is not null;

            return new PackInspection(
                icon,
                description,
                packMetaEntry is not null,
                hasShaders,
                isValid);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return PackInspection.Empty;
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        var bytes = ReadEntryBounded(entry, MaxMetadataBytes);
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }

        var offset = HasUtf8Bom(bytes) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static byte[]? ReadEntryBounded(ZipArchiveEntry entry, int maxBytes)
    {
        try
        {
            if (entry.Length > maxBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            return ReadBounded(stream, maxBytes);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return null;
        }
    }

    private static byte[]? ReadFileBounded(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes)
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            return ReadBounded(stream, maxBytes);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return null;
        }
    }

    private static string ReadTextFileBounded(string path, int maxBytes)
    {
        var bytes = ReadFileBounded(path, maxBytes);
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }

        var offset = HasUtf8Bom(bytes) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static byte[]? ReadBounded(Stream stream, int maxBytes)
    {
        var buffer = new byte[81920];
        using var result = new MemoryStream();

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + read > maxBytes)
            {
                return null;
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    private static string ReadPackDescription(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "pack", out var pack)
                || pack.ValueKind != JsonValueKind.Object
                || !TryGetProperty(pack, "description", out var description))
            {
                return "";
            }

            return FlattenText(description).Trim();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string FlattenText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";

            case JsonValueKind.Number:
                return element.GetRawText();

            case JsonValueKind.True:
                return "true";

            case JsonValueKind.False:
                return "false";

            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    var text = FlattenText(item);
                    if (text.Length > 0)
                    {
                        parts.Add(text);
                    }
                }

                return string.Concat(parts);
            }

            case JsonValueKind.Object:
            {
                var parts = new List<string>();

                if (TryGetProperty(element, "text", out var textElement))
                {
                    var text = FlattenText(textElement);
                    if (text.Length > 0)
                    {
                        parts.Add(text);
                    }
                }
                else if (TryGetProperty(element, "translate", out var translateElement)
                         && translateElement.ValueKind == JsonValueKind.String)
                {
                    var translate = translateElement.GetString();
                    if (!string.IsNullOrWhiteSpace(translate))
                    {
                        parts.Add(translate);
                    }
                }

                if (TryGetProperty(element, "extra", out var extraElement)
                    && extraElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in extraElement.EnumerateArray())
                    {
                        var text = FlattenText(item);
                        if (text.Length > 0)
                        {
                            parts.Add(text);
                        }
                    }
                }

                return string.Concat(parts);
            }

            default:
                return "";
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeEntryName(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool IsExpectedFileError(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException
            or System.Security.SecurityException;
}

/// <summary>Результат разбора zip/папки пака для сервисов.</summary>
internal sealed class PackInspection
{
    public static PackInspection Empty { get; } = new(null, "", false, false, false);

    public PackInspection(
        byte[]? iconPng,
        string description,
        bool hasPackMcmeta,
        bool hasShaders,
        bool isValid)
    {
        IconPng = iconPng;
        Description = description;
        HasPackMcmeta = hasPackMcmeta;
        HasShaders = hasShaders;
        IsValid = isValid;
    }

    public byte[]? IconPng { get; }

    public string Description { get; }

    public bool HasPackMcmeta { get; }

    public bool HasShaders { get; }

    public bool IsValid { get; }
}
