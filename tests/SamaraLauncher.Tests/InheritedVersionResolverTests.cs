using SamaraLauncher.Core;
using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class InheritedVersionResolverTests
{
    private const string ParentJson = """
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
            { "name": "org.example:parent:1.0" },
            { "name": "org.example:shared:1.0" }
          ],
          "arguments": {
            "jvm": [ "-Dparent=true" ],
            "game": [ "--parent" ]
          }
        }
        """;

    private const string ChildJson = """
        {
          "id": "1.20.1-forge-47.4.5",
          "inheritsFrom": "1.20.1",
          "type": "release",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "javaVersion": { "majorVersion": 21 },
          "libraries": [
            { "name": "org.example:child:1.0" },
            { "name": "org.example:shared:2.0" }
          ],
          "arguments": {
            "jvm": [ "-Dchild=true" ],
            "game": [ "--child" ]
          }
        }
        """;

    [Fact]
    public void Load_MergesParentAndChild()
    {
        using var temp = new TempDirectory();
        WriteVersion(temp.Root, "1.20.1", ParentJson);
        WriteVersion(temp.Root, "1.20.1-forge-47.4.5", ChildJson);

        var resolved = InheritedVersionResolver.Load(temp.Root, "1.20.1-forge-47.4.5");

        Assert.Equal("1.20.1", resolved.BaseVersionId);
        Assert.Equal("1.20.1-forge-47.4.5", resolved.VersionId);
        Assert.Equal("1.20.1-forge-47.4.5", resolved.Metadata.Id);
        Assert.Null(resolved.Metadata.InheritsFrom);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", resolved.Metadata.MainClass);
        Assert.Equal("5", resolved.Metadata.Assets);
        Assert.Equal("5", resolved.Metadata.AssetIndexId);
        Assert.Equal("release", resolved.Metadata.Type);
        Assert.Equal(21, resolved.Metadata.JavaMajorVersion!.Value);
        Assert.Equal("versions/1.20.1/1.20.1.jar", resolved.Metadata.ClientJarPath);

        Assert.Equal(
            new[]
            {
                "org.example:parent:1.0",
                "org.example:child:1.0",
                "org.example:shared:2.0"
            },
            resolved.Metadata.Libraries.Select(library => library.Name));

        Assert.Equal(
            new[] { "-Dparent=true", "-Dchild=true" },
            resolved.Metadata.JvmArguments.SelectMany(argument => argument.Values));

        Assert.Equal(
            new[] { "--parent", "--child" },
            resolved.Metadata.GameArguments.SelectMany(argument => argument.Values));
    }

    [Fact]
    public void Load_DoesNotMutateSourceVersionJson()
    {
        using var temp = new TempDirectory();
        var paths = new VersionPaths(temp.Root);
        WriteVersion(temp.Root, "1.20.1", ParentJson);
        WriteVersion(temp.Root, "1.20.1-forge-47.4.5", ChildJson);

        _ = InheritedVersionResolver.Load(temp.Root, "1.20.1-forge-47.4.5");

        var parent = VersionMetadata.Load(paths.VersionJson("1.20.1"));
        var child = VersionMetadata.Load(paths.VersionJson("1.20.1-forge-47.4.5"));

        Assert.Equal("1.20.1", parent.Id);
        Assert.Equal(2, parent.Libraries.Count);
        Assert.Equal("1.20.1-forge-47.4.5", child.Id);
        Assert.Equal("1.20.1", child.InheritsFrom);
        Assert.Equal(2, child.Libraries.Count);
    }

    [Fact]
    public void Load_MissingParent_ThrowsClearError()
    {
        using var temp = new TempDirectory();
        WriteVersion(temp.Root, "fabric-loader-0.16.10-1.21.1", """
            {
              "id": "fabric-loader-0.16.10-1.21.1",
              "inheritsFrom": "1.21.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient"
            }
            """);

        var exception = Assert.Throws<LauncherException>(() =>
            InheritedVersionResolver.Load(temp.Root, "fabric-loader-0.16.10-1.21.1"));

        Assert.Contains("родительской версии 1.21.1", exception.Message);
    }

    [Fact]
    public void Load_Cycle_ThrowsClearError()
    {
        using var temp = new TempDirectory();
        WriteVersion(temp.Root, "version-a", """
            {
              "id": "version-a",
              "inheritsFrom": "version-b",
              "mainClass": "example.A"
            }
            """);
        WriteVersion(temp.Root, "version-b", """
            {
              "id": "version-b",
              "inheritsFrom": "version-a",
              "mainClass": "example.B"
            }
            """);

        var exception = Assert.Throws<LauncherException>(() =>
            InheritedVersionResolver.Load(temp.Root, "version-a"));

        Assert.Contains("Циклическая ссылка", exception.Message);
    }

    private static void WriteVersion(string gameDir, string versionId, string json)
    {
        var paths = new VersionPaths(gameDir);
        Directory.CreateDirectory(paths.VersionDir(versionId));
        File.WriteAllText(paths.VersionJson(versionId), json);
    }
}
