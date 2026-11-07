using System.Globalization;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using LaunchPlanModel = SamaraLauncher.Core.Models.LaunchPlan;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Собирает команду запуска Forge 1.20.1 по проверенной схеме запуска.
/// </summary>
public sealed class LaunchCommandBuilder
{
    public const string DefaultForgeVersionId = "1.20.1-forge-47.4.28";

    private const string LauncherName = "SamaraLauncher";
    private const string ForgeGroup = "net.minecraftforge";
    private const string DefaultForgeVersion = "47.4.28";
    private const string DefaultMcpVersion = "20230612.114412";
    private const int DefaultServerPort = 25565;

    /// <summary>
    /// Собирает команду запуска Forge. Если задан <paramref name="serverAddress"/>
    /// (host, host:port или [ipv6]:port), в game-аргументы добавляются --server и --port.
    /// </summary>
    public LaunchPlanModel Build(InstanceProfile instance, string javaPath, string? serverAddress = null)
    {
        if (instance is null)
        {
            throw new LauncherException("Профиль не задан.");
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            throw new LauncherException("Не указан путь к java.exe.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("В профиле не указана папка игры.");
        }

        var serverHost = "";
        var serverPort = 0;
        var hasServer = serverAddress is not null;
        if (serverAddress is not null)
        {
            (serverHost, serverPort) = ParseServerAddress(serverAddress);
        }

        var gameDir = GetFullPath(instance.GameDir, "папки игры");
        if (!Directory.Exists(gameDir))
        {
            throw new LauncherException($"Папка игры не существует: {gameDir}");
        }

        var versionId = ResolveVersionId(instance, gameDir);
        var minecraftVersion = ResolveMinecraftVersion(instance, versionId);
        var paths = new VersionPaths(gameDir);

        var forgeJson = paths.VersionJson(versionId);
        var vanillaJson = paths.VersionJson(minecraftVersion);

        var forge = VersionMetadata.Load(forgeJson);
        var vanilla = VersionMetadata.Load(vanillaJson);

        var mainClass = FirstNonEmpty(forge.MainClass, vanilla.MainClass);
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new LauncherException($"В {forgeJson} не указан mainClass.");
        }

        var assetIndex = FirstNonEmpty(
            forge.AssetIndexId,
            forge.Assets,
            vanilla.AssetIndexId,
            vanilla.Assets,
            "5");

        var nativesDir = paths.NativesDir(versionId);
        try
        {
            Directory.CreateDirectory(nativesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось создать папку natives: {nativesDir}", ex);
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion.Current,
            ["library_directory"] = paths.LibrariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["version_name"] = versionId
        };

        var missing = new List<string>();
        var classpath = BuildClasspath(paths, vanilla, forge, variables, missing);

        if (missing.Count > 0)
        {
            var distinct = missing
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var list = string.Join(
                Environment.NewLine,
                distinct.Select(path => "  - " + path));

            throw new LauncherException(
                $"В папке libraries не хватает файлов ({distinct.Count}):{Environment.NewLine}{list}");
        }

        var classpathString = string.Join(Path.PathSeparator, classpath);
        variables["classpath"] = classpathString;

        var username = string.IsNullOrWhiteSpace(instance.Username) ? "Player" : instance.Username.Trim();
        var uuid = string.IsNullOrWhiteSpace(instance.Uuid)
            ? OfflineUuid.FromUsername(username)
            : OfflineUuid.Normalize(instance.Uuid);

        variables["auth_player_name"] = username;
        variables["auth_uuid"] = uuid;
        variables["auth_access_token"] = "0";
        variables["clientid"] = "0";
        variables["auth_xuid"] = "0";
        variables["user_type"] = "legacy";
        variables["version_type"] = "release";
        variables["version_name"] = versionId;
        variables["game_assets"] = paths.AssetsDir;
        variables["auth_session"] = "0";
        variables["user_properties"] = "{}";
        variables["game_directory"] = gameDir;
        variables["assets_root"] = paths.AssetsDir;
        variables["assets_index_name"] = assetIndex;
        variables["resolution_width"] = "854";
        variables["resolution_height"] = "480";

        var memoryMb = instance.MemoryMb;
        if (memoryMb < 1024)
        {
            memoryMb = 6144;
        }

        var minMemoryMb = instance.MinMemoryMb;
        if (minMemoryMb < 512 || minMemoryMb > memoryMb)
        {
            minMemoryMb = Math.Min(2048, memoryMb);
        }

        var arguments = new List<string>
        {
            $"-Xms{minMemoryMb}M",
            $"-Xmx{memoryMb}M"
        };

        foreach (var jvmArg in instance.JvmArgs)
        {
            if (!string.IsNullOrWhiteSpace(jvmArg))
            {
                arguments.Add(jvmArg);
            }
        }

        AddJvmArguments(forge, variables, classpathString, arguments, out var classpathAdded);
        if (!classpathAdded)
        {
            arguments.Add("-cp");
            arguments.Add(classpathString);
        }

        arguments.Add(mainClass);
        AddGameArguments(forge, vanilla, instance, variables, minecraftVersion, arguments);

        if (hasServer)
        {
            AddServerArguments(arguments, serverHost, serverPort);
        }

        return new LaunchPlanModel(
            javaPath,
            gameDir,
            arguments,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            MainClass = mainClass,
            Classpath = classpathString,
            NativesDirectory = nativesDir,
            ServerAddress = hasServer ? serverHost : null,
            ServerPort = hasServer ? serverPort : null
        };
    }

    private static string ResolveVersionId(InstanceProfile instance, string gameDir)
    {
        if (!string.IsNullOrWhiteSpace(instance.VersionId))
        {
            return instance.VersionId.Trim();
        }

        var detected = VersionPaths.DetectForgeVersionId(gameDir);
        return string.IsNullOrWhiteSpace(detected) ? DefaultForgeVersionId : detected;
    }

    private static string ResolveMinecraftVersion(InstanceProfile instance, string versionId)
    {
        if (!string.IsNullOrWhiteSpace(instance.MinecraftVersion))
        {
            return instance.MinecraftVersion.Trim();
        }

        var marker = versionId.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? versionId[..marker] : versionId;
    }

    private static List<string> BuildClasspath(
        VersionPaths paths,
        VersionMetadata vanilla,
        VersionMetadata forge,
        IReadOnlyDictionary<string, string> variables,
        List<string> missing)
    {
        var classpath = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                missing.Add(full);
                return;
            }

            if (seen.Add(full))
            {
                classpath.Add(full);
            }
        }

