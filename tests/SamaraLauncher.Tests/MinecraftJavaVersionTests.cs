using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class MinecraftJavaVersionTests
{
    [Theory]
    [InlineData("1.6.4", 8)]
    [InlineData("1.12.2", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17", 17)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.6", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("b1.7.3", 8)]
    [InlineData("a1.2.6", 8)]
    [InlineData("1.20.1-forge-47.4.28", 17)]
    [InlineData("24w14a", 21)]
    [InlineData("21w03a", 17)]
    public void GetFallbackMajor_MapsMinecraftVersions(string versionId, int expected)
    {
        Assert.Equal(expected, MinecraftJavaVersion.GetFallbackMajor(versionId));
    }

    [Fact]
    public void GetRequiredMajor_PrefersDeclaredJavaVersion()
    {
        Assert.Equal(21, MinecraftJavaVersion.GetRequiredMajor("1.16.5", declaredMajor: 21));
        Assert.Equal(8, MinecraftJavaVersion.GetRequiredMajor("1.20.4", declaredMajor: 8));
    }

    [Theory]
    [InlineData("1.17", 16, 17)]
    [InlineData("1.17.1", 16, 17)]
    [InlineData("1.16.5", 8, 8)]
    [InlineData("1.20.4", 21, 21)]
    [InlineData("1.17", 18, 21)]
    [InlineData("1.17", 20, 21)]
    public void GetRequiredMajor_NormalizesDeclaredMajorToSupportedRuntime(
        string versionId,
        int declaredMajor,
        int expected)
    {
        Assert.Equal(
            expected,
            MinecraftJavaVersion.GetRequiredMajor(versionId, declaredMajor));
    }

    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.20.5", 21)]
    public void GetRequiredMajor_UsesFallbackWhenDeclarationMissing(
        string versionId,
        int expected)
    {
        Assert.Equal(expected, MinecraftJavaVersion.GetRequiredMajor(versionId));
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 17)]
    [InlineData(17, 17)]
    [InlineData(18, 21)]
    [InlineData(20, 21)]
    [InlineData(21, 21)]
    public void NormalizeMajor_MapsToSupportedRuntime(int major, int expected)
    {
        Assert.Equal(expected, MinecraftJavaVersion.NormalizeMajor(major));
    }

    [Theory]
    [InlineData("1.5.2", "pre-1.6", "pre-1.6", true)]
    [InlineData("1.6.4", "legacy", "legacy", true)]
    [InlineData("1.7.10", "1.7.10", "1.7.10", false)]
    [InlineData("1.20.1", "5", "5", false)]
    [InlineData("b1.7.3", null, null, true)]
    public void IsLegacyAssets_DetectsOldVersions(
        string versionId,
        string? assets,
        string? assetIndex,
        bool expected)
    {
        Assert.Equal(expected, MinecraftVersionRules.IsLegacyAssets(versionId, assets, assetIndex));
    }
}
