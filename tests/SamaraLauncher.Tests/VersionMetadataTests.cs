using System.Text.Json;
using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class VersionMetadataTests
{
    private const string ForgeJson = """
        {
          "id": "1.20.1-forge-47.4.28",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "inheritsFrom": "1.20.1",
          "assets": "legacy-assets",
          "assetIndex": { "id": "5" },
          "libraries": [
            {
              "name": "net.minecraftforge:forge:1.20.1-47.4.28",
              "downloads": {
                "artifact": {
                  "path": "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28.jar",
                  "sha1": "abc123",
                  "url": "https://example.invalid/forge.jar",
                  "size": 4242
                },
                "classifiers": {
                  "natives-windows": {
                    "path": "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-natives-windows.jar",
                    "sha1": "def456",
                    "url": "https://example.invalid/forge-natives.jar",
                    "size": 99
                  }
                }
              },
              "rules": [
                { "action": "allow", "os": { "name": "windows", "arch": "x86_64", "version": "^10\\." } }
              ]
            },
            { "name": "org.lwjgl:lwjgl:3.3.1" }
          ],
          "arguments": {
            "jvm": [
              "-Djava.library.path=${natives_directory}",
              {
                "rules": [ { "action": "allow", "os": { "name": "windows" } } ],
                "value": [ "-DignoreList=client.jar", "-Dfoo=bar" ]
              }
            ],
            "game": [
              "--username",
              { "rules": [], "values": [ "--demo" ] }
            ]
          }
        }
        """;

    [Fact]
    public void Parse_ReadsMetadataLibrariesAndArguments()
    {
        using var document = JsonDocument.Parse(ForgeJson);

        var version = VersionMetadata.Parse(document.RootElement);

        Assert.Equal("1.20.1-forge-47.4.28", version.Id);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", version.MainClass);
        Assert.Equal("1.20.1", version.InheritsFrom);
        Assert.Equal("legacy-assets", version.Assets);
        Assert.Equal("5", version.AssetIndexId);
        Assert.Null(version.Type);

        Assert.Equal(2, version.Libraries.Count);

        var forge = version.Libraries[0];
        Assert.Equal("net.minecraftforge:forge:1.20.1-47.4.28", forge.Name);
        Assert.NotNull(forge.Artifact);
        Assert.Equal(
            "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28.jar",
            forge.Artifact!.Path);
        Assert.Equal("abc123", forge.Artifact.Sha1);
        Assert.Equal("https://example.invalid/forge.jar", forge.Artifact.Url);
        Assert.Equal(4242, forge.Artifact.Size);
        Assert.True(forge.Classifiers.TryGetValue("natives-windows", out var natives));
        Assert.Equal(
            "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-natives-windows.jar",
            natives!.Path);
        var forgeRule = Assert.Single(forge.Rules);
        var forgeOs = Assert.IsType<RuleOs>(forgeRule.Os);
        Assert.Equal("allow", forgeRule.Action);
        Assert.Equal("windows", forgeOs.Name);
        Assert.Equal("x86_64", forgeOs.Arch);
        Assert.Equal("^10\\.", forgeOs.Version);

        var lwjgl = version.Libraries[1];
        Assert.Null(lwjgl.Artifact);
        Assert.Empty(lwjgl.Rules);

        Assert.Equal(2, version.JvmArguments.Count);
        Assert.Equal(
            new[] { "-Djava.library.path=${natives_directory}" },
            version.JvmArguments[0].Values);
        Assert.Equal(
            new[] { "-DignoreList=client.jar", "-Dfoo=bar" },
            version.JvmArguments[1].Values);
        Assert.Equal("windows", version.JvmArguments[1].Rules[0].Os!.Name);

        Assert.Equal(2, version.GameArguments.Count);
        Assert.Equal(new[] { "--username" }, version.GameArguments[0].Values);
        Assert.Equal(new[] { "--demo" }, version.GameArguments[1].Values);
        Assert.Empty(version.GameArguments[1].Rules);
    }

    [Fact]
    public void Parse_LeavesAssetIndexNullWhenAssetIndexIsMissing()
    {
        const string json = """
            { "id": "1.20.1", "assets": "legacy" }
            """;

        using var document = JsonDocument.Parse(json);

        var version = VersionMetadata.Parse(document.RootElement);

        Assert.Null(version.AssetIndexId);
        Assert.Equal("legacy", version.Assets);
    }

    [Fact]
    public void Parse_FallsBackToAssetsWhenAssetIndexHasNoId()
    {
        const string json = """
            { "id": "1.20.1", "assets": "legacy", "assetIndex": {} }
            """;

        using var document = JsonDocument.Parse(json);

        var version = VersionMetadata.Parse(document.RootElement);

        Assert.Equal("legacy", version.AssetIndexId);
    }

    [Fact]
    public void Parse_ReadsLegacyMinecraftArguments()
    {
        const string json = """
            {
              "id": "1.20.1",
              "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory}"
            }
            """;

        using var document = JsonDocument.Parse(json);

        var version = VersionMetadata.Parse(document.RootElement);

        Assert.Equal(
            new[] { "--username", "${auth_player_name}", "--gameDir", "${game_directory}" },
            version.GameArguments.SelectMany(argument => argument.Values));
    }

    [Theory]
    [InlineData("g:a:1.0", "g", "a", "1.0", null)]
    [InlineData("g:a:1.0:classifier", "g", "a", "1.0", "classifier")]
    [InlineData("a", "", "a", "", null)]
    [InlineData("", "", "", "", null)]
    public void VersionLibrary_ParsedName_HandlesOptionalClassifier(
        string name,
        string group,
        string artifact,
        string version,
        string? classifier)
    {
        var library = new VersionLibrary { Name = name };

        var parsed = library.ParsedName;

        Assert.Equal((group, artifact, version, classifier), parsed);
    }
}
