using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Чтение метаданных мода из <c>META-INF/mods.toml</c>, <c>META-INF/neoforge.mods.toml</c>
/// или <c>fabric.mod.json</c>. Ошибка одного jar не прерывает сканирование.
/// </summary>
public sealed class ModMetadataReader
{
    private const string ForgeManifestPath = "META-INF/mods.toml";
    private const string NeoForgeManifestPath = "META-INF/neoforge.mods.toml";
    private const string FabricManifestPath = "fabric.mod.json";
    private const string JarManifestPath = "META-INF/MANIFEST.MF";
    private const string JarJarMetadataPath = "META-INF/jarjar/metadata.json";

    /// <summary>Максимальная глубина вложенных jar, которые ещё разбираются.</summary>
    private const int MaxNestedDepth = 2;

    private const int MaxNestedJarBytes = 64 * 1024 * 1024;

    /// <summary>Читает метаданные jar. Для отсутствующего файла возвращает null.</summary>
    public ModMetadata? Read(string jarPath) => Read(jarPath, null);

    /// <summary>
    /// Читает метаданные jar. Если файл повреждён или не содержит метаданных,
    /// возвращает запасные метаданные (имя файла, <see cref="ModLoader.Unknown"/>)
    /// и добавляет предупреждение в <paramref name="warnings"/>.
    /// Вложенные jar (JarJar, META-INF/jars) попадают в <see cref="ModMetadata.NestedMods"/>.
    /// </summary>
    public ModMetadata? Read(string jarPath, ICollection<string>? warnings)
    {
        if (string.IsNullOrWhiteSpace(jarPath))
        {
            throw new LauncherException("Не указан путь к файлу мода.");
        }

        var fullPath = Path.GetFullPath(jarPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(fullPath);
        var metadata = CreateFallback(fullPath, fileName);

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            ReadArchive(archive, metadata, warnings, depth: 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            warnings?.Add($"Не удалось прочитать {fileName}: {ex.Message}");
        }

        return metadata;
    }

    /// <summary>
    /// Разбирает открытый архив. Возвращает true, если найден манифест мода.
    /// <paramref name="depth"/> — глубина вложенности (0 — корневой jar).
    /// </summary>
    private static bool ReadArchive(
        ZipArchive archive,
        ModMetadata metadata,
        ICollection<string>? warnings,
        int depth)
    {
        var neoForgeEntry = FindEntry(archive, NeoForgeManifestPath);
        var forgeEntry = FindEntry(archive, ForgeManifestPath);
        var fabricEntry = FindEntry(archive, FabricManifestPath);

        var parsed = false;

        if (neoForgeEntry is not null)
        {
            parsed = TryParseForgeToml(
                ReadEntryText(neoForgeEntry),
                ModLoader.NeoForge,
                archive,
                metadata,
                warnings);
        }

        if (!parsed && forgeEntry is not null)
        {
            parsed = TryParseForgeToml(
                ReadEntryText(forgeEntry),
                ModLoader.Forge,
                archive,
                metadata,
                warnings);
        }

        if (!parsed && fabricEntry is not null)
        {
            parsed = TryParseFabric(ReadEntryText(fabricEntry), metadata, warnings);
        }

        if (depth < MaxNestedDepth)
        {
            ReadNestedMods(archive, metadata, depth);
        }

        // Контейнер с вложенными модами (например, kotlinforforge) — корневых
        // метаданных нет, но мод лежит внутри, поэтому это не проблема.
        if (!parsed && metadata.NestedMods.Count == 0)
        {
            warnings?.Add(
                $"В {metadata.FileName} не найдены метаданные Forge/NeoForge/Fabric — используется имя файла.");
        }

        return parsed;
    }

    private static void ReadNestedMods(ZipArchive archive, ModMetadata metadata, int depth)
    {
        List<ZipArchiveEntry> entries;

        try
        {
            entries = FindNestedJarEntries(archive);
        }
        catch (Exception ex) when (IsArchiveError(ex))
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                var bytes = ReadEntryBytes(entry, MaxNestedJarBytes);
                if (bytes is null)
                {
                    continue;
                }

                var nestedFileName = GetEntryFileName(entry);
                if (string.IsNullOrWhiteSpace(nestedFileName))
                {
                    continue;
                }

                var nested = CreateFallback(metadata.FullPath, nestedFileName);
                nested.Enabled = metadata.Enabled;

                using var stream = new MemoryStream(bytes, writable: false);
                using var nestedArchive = new ZipArchive(stream, ZipArchiveMode.Read);

                // Предупреждения вложенных jar не засоряют общий отчёт.
                if (!ReadArchive(nestedArchive, nested, null, depth + 1))
                {
                    continue;
                }

                metadata.NestedMods.Add(nested);
            }
            catch (Exception ex) when (IsArchiveError(ex))
            {
                // Повреждённый вложенный jar не мешает чтению остальных.
            }
        }
    }

    private static List<ZipArchiveEntry> FindNestedJarEntries(ZipArchive archive)
    {
        var result = new List<ZipArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ReadJarJarPaths(archive))
        {
            var entry = FindEntry(archive, NormalizeEntryPath(path));
            if (entry is not null && seen.Add(entry.FullName))
            {
                result.Add(entry);
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = NormalizeEntryPath(entry.FullName);
            if (path.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(entry.FullName))
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    private static List<string> ReadJarJarPaths(ZipArchive archive)
    {
        var paths = new List<string>();
        var entry = FindEntry(archive, JarJarMetadataPath);
        if (entry is null)
        {
            return paths;
        }

        try
        {
            using var document = JsonDocument.Parse(
                ReadEntryText(entry),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jars", out var jars) ||
                jars.ValueKind != JsonValueKind.Array)
            {
                return paths;
            }

            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind == JsonValueKind.Object &&
                    jar.TryGetProperty("path", out var path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    var value = path.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        paths.Add(value);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Некорректный metadata.json не мешает искать jar по путям META-INF.
        }
        catch (InvalidDataException)
        {
            // Слишком большой metadata.json пропускаем.
        }

        return paths;
    }

    private static string NormalizeEntryPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string GetEntryFileName(ZipArchiveEntry entry)
    {
        var name = NormalizeEntryPath(entry.FullName);
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static bool IsArchiveError(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException
            or NotSupportedException or JsonException;

    private static byte[]? ReadEntryBytes(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > maxBytes)
            {
                return null;
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static ModMetadata CreateFallback(string fullPath, string fileName)
    {
        var modId = GetFallbackModId(fileName);

        return new ModMetadata
        {
            ModId = modId,
            Name = modId,
            FileName = fileName,
            FullPath = fullPath,
            Enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
            Loader = ModLoader.Unknown
        };
    }

    private static string GetFallbackModId(string fileName)
    {
        var name = fileName;

        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".disabled".Length];
        }

        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".jar".Length];
        }

        return string.IsNullOrWhiteSpace(name) ? fileName : name;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                entry.FullName.Replace('\\', '/').TrimStart('/'),
                path,
                StringComparison.OrdinalIgnoreCase));

    private const int MaxManifestEntryBytes = 4 * 1024 * 1024;

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxManifestEntryBytes)
        {
            throw new InvalidDataException(
                $"Запись {entry.FullName} слишком большая для чтения метаданных.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxManifestEntryBytes)
            {
                throw new InvalidDataException(
                    $"Запись {entry.FullName} слишком большая для чтения метаданных.");
            }

            memory.Write(buffer, 0, read);
        }

        memory.Position = 0;

        using var reader = new StreamReader(
            memory,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    // ------------------------------ Forge / NeoForge ------------------------------

    private static bool TryParseForgeToml(
        string toml,
        ModLoader loader,
        ZipArchive archive,
        ModMetadata metadata,
        ICollection<string>? warnings)
    {
        var tables = ParseToml(toml);
        var modTables = tables
            .Where(table => string.Equals(table.Name, "mods", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (modTables.Count == 0)
        {
            // На случай очень старых или самодельных mods.toml с ключами в корне.
            var root = tables.FirstOrDefault();
            if (root is null || !root.Values.ContainsKey("modId"))
            {
                return false;
            }

            modTables.Add(root);
        }

        // Несколько блоков [[mods]] встречаются у stub-модов и не являются ошибкой,
        // поэтому используется первый блок без предупреждения.
        var mod = modTables[0];
        var modId = mod.GetString("modId").Trim();

        if (string.IsNullOrWhiteSpace(modId))
        {
            warnings?.Add($"В {metadata.FileName} не указан modId.");
            return false;
        }

        metadata.ModId = modId;
        metadata.Name = FirstNonEmpty(mod.GetString("displayName"), modId);
        metadata.Version = ResolveVersion(mod.GetString("version"), archive);
        metadata.Description = mod.GetString("description").Trim();
        metadata.Authors = ParseTomlAuthors(mod);
        metadata.Loader = loader;

        var dependencies = new List<ModDependency>();

        foreach (var table in tables)
        {
            if (!table.Name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var owner = table.Name["dependencies.".Length..].Trim();
            if (!IsDependencyTableFor(owner, modId, modTables.Count))
            {
                continue;
            }

            var dependencyId = table.GetString("modId").Trim();
            if (string.IsNullOrWhiteSpace(dependencyId))
            {
                continue;
            }

            dependencies.Add(new ModDependency
            {
                ModId = dependencyId,
                VersionRange = table.GetString("versionRange").Trim(),
                Mandatory = ParseMandatory(table),
                Side = NormalizeSide(table.GetString("side")),
                Ordering = NormalizeOrdering(table.GetString("ordering"))
            });
        }

        metadata.Dependencies = dependencies;
        return true;
    }

    private static bool IsDependencyTableFor(string owner, string modId, int modCount) =>
        string.Equals(owner, modId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(owner, "${mod_id}", StringComparison.OrdinalIgnoreCase)
        || modCount == 1;

    private static bool ParseMandatory(TomlTable table)
    {
        var raw = table.GetString("mandatory").Trim();

        if (bool.TryParse(raw, out var mandatory))
        {
            return mandatory;
        }

        // NeoForge 1.20.5+ использует type вместо mandatory.
        var type = table.GetString("type").Trim().ToLowerInvariant();
        return type switch
        {
            "optional" or "suggests" or "discouraged" or "incompatible" => false,
            _ => true
        };
    }

    private static string NormalizeSide(string value)
    {
        var side = value.Trim().ToLowerInvariant();
        return side switch
        {
            "client" => "client",
            "server" => "server",
            _ => "both"
        };
    }

    private static string NormalizeOrdering(string value)
    {
        var ordering = value.Trim().ToUpperInvariant();
        return ordering switch
        {
            "BEFORE" => "BEFORE",
            "AFTER" => "AFTER",
            _ => "NONE"
        };
    }

    private static List<string> ParseTomlAuthors(TomlTable table)
    {
        var authors = new List<string>();

        foreach (var key in new[] { "authors", "authorList", "author" })
        {
            if (!table.Values.TryGetValue(key, out var value))
            {
                continue;
            }

            foreach (var item in value.AsList())
            {
                foreach (var part in item.Split(
                             ',',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddUnique(authors, part);
                }
            }
        }

        return authors;
    }

    private static string ResolveVersion(string version, ZipArchive archive)
    {
        var value = version.Trim();

        if (value.Length == 0 ||
            value.IndexOf("${file.jarVersion}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return value;
        }

        var manifestEntry = FindEntry(archive, JarManifestPath);
        if (manifestEntry is null)
        {
            return value;
        }

        var attributes = ParseManifest(ReadEntryText(manifestEntry));
        var replacement =
            FirstNonEmpty(
                attributes.GetValueOrDefault("Implementation-Version"),
                attributes.GetValueOrDefault("Specification-Version"));

        return string.IsNullOrWhiteSpace(replacement)
            ? value
            : value.Replace("${file.jarVersion}", replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseManifest(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (rawLine.StartsWith(' '))
            {
                if (currentKey is not null)
                {
                    attributes[currentKey] += rawLine[1..];
                }

                continue;
            }

            var colon = rawLine.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            currentKey = rawLine[..colon].Trim();
            attributes[currentKey] = rawLine[(colon + 1)..].Trim();
        }

        return attributes;
    }

    // ----------------------------------- Fabric -----------------------------------

    private static bool TryParseFabric(
        string json,
        ModMetadata metadata,
        ICollection<string>? warnings)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            warnings?.Add($"Некорректный fabric.mod.json в {metadata.FileName}: {ex.Message}");
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings?.Add($"Некорректный fabric.mod.json в {metadata.FileName}: ожидался объект.");
                return false;
            }

            var modId = GetJsonString(root, "id").Trim();
            if (string.IsNullOrWhiteSpace(modId))
            {
                warnings?.Add($"В fabric.mod.json ({metadata.FileName}) не указан id.");
                return false;
            }

            metadata.ModId = modId;
            metadata.Name = FirstNonEmpty(GetJsonString(root, "name"), modId);
            metadata.Version = GetJsonString(root, "version").Trim();
            metadata.Description = GetJsonString(root, "description").Trim();
            metadata.Authors = ReadJsonAuthors(root);
            metadata.Loader = ModLoader.Fabric;

            var environment = GetJsonString(root, "environment").Trim();
            metadata.IsClientSideOnly = environment.Equals("client", StringComparison.OrdinalIgnoreCase);
            metadata.IsServerSideOnly = environment.Equals("server", StringComparison.OrdinalIgnoreCase);

            var dependencies = new List<ModDependency>();

            if (root.TryGetProperty("depends", out var depends) &&
                depends.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in depends.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name))
                    {
                        continue;
                    }

                    dependencies.Add(new ModDependency
                    {
                        ModId = property.Name.Trim(),
                        VersionRange = ReadJsonVersionRange(property.Value),
                        Mandatory = true,
                        Side = "both",
                        Ordering = "NONE"
                    });
                }
            }

            metadata.Dependencies = dependencies;
            return true;
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    private static List<string> ReadJsonAuthors(JsonElement root)
    {
        var authors = new List<string>();

        if (!root.TryGetProperty("authors", out var value))
        {
            return authors;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddUnique(authors, item.GetString());
                }
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("name", out var name) &&
                         name.ValueKind == JsonValueKind.String)
                {
                    AddUnique(authors, name.GetString());
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            foreach (var part in (value.GetString() ?? "").Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddUnique(authors, part);
            }
        }

        return authors;
    }

    private static string ReadJsonVersionRange(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim() ?? "";
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var ranges = value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

        return string.Join(" || ", ranges);
    }

    // ------------------------------- Мини-парсер TOML -------------------------------

    private static IReadOnlyList<TomlTable> ParseToml(string text)
    {
        var tables = new List<TomlTable>();
        var current = new TomlTable("");
        tables.Add(current);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripTomlComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                var end = line.IndexOf("]]", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                current = new TomlTable(line[2..end].Trim());
                tables.Add(current);
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                var end = line.IndexOf(']');
                if (end < 0)
                {
                    continue;
                }

                current = new TomlTable(line[1..end].Trim());
                tables.Add(current);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var valueText = line[(equals + 1)..].Trim();

            if (valueText.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                while (!HasClosingDelimiter(valueText, "\"\"\"") && index + 1 < lines.Length)
                {
                    index++;
                    valueText += "\n" + lines[index];
                }
            }
            else if (valueText.StartsWith("'''", StringComparison.Ordinal))
            {
                while (!HasClosingDelimiter(valueText, "'''") && index + 1 < lines.Length)
                {
                    index++;
                    valueText += "\n" + lines[index];
                }
            }
            else if (CountBracketDepth(valueText) > 0)
            {
                while (CountBracketDepth(valueText) > 0 && index + 1 < lines.Length)
                {
                    index++;
                    valueText += "\n" + StripTomlComment(lines[index]);
                }
            }

            if (current.Name.Length == 0 && key.Equals("mods", StringComparison.OrdinalIgnoreCase) && valueText.StartsWith("[", StringComparison.Ordinal))
            {
                foreach (var inlineTable in ParseInlineModTables(valueText)) tables.Add(inlineTable);
            }

            if (key.Length > 0)
            {
                current.Values[key] = ParseTomlValue(valueText);
            }
        }

        return tables;
    }

    private static IEnumerable<TomlTable> ParseInlineModTables(string text)
    {
        var start = -1;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (quote == '"' && ch == '\\' && i + 1 < text.Length) { i++; continue; }
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch == '\'' || ch == '"') { quote = ch; continue; }
            if (ch == '{') { if (depth++ == 0) start = i + 1; continue; }
            if (ch != '}' || depth == 0) continue;
            if (--depth != 0 || start < 0) continue;

            var table = new TomlTable("mods");
            foreach (var entry in SplitArrayElements(text[start..i]))
            {
                var equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                var key = entry[..equals].Trim();
                if (key.Length > 0) table.Values[key] = ParseTomlValue(entry[(equals + 1)..]);
            }
            if (!string.IsNullOrWhiteSpace(table.GetString("modId"))) yield return table;
            start = -1;
        }
    }

    private static bool HasClosingDelimiter(string text, string delimiter) =>
        text.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal) >= 0;

    private static string StripTomlComment(string line)
    {
        var inBasic = false;
        var inLiteral = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inBasic)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inBasic = false;
                }

                continue;
            }

            if (inLiteral)
            {
                if (c == '\'')
                {
                    inLiteral = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inBasic = true;
                    break;
                case '\'':
                    inLiteral = true;
                    break;
                case '#':
                    return line[..i];
            }
        }

        return line;
    }

    private static int CountBracketDepth(string text)
    {
        var depth = 0;
        var inBasic = false;
        var inLiteral = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inBasic)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inBasic = false;
                }

                continue;
            }

            if (inLiteral)
            {
                if (c == '\'')
                {
                    inLiteral = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inBasic = true;
                    break;
                case '\'':
                    inLiteral = true;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
            }
        }

        return Math.Max(0, depth);
    }

    private static TomlValue ParseTomlValue(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var end = text.LastIndexOf(']');
            var inner = end > 0 ? text[1..end] : text[1..];
            var items = SplitArrayElements(inner)
                .Select(ParseTomlScalar)
                .Where(item => item.Length > 0)
                .ToList();

            return TomlValue.FromArray(items);
        }

        return TomlValue.FromScalar(ParseTomlScalar(text));
    }

    private static List<string> SplitArrayElements(string text)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();
        var inBasic = false;
        var inLiteral = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inBasic)
            {
                buffer.Append(c);

                if (c == '\\' && i + 1 < text.Length)
                {
                    buffer.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inBasic = false;
                }

                continue;
            }

            if (inLiteral)
            {
                buffer.Append(c);

                if (c == '\'')
                {
                    inLiteral = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inBasic = true;
                    buffer.Append(c);
                    break;
                case '\'':
                    inLiteral = true;
                    buffer.Append(c);
                    break;
                case ',':
                    AddArrayElement(result, buffer);
                    break;
                default:
                    buffer.Append(c);
                    break;
            }
        }

        AddArrayElement(result, buffer);
        return result;
    }

    private static void AddArrayElement(List<string> result, StringBuilder buffer)
    {
        var item = buffer.ToString().Trim();
        buffer.Clear();

        if (item.Length > 0)
        {
            result.Add(item);
        }
    }

    private static string ParseTomlScalar(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return "";
        }

        if (value.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            var end = value.IndexOf("\"\"\"", 3, StringComparison.Ordinal);
            var inner = end > 0 ? value[3..end] : value[3..];
            if (inner.StartsWith('\n'))
            {
                inner = inner[1..];
            }

            return UnescapeTomlBasicString(inner);
        }

        if (value.StartsWith("'''", StringComparison.Ordinal))
        {
            var end = value.IndexOf("'''", 3, StringComparison.Ordinal);
            var inner = end > 0 ? value[3..end] : value[3..];
            if (inner.StartsWith('\n'))
            {
                inner = inner[1..];
            }

            return inner;
        }

        if (value[0] == '"')
        {
            return UnescapeTomlBasicString(ReadQuotedTomlString(value, '"'));
        }

        if (value[0] == '\'')
        {
            return ReadQuotedTomlString(value, '\'');
        }

        return value;
    }

    private static string ReadQuotedTomlString(string text, char quote)
    {
        var buffer = new StringBuilder();

        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];

            if (quote == '"' && c == '\\' && i + 1 < text.Length)
            {
                buffer.Append(c);
                buffer.Append(text[++i]);
                continue;
            }

            if (c == quote)
            {
                break;
            }

            buffer.Append(c);
        }

        return buffer.ToString();
    }

    private static string UnescapeTomlBasicString(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var buffer = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c != '\\' || i + 1 >= value.Length)
            {
                buffer.Append(c);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'b':
                    buffer.Append('\b');
                    break;
                case 't':
                    buffer.Append('\t');
                    break;
                case 'n':
                    buffer.Append('\n');
                    break;
                case 'f':
                    buffer.Append('\f');
                    break;
                case 'r':
                    buffer.Append('\r');
                    break;
                case '"':
                    buffer.Append('"');
                    break;
                case '\\':
                    buffer.Append('\\');
                    break;
                case 'u' when i + 4 < value.Length:
                    if (int.TryParse(
                            value.AsSpan(i + 1, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var codeUnit))
                    {
                        buffer.Append((char)codeUnit);
                        i += 4;
                    }
                    else
                    {
                        buffer.Append("\\u");
                    }

                    break;
                case 'U' when i + 8 < value.Length:
                    if (int.TryParse(
                            value.AsSpan(i + 1, 8),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var codePoint) &&
                        codePoint is >= 0 and <= 0x10FFFF)
                    {
                        buffer.Append(char.ConvertFromUtf32(codePoint));
                        i += 8;
                    }
                    else
                    {
                        buffer.Append("\\U");
                    }

                    break;
                default:
                    buffer.Append(next);
                    break;
            }
        }

        return buffer.ToString();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static void AddUnique(List<string> target, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!target.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(text);
        }
    }

    private sealed class TomlTable
    {
        public TomlTable(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public Dictionary<string, TomlValue> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string GetString(string key) =>
            Values.TryGetValue(key, out var value) ? value.AsString() : "";
    }

    private sealed class TomlValue
    {
        public bool IsArray { get; private init; }

        public string Scalar { get; private init; } = "";

        public List<string> Items { get; private init; } = new();

        public static TomlValue FromScalar(string value) => new() { Scalar = value };

        public static TomlValue FromArray(List<string> items) =>
            new() { IsArray = true, Items = items };

        public string AsString() => IsArray ? string.Join(", ", Items) : Scalar;

        public List<string> AsList()
        {
            if (IsArray)
            {
                return Items;
            }

            return string.IsNullOrWhiteSpace(Scalar)
                ? new List<string>()
                : new List<string> { Scalar };
        }
    }
}
