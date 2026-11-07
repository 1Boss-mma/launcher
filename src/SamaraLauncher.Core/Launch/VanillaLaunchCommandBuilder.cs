using System.Globalization;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using LaunchPlanModel = SamaraLauncher.Core.Models.LaunchPlan;
using MinecraftRuleEvaluator = SamaraLauncher.Core.Minecraft.RuleEvaluator;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Собирает команду запуска Minecraft из version json с учётом <c>inheritsFrom</c>:
/// ванильные версии, Fabric, произвольный Forge и OptiFine. Поддерживаются
/// современные <c>arguments.jvm/game</c> и старый <c>minecraftArguments</c>.
/// </summary>
public sealed class VanillaLaunchCommandBuilder
{
    private const string LauncherName = "SamaraLauncher";
    private const int DefaultServerPort = 25565;

    /// <summary>
    /// Собирает план запуска версии. Если задан <paramref name="serverAddress"/>
    /// (host, host:port или [ipv6]:port), добавляются --server и --port.
    /// </summary>
    public LaunchPlanModel Build(
        InstanceProfile instance,
        string javaPath,
        string? serverAddress = null)
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

        var gameDir = GetFullPath(instance.GameDir, "папки игры");
        if (!Directory.Exists(gameDir))
        {
            throw new LauncherException($"Папка игры не существует: {gameDir}");
        }

        var versionId = ResolveVersionId(instance);
        var paths = new VersionPaths(gameDir);

        var resolved = InheritedVersionResolver.Load(gameDir, versionId);
        var metadata = resolved.Metadata;
        var versionJson = paths.VersionJson(versionId);

