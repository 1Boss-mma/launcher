using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class VersionPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "samara-path-tests");

    [Fact]
    public void LibraryPath_UsesArtifactRelativePath()
    {
        var artifact = new DownloadArtifact
        {
            Path = "net/minecraft/client/client-1.20.1.jar"
        };

        var path = VersionPaths.LibraryPath(Root, artifact);

        Assert.Equal(
            Path.Combine(Root, "net", "minecraft", "client", "client-1.20.1.jar"),
            path);
    }

    [Fact]
    public void GuessLibraryPath_PrefersArtifact()
    {
        var library = new VersionLibrary
        {
            Name = "net.minecraft:client:1.20.1",
            Artifact = new DownloadArtifact { Path = "net/minecraft/client/1.20.1/client.jar" }
        };

        Assert.Equal(
            Path.Combine(Root, "net", "minecraft", "client", "1.20.1", "client.jar"),
            VersionPaths.GuessLibraryPath(Root, library));
    }

    [Fact]
    public void GuessLibraryPath_UsesRequestedClassifierArtifact()
    {
        var library = new VersionLibrary
        {
            Name = "org.lwjgl:lwjgl:3.3.1",
            Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar" }
        };
        library.Classifiers["natives-windows"] = new DownloadArtifact
        {
            Path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar"
        };

        Assert.Equal(
            Path.Combine(Root, "org", "lwjgl", "lwjgl", "3.3.1", "lwjgl-3.3.1-natives-windows.jar"),
            VersionPaths.GuessLibraryPath(Root, library, "natives-windows"));
    }

    [Theory]
    [InlineData("org.example:demo:2.0", "demo-2.0.jar")]
    [InlineData("org.example:demo:2.0:linux", "demo-2.0-linux.jar")]
    public void GuessLibraryPath_FallsBackToMavenName(string name, string fileName)
    {
        var library = new VersionLibrary { Name = name };

        Assert.Equal(
            Path.Combine(Root, "org", "example", "demo", "2.0", fileName),
            VersionPaths.GuessLibraryPath(Root, library));
    }

    [Fact]
    public void VersionHelpers_ReturnPathsInsideGameDirectory()
    {
        var paths = new VersionPaths(Root);

        Assert.Equal(Path.Combine(Root, "versions", "1.20.1", "1.20.1.json"), paths.VersionJson("1.20.1"));
        Assert.Equal(Path.Combine(Root, "versions", "1.20.1", "1.20.1.jar"), paths.VersionJar("1.20.1"));
        Assert.Equal(Path.Combine(Root, "versions", "1.20.1", "natives"), paths.NativesDir("1.20.1"));
        Assert.Equal(Path.Combine(Root, "mods"), paths.ModsDir);
        Assert.Equal(Path.Combine(Root, "assets", "indexes"), paths.AssetsIndexesDir);
    }
}
