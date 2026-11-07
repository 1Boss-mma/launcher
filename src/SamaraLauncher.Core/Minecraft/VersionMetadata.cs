using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaraLauncher.Core.Minecraft;

/// <summary>Разобранный version json (ванильный или Forge).</summary>
public sealed class VersionMetadata
{
    public string Id { get; set; } = "";

    public string MainClass { get; set; } = "";

    public string? InheritsFrom { get; set; }

    public string? Assets { get; set; }

    public string? AssetIndexId { get; set; }

    public string? Type { get; set; }

    /// <summary>Относительный путь client jar из downloads.client.path, если он указан.</summary>
    public string? ClientJarPath { get; set; }

    /// <summary>javaVersion.majorVersion из version json (8, 17, 21...), если он есть.</summary>
    public int? JavaMajorVersion { get; set; }

    public List<VersionLibrary> Libraries { get; set; } = new();

    public List<VersionArgument> JvmArguments { get; set; } = new();

    public List<VersionArgument> GameArguments { get; set; } = new();

    public static VersionMetadata Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new LauncherException($"Не найден version json: {path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new LauncherException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }
    }

    public static VersionMetadata Parse(JsonElement root)
    {
        var result = new VersionMetadata
        {
            Id = GetString(root, "id") ?? "",
            MainClass = GetString(root, "mainClass") ?? "",
            InheritsFrom = GetString(root, "inheritsFrom"),
            Assets = GetString(root, "assets"),
            Type = GetString(root, "type"),
            JavaMajorVersion = ParseJavaMajor(root)
        };

        if (root.TryGetProperty("downloads", out var downloads) &&
            downloads.ValueKind == JsonValueKind.Object &&
            downloads.TryGetProperty("client", out var client) &&
            client.ValueKind == JsonValueKind.Object)
        {
            result.ClientJarPath = GetString(client, "path");
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex) &&
            assetIndex.ValueKind == JsonValueKind.Object)
        {
            result.AssetIndexId = GetString(assetIndex, "id") ?? result.Assets;
        }

        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                result.Libraries.Add(ParseLibrary(lib));
            }
        }

        if (root.TryGetProperty("arguments", out var args) &&
            args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("jvm", out var jvm))
            {
                result.JvmArguments.AddRange(ParseArguments(jvm));
            }

            if (args.TryGetProperty("game", out var game))
            {
                result.GameArguments.AddRange(ParseArguments(game));
            }
        }

        // Старый формат (1.20.1 vanilla) — одна строка.
        var legacy = GetString(root, "minecraftArguments");
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            var parts = legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result.GameArguments.AddRange(parts.Select(p => new VersionArgument { Values = new List<string> { p } }));
        }

        return result;
    }

    private static VersionLibrary ParseLibrary(JsonElement element)
    {
        var lib = new VersionLibrary
        {
            Name = GetString(element, "name") ?? "",
            Url = GetString(element, "url"),
            Natives = GetString(element, "natives")
        };

        lib.Rules.AddRange(ParseRules(element));

        if (element.TryGetProperty("downloads", out var downloads) &&
            downloads.ValueKind == JsonValueKind.Object)
        {
            if (downloads.TryGetProperty("artifact", out var artifact))
            {
                lib.Artifact = ParseArtifact(artifact);
            }

            if (downloads.TryGetProperty("classifiers", out var classifiers) &&
                classifiers.ValueKind == JsonValueKind.Object)
            {
                foreach (var classifier in classifiers.EnumerateObject())
                {
                    lib.Classifiers[classifier.Name] = ParseArtifact(classifier.Value);
                }
            }
        }

        if (lib.Artifact is null &&
            element.TryGetProperty("artifact", out var topLevelArtifact) &&
            topLevelArtifact.ValueKind == JsonValueKind.Object)
        {
            lib.Artifact = ParseArtifact(topLevelArtifact);
        }

        return lib;
    }

    private static DownloadArtifact ParseArtifact(JsonElement element) => new()
    {
        Path = GetString(element, "path") ?? "",
        Sha1 = GetString(element, "sha1") ?? "",
        Url = GetString(element, "url") ?? "",
        Size = element.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0
    };

    private static IEnumerable<VersionArgument> ParseArguments(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                yield return new VersionArgument { Values = new List<string> { item.GetString() ?? "" } };
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var arg = new VersionArgument();
            arg.Rules.AddRange(ParseRules(item));

            if (item.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    arg.Values.Add(value.GetString() ?? "");
                }
            }
            else if (item.TryGetProperty("value", out var single))
            {
                if (single.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in single.EnumerateArray())
                    {
                        arg.Values.Add(value.GetString() ?? "");
                    }
                }
                else if (single.ValueKind == JsonValueKind.String)
                {
                    arg.Values.Add(single.GetString() ?? "");
                }
            }

            yield return arg;
        }
    }

    private static IEnumerable<Rule> ParseRules(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in rules.EnumerateArray())
        {
            var rule = new Rule { Action = GetString(item, "action") ?? "" };

            if (item.TryGetProperty("os", out var os))
            {
                rule.Os = new RuleOs
                {
                    Name = GetString(os, "name"),
                    Arch = GetString(os, "arch"),
                    Version = GetString(os, "version")
                };
            }

            if (item.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
            {
                rule.Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var feature in features.EnumerateObject())
                {
                    rule.Features[feature.Name] = feature.Value.ValueKind == JsonValueKind.True;
                }
            }

            yield return rule;
        }
    }

    private static int? ParseJavaMajor(JsonElement root)
    {
        if (!root.TryGetProperty("javaVersion", out var javaVersion) ||
            javaVersion.ValueKind != JsonValueKind.Object ||
            !javaVersion.TryGetProperty("majorVersion", out var majorVersion))
        {
            return null;
        }

        if (majorVersion.ValueKind == JsonValueKind.Number &&
            majorVersion.TryGetInt32(out var number))
        {
            return number > 0 ? number : null;
        }

        if (majorVersion.ValueKind == JsonValueKind.String &&
            int.TryParse(
                majorVersion.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed > 0 ? parsed : null;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class VersionLibrary
{
    public string Name { get; set; } = "";

    public string? Url { get; set; }

    public string? Natives { get; set; }

    public DownloadArtifact? Artifact { get; set; }

    public Dictionary<string, DownloadArtifact> Classifiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Rule> Rules { get; set; } = new();

    /// <summary>Например group:artifact:version или group:artifact:version:classifier.</summary>
    public (string Group, string Artifact, string Version, string? Classifier) ParsedName
    {
        get
        {
            var parts = Name.Split(':');
            return parts.Length switch
            {
                >= 4 => (parts[0], parts[1], parts[2], parts[3]),
                3 => (parts[0], parts[1], parts[2], null),
                _ => ("", Name, "", null)
            };
        }
    }
}

public sealed class DownloadArtifact
{
    public string Path { get; set; } = "";

    public string Sha1 { get; set; } = "";

    public string Url { get; set; } = "";

    public long Size { get; set; }
}

public sealed class VersionArgument
{
    public List<string> Values { get; set; } = new();

    public List<Rule> Rules { get; set; } = new();
}

public sealed class Rule
{
    public string Action { get; set; } = "";

    public RuleOs? Os { get; set; }

    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOs
{
    public string? Name { get; set; }

    public string? Arch { get; set; }

    public string? Version { get; set; }
}

/// <summary>Проверка rules из version json под текущую Windows x64.</summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule> rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, features))
            {
                continue;
            }

            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Rule rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrEmpty(rule.Os.Name) &&
                !string.Equals(rule.Os.Name, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(rule.Os.Arch))
            {
                var actual = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                if (!string.Equals(rule.Os.Arch, actual, StringComparison.OrdinalIgnoreCase) &&
                    !(rule.Os.Arch == "x86" && actual == "x86_64") &&
                    !(rule.Os.Arch == "64" && actual == "x86_64"))
                {
                    return false;
                }
            }
        }

        if (rule.Features is not null)
        {
            foreach (var feature in rule.Features)
            {
                var actual = features is not null &&
                             features.TryGetValue(feature.Key, out var value) &&
                             value;

                if (actual != feature.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