        var mainClass = metadata.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new LauncherException($"В {versionJson} не указан mainClass.");
        }

        var assetIndex = FirstNonEmpty(metadata.AssetIndexId, metadata.Assets, "legacy");
        var legacyAssets = MinecraftVersionRules.IsLegacyAssets(
            resolved.BaseVersionId,
            metadata.Assets,
            assetIndex);

        var assetsRoot = legacyAssets
            ? Path.Combine(paths.AssetsDir, "virtual", assetIndex)
            : paths.AssetsDir;

        var nativesDir = paths.NativesDir(versionId);
        try
        {
            Directory.CreateDirectory(nativesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось создать папку natives: {nativesDir}", ex);
        }

        var extraJar = instance.IsForge && !instance.IsFabric
            ? FindForgeExtraJar(paths.LibrariesDir, resolved.BaseVersionId)
            : null;

        var clientJar = ResolveClientJar(paths, metadata, resolved.BaseVersionId);
        var missing = new List<string>();
        var classpath = BuildClasspath(paths, metadata, clientJar, extraJar, missing);

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

        var username = string.IsNullOrWhiteSpace(instance.Username)
            ? "Player"
            : instance.Username.Trim();

        var uuid = string.IsNullOrWhiteSpace(instance.Uuid)
            ? OfflineUuid.FromUsername(username)
            : OfflineUuid.Normalize(instance.Uuid);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = username,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = "0",
            ["auth_session"] = "0",
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
            ["user_type"] = "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = FirstNonEmpty(metadata.Type, "release"),
            ["version_name"] = FirstNonEmpty(metadata.Id, versionId),
            ["game_directory"] = gameDir,
            ["assets_root"] = assetsRoot,
            ["game_assets"] = assetsRoot,
            ["assets_index_name"] = assetIndex,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion.Current,
            ["library_directory"] = paths.LibrariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = classpathString,
            ["resolution_width"] = "854",
            ["resolution_height"] = "480"
        };

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

        AddJvmArguments(
            metadata,
            variables,
            classpathString,
            arguments,
            out var classpathAdded);

        if (!classpathAdded)
        {
            arguments.Add("-cp");
            arguments.Add(classpathString);
        }

        EnsureJavaLibraryPath(arguments, nativesDir);

        arguments.Add(mainClass);
        AddGameArguments(metadata, variables, arguments, versionJson);

        var hasServer = serverAddress is not null;
        var serverHost = "";
        var serverPort = 0;

        if (hasServer)
        {
            (serverHost, serverPort) = ParseServerAddress(serverAddress!);
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

    /// <summary>
    /// Id версии для запуска: VersionId (включая Forge/Fabric/OptiFine), иначе MinecraftVersion.
    /// </summary>
    public static string ResolveVersionId(InstanceProfile instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var versionId = instance.VersionId?.Trim() ?? "";
        if (versionId.Length > 0)
        {
            return versionId;
        }

        var minecraftVersion = instance.MinecraftVersion?.Trim() ?? "";
        if (minecraftVersion.Length > 0)
        {
            return minecraftVersion;
        }

        throw new LauncherException("Не удалось определить версию Minecraft: укажите VersionId.");
    }

    /// <summary>
    /// Требуемая major-версия Java: javaVersion.majorVersion из слитого version json,
    /// иначе таблица совместимости Minecraft. Базовая версия берётся из профиля или
    /// из корня цепочки inheritsFrom. Ошибка чтения json не пробрасывается.
    /// </summary>
    public static int GetRequiredJavaMajor(InstanceProfile instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        string versionId;
        try
        {
            versionId = ResolveVersionId(instance);
        }
        catch (LauncherException)
        {
            return MinecraftJavaVersion.GetRequiredMajor(instance.BaseMinecraftVersion, null);
        }

        var baseVersion = instance.BaseMinecraftVersion;
        int? declaredMajor = null;

        if (!string.IsNullOrWhiteSpace(instance.GameDir))
        {
            try
            {
                var resolved = InheritedVersionResolver.Load(instance.GameDir, versionId);
                declaredMajor = resolved.Metadata.JavaMajorVersion;
                if (!string.IsNullOrWhiteSpace(resolved.BaseVersionId))
                {
                    baseVersion = resolved.BaseVersionId;
                }
            }
            catch (Exception ex) when (
                ex is LauncherException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Повреждённый json будет обработан при сборке плана; здесь достаточно таблицы.
            }
        }

        return MinecraftJavaVersion.GetRequiredMajor(baseVersion, declaredMajor);
    }

    private static string ResolveClientJar(
        VersionPaths paths,
        VersionMetadata metadata,
        string baseVersionId)
    {
        if (!string.IsNullOrWhiteSpace(metadata.ClientJarPath))
        {
            return VersionPaths.ResolveGamePath(paths.GameDir, metadata.ClientJarPath);
        }

        return paths.VersionJar(baseVersionId);
    }

    private static List<string> BuildClasspath(
        VersionPaths paths,
        VersionMetadata metadata,
        string clientJar,
        string? extraJar,
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

        foreach (var library in metadata.Libraries)
        {
            if (!MinecraftRuleEvaluator.IsAllowed(library.Rules) || IsNativeOnlyLibrary(library))
            {
                continue;
            }

            var path = !string.IsNullOrWhiteSpace(library.Artifact?.Path)
                ? VersionPaths.LibraryPath(paths.LibrariesDir, library.Artifact!)
                : VersionPaths.GuessLibraryPath(paths.LibrariesDir, library);

            AddFile(path);
        }

        // Произвольный Forge (не 1.20.1/47.x) использует client-<base>-extra.jar.
        if (!string.IsNullOrWhiteSpace(extraJar))
        {
            AddFile(extraJar);
        }

        AddFile(clientJar);
        return classpath;
    }

    private static string? FindForgeExtraJar(string librariesDir, string baseVersionId)
    {
        if (string.IsNullOrWhiteSpace(librariesDir) ||
            string.IsNullOrWhiteSpace(baseVersionId) ||
            !Directory.Exists(librariesDir))
        {
            return null;
        }

        var expectedName = $"client-{baseVersionId}-extra.jar";

        try
        {
            return Directory
                .EnumerateFiles(librariesDir, expectedName, SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsNativeOnlyLibrary(VersionLibrary library)
    {
        var (_, _, _, classifier) = library.ParsedName;
        if (!string.IsNullOrWhiteSpace(classifier) && IsWindowsNativesClassifier(classifier))
        {
            return true;
        }

        var artifactPath = library.Artifact?.Path;
        return !string.IsNullOrWhiteSpace(artifactPath) &&
               artifactPath.Contains("-natives-windows", StringComparison.OrdinalIgnoreCase) &&
               artifactPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsNativesClassifier(string classifier)
    {
        if (string.Equals(classifier, "natives-windows", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string prefix = "natives-windows-";
        if (!classifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = classifier[prefix.Length..].ToLowerInvariant();

        return suffix switch
        {
            "64" or "x86_64" => Environment.Is64BitOperatingSystem,
            "32" or "x86" => !Environment.Is64BitOperatingSystem,
            "arm64" => false,
            _ => false
        };
    }

    private static void AddJvmArguments(
        VersionMetadata metadata,
        IReadOnlyDictionary<string, string> variables,
        string classpath,
        List<string> arguments,
        out bool classpathAdded)
    {
        classpathAdded = false;

        foreach (var entry in metadata.JvmArguments)
        {
            if (!MinecraftRuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                if (string.Equals(value, "-cp", StringComparison.Ordinal) ||
                    string.Equals(value, "-classpath", StringComparison.Ordinal))
                {
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
    }

    private static void EnsureJavaLibraryPath(List<string> arguments, string nativesDir)
    {
        if (arguments.Any(argument =>
                argument.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        arguments.Add($"-Djava.library.path={nativesDir}");
    }

    private static void AddGameArguments(
        VersionMetadata metadata,
        IReadOnlyDictionary<string, string> variables,
        List<string> arguments,
        string versionJson)
    {
        var added = 0;

        foreach (var entry in metadata.GameArguments)
        {
            if (!MinecraftRuleEvaluator.IsAllowed(entry.Rules))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                var expanded = Expand(value, variables);
                if (!string.IsNullOrEmpty(expanded))
                {
                    arguments.Add(expanded);
                    added++;
                }
            }
        }

        if (added == 0)
        {
            throw new LauncherException(
                $"В {versionJson} нет game-аргументов — нечего передавать игре.");
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
