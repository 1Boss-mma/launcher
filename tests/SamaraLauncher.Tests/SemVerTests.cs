using SamaraLauncher.Core;
using SamaraLauncher.Core.Channels;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "")]
    [InlineData("v1.2.3", 1, 2, 3, "", "")]
    [InlineData("0.0.0", 0, 0, 0, "", "")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1", "")]
    [InlineData("1.2.3-beta.1+sha.abc", 1, 2, 3, "beta.1", "sha.abc")]
    [InlineData("  2.10.0-rc.1+build.5  ", 2, 10, 0, "rc.1", "build.5")]
    public void TryParse_AcceptsValidVersions(
        string value,
        int major,
        int minor,
        int patch,
        string prerelease,
        string build)
    {
        Assert.True(SemVer.TryParse(value, out var version));

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
        Assert.Equal(build, version.BuildMetadata);
        Assert.Equal(prerelease.Length > 0, version.IsPrerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3-alpha_1")]
    public void TryParse_RejectsInvalidVersions(string? value)
    {
        Assert.False(SemVer.TryParse(value, out _));
    }

    [Fact]
    public void CompareTo_FollowsSemVerPrecedence()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0"
        }.Select(SemVer.Parse).ToList();

        for (var index = 0; index < ordered.Count - 1; index++)
        {
            Assert.True(
                ordered[index].CompareTo(ordered[index + 1]) < 0,
                $"{ordered[index]} должен быть меньше {ordered[index + 1]}.");
        }
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadata()
    {
        var left = SemVer.Parse("1.2.3+sha.aaa");
        var right = SemVer.Parse("1.2.3+sha.bbb");

        Assert.Equal(0, left.CompareTo(right));
    }

    [Fact]
    public void Parse_ThrowsLauncherExceptionForInvalidVersion()
    {
        var exception = Assert.Throws<LauncherException>(() => SemVer.Parse("1.2"));

        Assert.Contains("1.2", exception.Message);
    }

    [Fact]
    public void Compare_NullIsLessThanAnyVersion()
    {
        Assert.Equal(0, SemVer.Compare((string?)null, null));
        Assert.True(SemVer.Compare((string?)null, "1.0.0") < 0);
        Assert.True(SemVer.Compare("1.0.0", (string?)null) > 0);
        Assert.Equal(0, SemVer.Compare((SemVer?)null, null));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3-beta.1+sha.abc", "1.2.3-beta.1+sha.abc")]
    public void ToString_RoundTripsCanonicalForm(string value, string expected)
    {
        Assert.Equal(expected, SemVer.Parse(value).ToString());
    }
}
