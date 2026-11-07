using SamaraLauncher.Core.Models;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class OfflineUuidTests
{
    [Fact]
    public void FromUsername_MatchesJavaOfflinePlayerAlgorithm()
    {
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", OfflineUuid.FromUsername("Notch"));
        Assert.Equal("36532b5ec4423dbba24cc7e55d0f979a", OfflineUuid.FromUsername("Alex"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("A01E3843-E521-3998-958A-F459800E4D11", "a01e3843e5213998958af459800e4d11")]
    public void Normalize_RemovesDashesAndLowercases(string? uuid, string expected)
    {
        Assert.Equal(expected, OfflineUuid.Normalize(uuid));
    }

    [Fact]
    public void EffectiveUuid_ComputesFromUsernameWhenUuidIsMissing()
    {
        var profile = new InstanceProfile { Username = "Player", Uuid = null };

        Assert.Equal(OfflineUuid.FromUsername("Player"), profile.EffectiveUuid);
    }

    [Fact]
    public void EffectiveUuid_NormalizesConfiguredUuid()
    {
        var profile = new InstanceProfile
        {
            Username = "Player",
            Uuid = "A01E3843-E521-3998-958A-F459800E4D11"
        };

        Assert.Equal("a01e3843e5213998958af459800e4d11", profile.EffectiveUuid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromUsername_ThrowsForBlankUsername(string? username)
    {
        Assert.ThrowsAny<ArgumentException>(() => OfflineUuid.FromUsername(username!));
    }
}
