using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Собирает команду запуска Forge 1.20.1 по проверенной схеме запуска.
/// </summary>
public sealed class LaunchPlanBuilder
{
    private const string LauncherName = "SamaraLauncher";

    private readonly List<string> _warnings = new();

    public LaunchPlan Build(InstanceProfile instance, LaunchOptions? options = null)
    {
        options ??= new LaunchOptions();
        _warnings.Clear();

        var gameDir = Path.GetFullPath(instance.GameDir);
        if (!Directory.Exists(gameDir))
        {
            throw new LauncherException($"Папка игры не существует: {gameDir}");
        }

        var versionId = FirstNonEmpty(options.VersionId, instance.VersionId);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            versionId = VersionManifestLoader.DetectVersionId(gameDir, instance.MinecraftVersion);
        }

        var minecraftVersion = FirstNonEmpty(instance.MinecraftVersion, DeriveMinecraftVersion(versionId));
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new LauncherException($"Не удалось определить версию Minecraft из \"{versionId}\".");
        }

        var versionsDir = Path.Combine(gameDir, "versions");
        var forgeJson = Path.Combine(versionsDir, versionId, versionId + ".json");
        var vanillaJson = Path.Combine(versionsDir, minecraftVersion, minecraftVersion + ".json");

        var forgeManifest = VersionManifestLoader.Load(forgeJson);
        var vanillaManifest = VersionManifestLoader.Load(vanillaJson);

        var mainClass = FirstNonEmpty(forgeManifest.MainClass, vanillaManifest.MainClass);
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new LauncherException($"В {forgeJson} не указан mainClass.");
        }

        var assetIndex = FirstNonEmpty(forgeManifest.AssetIndex?.Id, vanillaManifest.AssetIndex?.Id, "5");
        var librariesDir = Path.Combine(gameDir, "libraries");
        var nativesDir = Path.Combine(versionsDir, versionId, "natives");
        Directory.CreateDirectory(nativesDir);

        var classpath = BuildClasspath(gameDir, librariesDir, versionId, minecraftVersion, vanillaManifest, forgeManifest);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion.Current,
            ["library_directory"] = librariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["version_name"] = versionId
        };

        AddModulePathToClasspath(forgeManifest, variables, classpath, librariesDir);
        variables["classpath"] = string.Join(Path.PathSeparator, classpath);

        var username = FirstNonEmpty(options.Username, instance.Username, "Player");
        var uuid = NormalizeUuid(FirstNonEmpty(options.Uuid, instance.Uuid));
        if (string.IsNullOrWhiteSpace(uuid))
        {
            uuid = OfflineIdentity.ComputeUuid(username);
        }

        var memoryMb = options.MemoryMb ?? instance.MemoryMb;
        var minMemoryMb = options.MinMemoryMb ?? instance.MinMemoryMb;
        if (memoryMb < 1024)
        {
            throw new LauncherException($"Слишком мало памяти: {memoryMb} МБ (минимум 1024).");
        }

        if (minMemoryMb < 512 || minMemoryMb > memoryMb)
        {
            minMemoryMb = Math.Min(2048, memoryMb);
        }

        if (memoryMb > 8192)
        {
            _warnings.Add($"Выделено {memoryMb} МБ. На машине с 16 ГБ RAM больше 8 ГБ может не хватить системе.");
        }

        variables["auth_player_name"] = username;
        variables["auth_uuid"] = uuid;
        variables["auth_access_token"] = "0";
        variables["clientid"] = "0";
        variables["auth_xuid"] = "0";
        variables["user_type"] = "legacy";
        variables["version_type"] = "release";
        variables["game_directory"] = gameDir;
        variables["assets_root"] = Path.Combine(gameDir, "assets");
        variables["assets_index_name"] = assetIndex;

        var args = new List<string>
        {
            $"-Xms{minMemoryMb}M",
            $"-Xmx{memoryMb}M"
        };

        args.AddRange(instance.JvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)));
        args.AddRange(options.ExtraJvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)));

        var jvmEntries = forgeManifest.Arguments?.Jvm;
        if (jvmEntries is { Count: > 0 })
        {
            foreach (var entry in jvmEntries)
            {
                if (!RuleEvaluator.IsAllowed(entry.Rules))
                {
                    continue;
                }

                foreach (var value in entry.Values)
                {
                    args.Add(Expand(value, variables));
                }
            }
        }
        else
        {
            throw new LauncherException($"В {forgeJson} нет arguments.jvm — это не Forge-профиль?");
        }

        var gameEntries = forgeManifest.Arguments?.Game;
        if (gameEntries is not { Count: > 0 })
        {
            gameEntries = vanillaManifest.Arguments?.Game;
        }

        if (gameEntries is not { Count: > 0 })
        {
            throw new LauncherException($"В {forgeJson} нет arguments.game — нечего передавать игре.");
        }

        args.Add(mainClass);

        foreach (var entry in gameEntries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            args.AddRange(entry.Values.Select(value => Expand(value, variables)));
        }

        var javaPath = FirstNonEmpty(options.JavaPath, instance.JavaPath, "auto");
        var resolvedJava = JavaLocator.Resolve(gameDir, javaPath);
        if (string.Equals(resolvedJava, "java", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add("Java не найдена в runtime инстанса и JAVA_HOME — будет использована java из PATH.");
        }

        if (!File.Exists(Path.Combine(gameDir, "assets", "indexes", assetIndex + ".json")))
        {
            _warnings.Add($"Нет asset index {assetIndex}.json — звук и шрифты могут работать некорректно.");
        }

        return new LaunchPlan
        {
            JavaPath = resolvedJava,
            WorkingDirectory = gameDir,
            MainClass = mainClass,
            Arguments = args,
            Warnings = _warnings.ToArray()
        };
    }

    private List<string> BuildClasspath(
        string gameDir,
        string librariesDir,
        string versionId,
        string minecraftVersion,
        VersionManifest vanillaManifest,
        VersionManifest forgeManifest)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        void AddFile(string path)
        {
            if (!File.Exists(path))
            {
                missing.Add(path);
                return;
            }

            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                result.Add(full);
            }
        }

        foreach (var library in vanillaManifest.Libraries.Concat(forgeManifest.Libraries))
        {
            var relative = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrWhiteSpace(relative))
            {
                relative = RelativePathFromName(library.Name);
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            AddFile(Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        var clientExtra = Directory.Exists(Path.Combine(librariesDir, "net", "minecraft", "client"))
            ? Directory.EnumerateFiles(
                    Path.Combine(librariesDir, "net", "minecraft", "client"),
                    "*-extra.jar",
                    SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (clientExtra is not null)
        {
            AddFile(clientExtra);
        }
        else
        {
            var vanillaJar = Path.Combine(gameDir, "versions", minecraftVersion, minecraftVersion + ".jar");
            if (File.Exists(vanillaJar))
            {
                AddFile(vanillaJar);
            }
            else
            {
                _warnings.Add("Не найден client-*-extra.jar и ванильный jar — запуск, скорее всего, упадёт.");
            }
        }

        AddJarsRecursive(Path.Combine(librariesDir, "net", "minecraftforge", "forge"), AddFile);
        AddJarsRecursive(Path.Combine(librariesDir, "net", "minecraftforge", "mergetool"), AddFile);

        // Часть записей forge-манифеста может отсутствовать на диске (например, epoll для Linux
        // или служебный mergetool без classifier). Предупреждаем только о значимых пропусках.
        var significantMissing = missing
            .Where(path => !path.Contains("netty-transport-native-epoll", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("mergetool-1.1.5.jar", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (significantMissing.Count > 0)
        {
            var sample = string.Join(", ", significantMissing.Take(5).Select(Path.GetFileName));
            _warnings.Add($"Не найдено библиотек: {significantMissing.Count} ({sample}{(significantMissing.Count > 5 ? ", ..." : "")}).");
        }

        var vanillaJarPath = Path.GetFullPath(
            Path.Combine(gameDir, "versions", minecraftVersion, minecraftVersion + ".jar"));

        if (!result.Any(path => path.EndsWith("-extra.jar", StringComparison.OrdinalIgnoreCase))
            && !result.Contains(vanillaJarPath, StringComparer.OrdinalIgnoreCase))
        {
            _warnings.Add("В classpath нет client jar Minecraft.");
        }

        return result;
    }

    private static void AddJarsRecursive(string directory, Action<string> add)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var jar in Directory.EnumerateFiles(directory, "*.jar", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            add(jar);
        }
    }

    /// <summary>
    /// bootstraplauncher, securejarhandler и asm лежат в module path (-p),
    /// но Forge также ожидает их в -cp (на них ссылается -DignoreList).
    /// </summary>
    private static void AddModulePathToClasspath(
        VersionManifest forgeManifest,
        Dictionary<string, string> variables,
        List<string> classpath,
        string librariesDir)
    {
        var seen = new HashSet<string>(classpath, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in forgeManifest.Arguments?.Jvm ?? new List<ArgumentEntry>())
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                // -DignoreList тоже содержит слово bootstraplauncher, но это не module path.
                if (!value.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("-D", StringComparison.Ordinal))
                {
                    continue;
                }

                var expanded = Expand(value, variables);
                foreach (var module in expanded.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Forge json uses forward slashes; приводим к тому же виду, что и остальные
                    // записи classpath, иначе dedupe не сработает.
                    var modulePath = Path.GetFullPath(module);
                    if (seen.Add(modulePath))
                    {
                        classpath.Add(modulePath);
                    }
                }
            }
        }
    }

    internal static string RelativePathFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return "";
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var versionPart = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";

        var extension = "jar";
        var at = versionPart.IndexOf('@');
        if (at >= 0)
        {
            extension = versionPart[(at + 1)..];
            versionPart = versionPart[..at];
        }
        else
        {
            var lastColon = classifier.IndexOf('@');
            if (lastColon >= 0)
            {
                extension = classifier[(lastColon + 1)..];
                classifier = classifier[..lastColon];
            }
        }

        return $"{group}/{artifact}/{versionPart}/{artifact}-{versionPart}{classifier}.{extension}";
    }

    private static string DeriveMinecraftVersion(string versionId)
    {
        var marker = versionId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? versionId[..marker] : versionId;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (key, replacement) in variables)
        {
            value = value.Replace("${" + key + "}", replacement, StringComparison.Ordinal);
        }

        return value;
    }

    private static string NormalizeUuid(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : OfflineIdentity.NormalizeUuid(value);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
