using SamaraLauncher.Core.Launch;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LaunchPlanBuilderTests
{
    [Theory]
    [InlineData(
        "net.minecraftforge:forge:1.20.1-47.4.28",
        "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28.jar")]
    [InlineData(
        "org.lwjgl:lwjgl:3.3.1:windows",
        "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-windows.jar")]
    [InlineData(
        "group:artifact:1.0:natives-windows@zip",
        "group/artifact/1.0/artifact-1.0-natives-windows.zip")]
    [InlineData(
        "group:artifact:1.0@zip",
        "group/artifact/1.0/artifact-1.0.zip")]
    [InlineData(
        "group:artifact:1.0:classifier@zip",
        "group/artifact/1.0/artifact-1.0-classifier.zip")]
    public void RelativePathFromName_BuildsMavenRepositoryPath(string name, string expected)
    {
        Assert.Equal(expected, LaunchPlanBuilder.RelativePathFromName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("group:artifact")]
    [InlineData("artifact")]
    public void RelativePathFromName_ReturnsEmptyForInvalidName(string? name)
    {
        Assert.Equal("", LaunchPlanBuilder.RelativePathFromName(name));
    }
}
