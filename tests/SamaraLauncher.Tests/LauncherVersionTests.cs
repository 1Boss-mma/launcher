using SamaraLauncher.Core;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LauncherVersionTests
{
    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.2.3", "1.2.3-beta", true)]
    [InlineData("1.2.3-beta", "1.2.3", false)]
    [InlineData("1.2.4-beta", "1.2.3", true)]
    [InlineData("v1.2.4", "1.2.3", true)]
    [InlineData("1.2.3+build.5", "1.2.3", false)]
    public void IsNewer_ComparesNumericPartsAndPrerelease(
        string candidate,
        string current,
        bool expected)
    {
        Assert.Equal(expected, LauncherVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void Current_IsANonEmptyVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(LauncherVersion.Current));
        Assert.Matches(@"^\d+\.\d+\.\d+", LauncherVersion.Current);
    }
}
