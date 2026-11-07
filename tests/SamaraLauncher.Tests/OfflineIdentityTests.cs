using SamaraLauncher.Core.Launch;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class OfflineIdentityTests
{
    // Эталон вычислен независимо: MD5("OfflinePlayer:Notch") с битами UUID v3.
    private const string NotchUuid = "b50ad385829d3141a2167e7d7539ba7f";

    [Fact]
    public void ComputeUuid_MatchesJavaOfflinePlayerAlgorithm()
    {
        Assert.Equal(NotchUuid, OfflineIdentity.ComputeUuid("Notch"));
    }

    [Fact]
    public void ComputeUuid_IsDeterministicAndUsesVersion3Variant()
    {
        var first = OfflineIdentity.ComputeUuid("Player");
        var second = OfflineIdentity.ComputeUuid("Player");

        Assert.Equal(first, second);
        Assert.Equal("a01e3843e5213998958af459800e4d11", first);
        Assert.Equal('3', first[12]);
        Assert.True("89ab".Contains(first[16]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComputeUuid_ThrowsForBlankUsername(string? username)
    {
        Assert.ThrowsAny<ArgumentException>(() => OfflineIdentity.ComputeUuid(username!));
    }

    [Theory]
    [InlineData("B50AD385-829D-3141-A216-7E7D7539BA7F", NotchUuid)]
    [InlineData(NotchUuid, NotchUuid)]
    [InlineData("ABCDEF", "abcdef")]
    public void NormalizeUuid_RemovesDashesAndLowercases(string uuid, string expected)
    {
        Assert.Equal(expected, OfflineIdentity.NormalizeUuid(uuid));
    }
}