        foreach (var library in vanilla.Libraries.Concat(forge.Libraries))
        {
            if (!Minecraft.RuleEvaluator.IsAllowed(library.Rules))
            {
                continue;
            }

            var path = !string.IsNullOrWhiteSpace(library.Artifact?.Path)
                ? VersionPaths.LibraryPath(paths.LibrariesDir, library.Artifact!)
                : VersionPaths.GuessLibraryPath(paths.LibrariesDir, library);

            AddFile(path);
        }

        // В classpath нужен именно client-*-extra.jar. Ванильный versions\<mc>\<mc>.jar
        // и forge-*.jar из versions добавлять нельзя: Forge упадёт с ResolutionException.
        var clientLibraries = Path.Combine(paths.LibrariesDir, "net", "minecraft", "client");
        var extraJar = Directory.Exists(clientLibraries)
            ? Directory.EnumerateFiles(clientLibraries, "client-*-extra.jar", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;

        if (extraJar is null)
        {
            missing.Add(Path.Combine(clientLibraries, "client-*-extra.jar"));
        }
        else
        {
            AddFile(extraJar);
        }

        AddJarsRecursive(Path.Combine(paths.LibrariesDir, "net", "minecraftforge", "forge"), AddFile, missing);
        AddJarsRecursive(Path.Combine(paths.LibrariesDir, "net", "minecraftforge", "mergetool"), AddFile, missing);

        AddModulePathJars(forge, variables, AddFile);

        return classpath;
    }

