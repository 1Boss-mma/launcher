using System.Net;
using System.Net.Sockets;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.ServerHost;
using SamaraLauncher.Core.Tools;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ServerHostServiceTests
{
    [Fact]
    public void BuildStartArguments_UsesMemoryResponseFileAndNoGui()
    {
        var profile = new HostedServerProfile
        {
            MemoryMb = 8192,
            MinMemoryMb = 2048,
            Port = 25565
        };

        var arguments = ServerHostService.BuildStartArguments(profile);

        Assert.Equal(
            new[]
            {
                "-Xms2048M",
                "-Xmx8192M",
                "-Dfile.encoding=UTF-8",
                "@libraries/net/minecraftforge/forge/1.20.1-47.4.28/win_args.txt",
                "nogui"
            },
            arguments);
    }

    [Fact]
    public void BuildStartArguments_ClampsMemoryToAllowedRange()
    {
        var profile = new HostedServerProfile
        {
            MemoryMb = 100000,
            MinMemoryMb = 0
        };

        var arguments = ServerHostService.BuildStartArguments(profile);

        Assert.Equal("-Xms512M", arguments[0]);
        Assert.Equal("-Xmx32768M", arguments[1]);
    }

    [Fact]
    public void BuildStartArguments_MinMemoryNeverExceedsMax()
    {
        var profile = new HostedServerProfile
        {
            MemoryMb = 4096,
            MinMemoryMb = 8192
        };

        var arguments = ServerHostService.BuildStartArguments(profile);

        Assert.Equal("-Xms4096M", arguments[0]);
        Assert.Equal("-Xmx4096M", arguments[1]);
    }

    [Fact]
    public void BuildStartArguments_RejectsInvalidPort()
    {
        var profile = new HostedServerProfile { Port = 0 };

        Assert.Throws<LauncherException>(() => ServerHostService.BuildStartArguments(profile));
    }

    [Theory]
    [InlineData("192.168.1.10", 25565, "192.168.1.10:25565")]
    [InlineData("  play.example.com  ", 25566, "play.example.com:25566")]
    [InlineData("2001:db8::1", 25565, "[2001:db8::1]:25565")]
    [InlineData("[::1]", 25565, "[::1]:25565")]
    public void ServerAddressInfo_Format_HandlesIpv4AndIpv6(
        string host,
        int port,
        string expected)
    {
        Assert.Equal(expected, ServerAddressInfo.Format(host, port));
    }

    [Fact]
    public void ServerAddressInfo_PrimaryAddresses_AreFirstAndPublic()
    {
        var info = new ServerAddressInfo(
            25565,
            new[] { "192.168.1.10", "10.0.0.5" },
            "203.0.113.7");

        Assert.Equal("192.168.1.10", info.PrimaryLocal);
        Assert.Equal("203.0.113.7", info.PrimaryPublic);

        var empty = new ServerAddressInfo(25565, Array.Empty<string>(), null);

        Assert.Null(empty.PrimaryLocal);
        Assert.Null(empty.PrimaryPublic);
    }

    [Fact]
    public void FindExistingServer_FindsServerInsideGameDir()
    {
        using var temp = new TempDirectory();
        var gameDir = Path.Combine(temp.Root, "game");
        var serverDir = Path.Combine(gameDir, "server");
        CreateServerMarker(serverDir);

        var found = ServerHostService.FindExistingServer(new InstanceProfile { GameDir = gameDir });

        Assert.Equal(Path.GetFullPath(serverDir), found);
    }

    [Fact]
    public void FindExistingServer_FindsSiblingServer()
    {
        using var temp = new TempDirectory();
        var gameDir = Path.Combine(temp.Root, "game");
        Directory.CreateDirectory(gameDir);

        var serverDir = Path.Combine(temp.Root, "server");
        CreateServerMarker(serverDir);

        var found = ServerHostService.FindExistingServer(new InstanceProfile { GameDir = gameDir });

        Assert.Equal(Path.GetFullPath(serverDir), found);
    }

    [Fact]
    public void FindExistingServer_FindsServerNextToLocalPack()
    {
        using var temp = new TempDirectory();
        var gameDir = Path.Combine(temp.Root, "game");
        Directory.CreateDirectory(gameDir);

        var packDir = Path.Combine(temp.Root, "packs", "samara");
        Directory.CreateDirectory(packDir);
        var packPath = Path.Combine(packDir, "pack.toml");
        File.WriteAllText(packPath, "name = \"Samara\"");

        var serverDir = Path.Combine(temp.Root, "packs", "server");
        CreateServerMarker(serverDir);

        var found = ServerHostService.FindExistingServer(new InstanceProfile
        {
            GameDir = gameDir,
            PackUrl = packPath
        });

        Assert.Equal(Path.GetFullPath(serverDir), found);
    }

    [Fact]
    public void FindExistingServer_ReturnsNullWithoutServer()
    {
        using var temp = new TempDirectory();
        var gameDir = Path.Combine(temp.Root, "game");
        Directory.CreateDirectory(gameDir);

        var found = ServerHostService.FindExistingServer(new InstanceProfile { GameDir = gameDir });

        Assert.Null(found);
    }

    [Fact]
    public void Inspect_ReportsForgeEulaModsWorldsAndPort()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        CreateServerMarker(serverDir, withForge: true);

        File.WriteAllText(Path.Combine(serverDir, "eula.txt"), "eula=TRUE\n");
        File.WriteAllText(Path.Combine(serverDir, "mods", "a.jar"), "");
        File.WriteAllText(Path.Combine(serverDir, "mods", "b.jar"), "");
        File.WriteAllText(Path.Combine(serverDir, "mods", "readme.txt"), "");

        Directory.CreateDirectory(Path.Combine(serverDir, "world"));
        File.WriteAllText(Path.Combine(serverDir, "world", "level.dat"), "");
        Directory.CreateDirectory(Path.Combine(serverDir, "world_nether"));

        var service = CreateService(temp);
        var inspection = service.Inspect(new HostedServerProfile
        {
            ServerDir = serverDir,
            Port = GetFreePort()
        });

        Assert.True(inspection.DirectoryExists);
        Assert.True(inspection.ForgeInstalled);
        Assert.Equal("47.4.28", inspection.InstalledForgeVersion);
        Assert.True(inspection.EulaAccepted);
        Assert.Equal(2, inspection.ModCount);
        Assert.Equal(new[] { "world" }, inspection.Worlds);
        Assert.Equal("world", inspection.LevelName);
        Assert.False(inspection.PortInUse);
        Assert.Null(inspection.Error);
    }

    [Fact]
    public void Inspect_ReadsLevelNameFromServerProperties()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        CreateServerMarker(serverDir);
        File.WriteAllText(
            Path.Combine(serverDir, "server.properties"),
            "level-name=Мой сервер\nmotd=test\n");

        var service = CreateService(temp);
        var inspection = service.Inspect(new HostedServerProfile
        {
            ServerDir = serverDir,
            Port = GetFreePort()
        });

        Assert.Equal("Мой сервер", inspection.LevelName);
    }

    [Fact]
    public void Inspect_MissingDirectory_ReturnsError()
    {
        using var temp = new TempDirectory();
        var service = CreateService(temp);

        var inspection = service.Inspect(new HostedServerProfile
        {
            ServerDir = Path.Combine(temp.Root, "missing")
        });

        Assert.False(inspection.DirectoryExists);
        Assert.False(inspection.ForgeInstalled);
        Assert.False(inspection.EulaAccepted);
        Assert.NotNull(inspection.Error);
    }

    [Fact]
    public void Inspect_DetectsBusyPort()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        CreateServerMarker(serverDir);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var service = CreateService(temp);
        var inspection = service.Inspect(new HostedServerProfile
        {
            ServerDir = serverDir,
            Port = port
        });

        Assert.True(inspection.PortInUse);
    }

    [Fact]
    public void ApplySettings_UpdatesKnownKeysAndPreservesOthers()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        Directory.CreateDirectory(serverDir);

        var path = Path.Combine(serverDir, "server.properties");
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "# комментарий",
            "level-name=myworld",
            "difficulty=hard",
            "motd=old",
            "unknown=keep",
            ""
        }));

        var service = CreateService(temp);
        var profile = new HostedServerProfile
        {
            ServerDir = serverDir,
            Port = 25570,
            Motd = "Сервер 1.20.1",
            MaxPlayers = 6,
            OnlineMode = false,
            Whitelist = true
        };

        service.ApplySettings(profile);

        var text = File.ReadAllText(path);

        Assert.Contains("# комментарий", text);
        Assert.Contains("level-name=myworld", text);
        Assert.Contains("difficulty=hard", text);
        Assert.Contains("unknown=keep", text);

        var properties = ServerProperties.Load(path);

        Assert.Equal(25570, properties.GetInt("server-port"));
        Assert.Equal("Сервер 1.20.1", properties.Get("motd"));
        Assert.Equal(6, properties.GetInt("max-players"));
        Assert.False(properties.GetBool("online-mode", true));
        Assert.True(properties.GetBool("white-list"));
        Assert.Equal(12, properties.GetInt("view-distance"));
        Assert.Equal(8, properties.GetInt("simulation-distance"));
        Assert.False(properties.GetBool("enforce-whitelist", true));
        Assert.Equal("myworld", properties.Get("level-name"));
    }

    [Fact]
    public void LauncherConfig_FindHostedServer_IsCaseInsensitive()
    {
        var server = new HostedServerProfile { Id = "MyServer" };
        var config = new LauncherConfig { HostedServers = { server } };

        Assert.Same(server, config.FindHostedServer("myserver"));
        Assert.Null(config.FindHostedServer("missing"));
    }

    [Fact]
    public void InstanceStore_LoadsOldConfigWithoutHostedServerFields()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(configPath, """{"schemaVersion":1,"instances":[]}""");

        var config = new InstanceStore(configPath).Load();

        Assert.NotNull(config.HostedServers);
        Assert.Empty(config.HostedServers);
        Assert.Null(config.LastHostedServerId);
        Assert.Equal(10, config.WorldBackupRetention);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(25, 25)]
    [InlineData(5000, 1000)]
    public void InstanceStore_NormalizesWorldBackupRetention(int saved, int expected)
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(
            configPath,
            $$"""{"schemaVersion":1,"instances":[],"worldBackupRetention":{{saved}}}""");

        var config = new InstanceStore(configPath).Load();

        Assert.Equal(expected, config.WorldBackupRetention);
    }

    private static ServerHostService CreateService(TempDirectory temp)
    {
        var paths = new LauncherPaths(
            Path.Combine(temp.Root, "appdata"),
            Path.Combine(temp.Root, "localdata"));

        return new ServerHostService(
            paths,
            new JavaManager(paths),
            new ToolLocator(temp.Root));
    }

    private static void CreateServerMarker(string serverDir, bool withForge = false)
    {
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, "server.properties"), "motd=test\n");
        Directory.CreateDirectory(Path.Combine(serverDir, "mods"));

        if (!withForge)
        {
            return;
        }

        var argsDir = Path.Combine(
            serverDir,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.20.1-47.4.28");

        Directory.CreateDirectory(argsDir);
        File.WriteAllText(Path.Combine(argsDir, "win_args.txt"), "");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
