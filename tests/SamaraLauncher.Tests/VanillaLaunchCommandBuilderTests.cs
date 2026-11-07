using SamaraLauncher.Core.Launch;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class VanillaLaunchCommandBuilderTests
{
    private const string ModernJson = """
        {
          "id": "1.20.1",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "5",
          "assetIndex": { "id": "5" },
          "javaVersion": { "majorVersion": 17 },
          "downloads": {
            "client": { "path": "versions/1.20.1/1.20.1.jar" }
          },
          "libraries": [
            {
              "name": "org.example:demo:1.0",
              "downloads": {
                "artifact": {
                  "path": "org/example/demo/1.0/demo-1.0.jar",
                  "url": "https://example.invalid/demo.jar",
                  "sha1": "aa",
                  "size": 3
                }
              }
            }
          ],
          "arguments": {
            "jvm": [
              "-Djava.library.path=${natives_directory}",
              "-cp",
              "${classpath}"
            ],
            "game": [
              "--username", "${auth_player_name}",
              "--gameDir", "${game_directory}",
              "--assetsDir", "${assets_root}",
              "--assetIndex", "${assets_index_name}",
              "--uuid", "${auth_uuid}",
              "--accessToken", "${auth_access_token}",
              "--userType", "${user_type}",
              "--versionType", "${version_type}",
              "--version", "${version_name}"
            ]
          }
        }
        """;

    private const string LegacyJson = """
        {
          "id": "b1.7.3",
          "type": "old_beta",
          "mainClass": "net.minecraft.client.Minecraft",
          "assets": "pre-1.6",
          "assetIndex": {
            "id": "pre-1.6",
            "url": "https://example.invalid/pre-1.6.json",
            "sha1": "",
            "size": 0
          },
          "downloads": {
            "client": {
              "url": "https://example.invalid/b1.7.3.jar",
              "sha1": "",
              "size": 1
            }
          },
          "libraries": [
            {
              "name": "org.lwjgl:lwjgl:2.9.4",
              "url": "https://libraries.minecraft.net/"
            }
          ],
          "minecraftArguments": "--username ${auth_player_name} --session ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}"
        }
        """;

    [Fact]
    public void Build_ModernArguments_ExpandsVariablesAndBuildsClasspath()
    {
        using var temp = new TempDirectory();
        var paths = PrepareVersion(temp.Root, "1.20.1", ModernJson);
        var libPath = CreateLibrary(paths, "org/example/demo/1.0/demo-1.0.jar");

        var instance = new InstanceProfile
        {
            GameDir = temp.Root,
            VersionId = "1.20.1",
            MinecraftVersion = "1.20.1",
            ForgeVersion = "",
            Username = "Steve"
        };

        var plan = new VanillaLaunchCommandBuilder().Build(instance, @"C:\Java\bin\java.exe");

        Assert.Equal("net.minecraft.client.main.Main", plan.MainClass);
        Assert.Contains(libPath, plan.Classpath);
        Assert.Contains(paths.VersionJar("1.20.1"), plan.Classpath);
        Assert.Contains(
            $"-Djava.library.path={paths.NativesDir("1.20.1")}",
            plan.Arguments);

        var arguments = plan.Arguments.ToList();
        var classpathIndex = arguments.IndexOf("-cp");
        Assert.True(classpathIndex >= 0);
        Assert.Equal(plan.Classpath, arguments[classpathIndex + 1]);

        Assert.Contains("Steve", arguments);
        Assert.Contains(temp.Root, arguments);
        Assert.Contains("5", arguments);
        Assert.DoesNotContain("${", string.Join('|', arguments));
    }

    [Fact]
    public void Build_LegacyArguments_UsesVirtualAssetsAndSessionVariables()
    {
        using var temp = new TempDirectory();
        var paths = PrepareVersion(temp.Root, "b1.7.3", LegacyJson);
        var libPath = CreateLibrary(paths, "org/lwjgl/lwjgl/2.9.4/lwjgl-2.9.4.jar");

        var instance = new InstanceProfile
        {
            GameDir = temp.Root,
            VersionId = "b1.7.3",
            MinecraftVersion = "b1.7.3",
            ForgeVersion = "",
            Username = "Alex"
        };

        var plan = new VanillaLaunchCommandBuilder().Build(instance, @"C:\Java\bin\java.exe");

        var arguments = plan.Arguments.ToList();
        var assetsDirIndex = arguments.IndexOf("--assetsDir");
        Assert.True(assetsDirIndex >= 0);
        Assert.Equal(
            Path.Combine(temp.Root, "assets", "virtual", "pre-1.6"),
            arguments[assetsDirIndex + 1]);

        var sessionIndex = arguments.IndexOf("--session");
        Assert.True(sessionIndex >= 0);
        Assert.Equal("0", arguments[sessionIndex + 1]);

        Assert.Contains(libPath, plan.Classpath);
        Assert.Contains(paths.VersionJar("b1.7.3"), plan.Classpath);
        Assert.Contains(
            $"-Djava.library.path={paths.NativesDir("b1.7.3")}",
            arguments);
    }

    [Fact]
    public void GetRequiredJavaMajor_UsesVersionJsonDeclaration()
    {
        using var temp = new TempDirectory();
        PrepareVersion(temp.Root, "1.20.1", ModernJson);

        var instance = new InstanceProfile
        {
            GameDir = temp.Root,
            VersionId = "1.20.1",
            MinecraftVersion = "1.20.1",
            ForgeVersion = ""
        };

        Assert.Equal(17, VanillaLaunchCommandBuilder.GetRequiredJavaMajor(instance));
    }

    [Fact]
    public void GetRequiredJavaMajor_MissingGameDir_FallsBackToProfileVersion()
    {
        var instance = new InstanceProfile
        {
            GameDir = "",
            VersionId = "1.21.1",
            MinecraftVersion = "",
            ForgeVersion = ""
        };

        Assert.Equal(21, VanillaLaunchCommandBuilder.GetRequiredJavaMajor(instance));
    }

    [Fact]
    public void ResolveVersionId_ReturnsForgeOrFabricChildId()
    {
        var forge = new InstanceProfile
        {
            VersionId = "1.20.1-forge-47.4.5",
            MinecraftVersion = "1.20.1"
        };

        var fabric = new InstanceProfile
        {
            VersionId = "fabric-loader-0.16.10-1.21.1",
            MinecraftVersion = "1.21.1"
        };

        Assert.Equal("1.20.1-forge-47.4.5", VanillaLaunchCommandBuilder.ResolveVersionId(forge));
        Assert.Equal(
            "fabric-loader-0.16.10-1.21.1",
            VanillaLaunchCommandBuilder.ResolveVersionId(fabric));
    }

    [Fact]
    public void Build_InheritedForgeVersion_MergesParentAndChildAndAddsExtraJar()
    {
        using var temp = new TempDirectory();
        var paths = PrepareVersion(temp.Root, "1.20.1", ModernJson);
        PrepareVersion(
            temp.Root,
            "1.20.1-forge-47.4.5",
            """
            {
              "id": "1.20.1-forge-47.4.5",
              "inheritsFrom": "1.20.1",
              "type": "release",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "libraries": [
                {
                  "name": "org.example:forge:1.0",
                  "downloads": {
                    "artifact": {
                      "path": "org/example/forge/1.0/forge-1.0.jar",
                      "url": "https://example.invalid/forge.jar",
                      "sha1": "bb",
                      "size": 3
                    }
                  }
                }
              ],
              "arguments": {
                "jvm": [ "-Dchild=true" ],
                "game": [ "--launchTarget", "forgeclient" ]
              }
            }
            """);

        var parentLibrary = CreateLibrary(paths, "org/example/demo/1.0/demo-1.0.jar");
        var forgeLibrary = CreateLibrary(paths, "org/example/forge/1.0/forge-1.0.jar");
        var extraJar = CreateLibrary(
            paths,
            "net/minecraft/client/1.20.1-20230612.114412/client-1.20.1-extra.jar");

        var instance = new InstanceProfile
        {
            GameDir = temp.Root,
            VersionId = "1.20.1-forge-47.4.5",
            MinecraftVersion = "",
            ForgeVersion = "47.4.5",
            Loader = "forge",
            Username = "Steve"
        };

        var plan = new VanillaLaunchCommandBuilder().Build(instance, @"C:\Java\bin\java.exe");

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", plan.MainClass);
        Assert.Contains(parentLibrary, plan.Classpath);
        Assert.Contains(forgeLibrary, plan.Classpath);
        Assert.Contains(extraJar, plan.Classpath);
        Assert.Contains(paths.VersionJar("1.20.1"), plan.Classpath);
        Assert.Contains(
            $"-Djava.library.path={paths.NativesDir("1.20.1-forge-47.4.5")}",
            plan.Arguments);
        Assert.Contains("--launchTarget", plan.Arguments);
        Assert.Contains("forgeclient", plan.Arguments);

        Assert.Equal(17, VanillaLaunchCommandBuilder.GetRequiredJavaMajor(instance));
    }

    private static VersionPaths PrepareVersion(string gameDir, string versionId, string json)
    {
        var paths = new VersionPaths(gameDir);
        Directory.CreateDirectory(paths.VersionDir(versionId));
        File.WriteAllText(paths.VersionJson(versionId), json);
        File.WriteAllText(paths.VersionJar(versionId), "client");
        return paths;
    }

    private static string CreateLibrary(VersionPaths paths, string relativePath)
    {
        var path = VersionPaths.ResolveLibraryPath(paths.LibrariesDir, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, "library");
        return path;
    }
}