    private static void AddJarsRecursive(string directory, Action<string> addFile, List<string> missing)
    {
        if (!Directory.Exists(directory))
        {
            missing.Add(Path.Combine(directory, "*.jar"));
            return;
        }

        foreach (var jar in Directory.EnumerateFiles(directory, "*.jar", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            addFile(jar);
        }
    }

    /// <summary>
    /// bootstraplauncher, securejarhandler, asm и JarJarFileSystems лежат в module path (-p),
    /// но Forge также ждёт их в -cp (на них ссылаются -DignoreList и securejarhandler).
    /// </summary>
    private static void AddModulePathJars(
        VersionMetadata forge,
        IReadOnlyDictionary<string, string> variables,
        Action<string> addFile)
    {
        var values = FlattenJvmArguments(forge);
        for (var i = 0; i < values.Count; i++)
        {
            string? modulePath = null;

            if (string.Equals(values[i], "-p", StringComparison.Ordinal) ||
                string.Equals(values[i], "--module-path", StringComparison.Ordinal))
            {
                if (i + 1 < values.Count)
                {
                    modulePath = values[++i];
                }
            }
            else if (values[i].StartsWith("-p=", StringComparison.Ordinal))
            {
                modulePath = values[i][3..];
            }
            else if (values[i].StartsWith("--module-path=", StringComparison.Ordinal))
            {
                modulePath = values[i]["--module-path=".Length..];
            }

            if (modulePath is null || modulePath.Contains("${classpath}", StringComparison.Ordinal))
            {
                continue;
            }

            var expanded = Expand(modulePath, variables);
            foreach (var entry in expanded.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = entry.Trim().Trim('"');
                if (path.Length > 0)
                {
                    addFile(path);
                }
            }
        }
    }

    private static void AddJvmArguments(
        VersionMetadata forge,
        IReadOnlyDictionary<string, string> variables,
        string classpath,
        List<string> arguments,
        out bool classpathAdded)
    {
        var values = FlattenJvmArguments(forge);
        classpathAdded = false;

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];

            // В forge json стоит пара "-cp" + "${classpath}". Заменяем её на уже собранный
            // classpath ровно один раз, чтобы не дублировать опцию.
            if (string.Equals(value, "-cp", StringComparison.Ordinal) ||
                string.Equals(value, "-classpath", StringComparison.Ordinal))
            {
                if (i + 1 < values.Count)
                {
                    i++;
                }

                if (!classpathAdded)
                {
                    arguments.Add(value);
                    arguments.Add(classpath);
                    classpathAdded = true;
                }

                continue;
            }

            if (value.Contains("${classpath}", StringComparison.Ordinal))
            {
                continue;
            }

            arguments.Add(Expand(value, variables));
        }
    }

    /// <summary>
    /// Выбирает game-аргументы для Forge-профиля. Самодостаточный Forge json
    /// (без inheritsFrom) уже содержит ванильные аргументы, поэтому добавлять их
    /// из ванильного json нельзя — иначе --gameDir, --username и т.п. дублируются.
    /// </summary>
    internal static IReadOnlyList<VersionArgument> SelectGameArguments(
        VersionMetadata forge,
        VersionMetadata vanilla)
    {
        if (forge.GameArguments.Count == 0)
        {
            return vanilla.GameArguments;
        }

        if (string.IsNullOrWhiteSpace(forge.InheritsFrom))
        {
            return forge.GameArguments;
        }

        return vanilla.GameArguments.Concat(forge.GameArguments).ToList();
    }

    private static void AddGameArguments(
        VersionMetadata forge,
        VersionMetadata vanilla,
        InstanceProfile instance,
        IReadOnlyDictionary<string, string> variables,
        string minecraftVersion,
        List<string> arguments)
    {
        foreach (var entry in SelectGameArguments(forge, vanilla))
        {
            if (!Minecraft.RuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                var expanded = Expand(value, variables);
                if (!string.IsNullOrEmpty(expanded))
                {
                    arguments.Add(expanded);
                }
            }
        }

        // В старом формате Forge (minecraftArguments) этих аргументов нет — без них
        // Forge падает с NullPointerException в ImmediateWindowHandler.load.
        if (!arguments.Any(argument => string.Equals(argument, "--launchTarget", StringComparison.OrdinalIgnoreCase)))
        {
            var forgeVersion = string.IsNullOrWhiteSpace(instance.ForgeVersion)
                ? DefaultForgeVersion
                : instance.ForgeVersion.Trim();

            arguments.AddRange(new[]
            {
                "--launchTarget", "forgeclient",
                "--fml.forgeVersion", forgeVersion,
                "--fml.mcVersion", minecraftVersion,
                "--fml.forgeGroup", ForgeGroup,
                "--fml.mcpVersion", DefaultMcpVersion
            });
        }
    }

    private static void AddServerArguments(List<string> arguments, string host, int port)
    {
        if (!HasOption(arguments, "--server"))
        {
            arguments.Add("--server");
            arguments.Add(host);
        }

        if (!HasOption(arguments, "--port"))
        {
            arguments.Add("--port");
            arguments.Add(port.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool HasOption(IReadOnlyList<string> arguments, string option) =>
        arguments.Any(argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Разбирает host, host:port или [ipv6]:port. Порт по умолчанию — 25565,
    /// IPv6-host возвращается без квадратных скобок.
    /// </summary>
    private static (string Host, int Port) ParseServerAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LauncherException("Не указан адрес сервера.");
        }

        var text = value.Trim();
        string host;
        var port = DefaultServerPort;

        if (text.StartsWith('['))
        {
            var closing = text.IndexOf(']');
            if (closing <= 1)
            {
                throw new LauncherException($"Некорректный адрес сервера: \"{value}\".");
            }

            host = text[1..closing].Trim();
            var rest = text[(closing + 1)..].Trim();
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    throw new LauncherException($"Некорректный адрес сервера: \"{value}\".");
                }

                port = ParseServerPort(rest[1..], value);
            }
        }
        else
        {
            var firstColon = text.IndexOf(':');
            if (firstColon >= 0 && text.IndexOf(':', firstColon + 1) >= 0)
            {
                // IPv6-литерал без скобок: порт не указан.
                host = text;
            }
            else if (firstColon >= 0)
            {
                host = text[..firstColon].Trim();
                port = ParseServerPort(text[(firstColon + 1)..], value);
            }
            else
            {
                host = text;
            }
        }

        if (host.Length == 0)
        {
            throw new LauncherException($"Некорректный адрес сервера: \"{value}\". Не указан host.");
        }

        if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('\\'))
        {
            throw new LauncherException(
                $"Некорректный адрес сервера: \"{value}\". Host не должен содержать пробелы, '/', '\\'.");
        }

        if (host.Contains('[') || host.Contains(']'))
        {
            throw new LauncherException($"Некорректный адрес сервера: \"{value}\".");
        }

        return (host, port);
    }

    private static int ParseServerPort(string value, string address)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new LauncherException(
                $"Некорректный порт сервера в \"{address}\": укажите число от 1 до 65535.");
        }

        return port;
    }

    private static List<string> FlattenJvmArguments(VersionMetadata forge)
    {
        var result = new List<string>();

        foreach (var entry in forge.JvmArguments)
        {
            if (!Minecraft.RuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (key, replacement) in variables)
        {
            value = value.Replace("${" + key + "}", replacement, StringComparison.Ordinal);
        }

        return value;
    }

    private static string GetFullPath(string path, string description)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь к {description}: {path}", ex);
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
