using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Подмножество version json, нужное для запуска Forge.
/// </summary>
public sealed class VersionManifest
{
    public string? Id { get; set; }

    public string? MainClass { get; set; }

    public string? InheritsFrom { get; set; }

    public AssetIndexRef? AssetIndex { get; set; }

    public JavaVersionInfo? JavaVersion { get; set; }

    public List<LibraryEntry> Libraries { get; set; } = new();

    public ArgumentsSection? Arguments { get; set; }

    /// <summary>Старый формат (до 1.13): строка аргументов Minecraft.</summary>
    public string? MinecraftArguments { get; set; }
}

public sealed class AssetIndexRef
{
    public string? Id { get; set; }
}

public sealed class JavaVersionInfo
{
    /// <summary>В разных манифестах бывает и числом, и строкой — значение не используется.</summary>
    public JsonElement? MajorVersion { get; set; }
}

public sealed class ArgumentsSection
{
    public List<ArgumentEntry>? Jvm { get; set; }

    public List<ArgumentEntry>? Game { get; set; }
}

[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public List<string> Values { get; set; } = new();

    public List<Rule>? Rules { get; set; }
}

public sealed class Rule
{
    public string? Action { get; set; }

    public OsRule? Os { get; set; }

    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Arch { get; set; }
}

public sealed class LibraryEntry
{
    public string? Name { get; set; }

    public LibraryDownloads? Downloads { get; set; }

    public List<Rule>? Rules { get; set; }
}

public sealed class LibraryDownloads
{
    public LibraryArtifact? Artifact { get; set; }

    public Dictionary<string, LibraryArtifact>? Classifiers { get; set; }
}

public sealed class LibraryArtifact
{
    public string? Path { get; set; }

    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}

/// <summary>
/// В version json элементы arguments.jvm/game бывают и строками, и объектами.
/// </summary>
internal sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            return new ArgumentEntry { Values = new List<string> { root.GetString() ?? "" } };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ArgumentEntry();
        }

        var entry = new ArgumentEntry();

        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            entry.Values = values.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
                .ToList();
        }

        if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            entry.Rules = JsonSerializer.Deserialize<List<Rule>>(rules.GetRawText(), options);
        }

        return entry;
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
