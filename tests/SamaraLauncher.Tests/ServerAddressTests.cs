using SamaraLauncher.Core.Servers;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("mc.example.com", 25565, "mc.example.com", 25565)]
    [InlineData("  play.example.com:25570  ", 25565, "play.example.com", 25570)]
    [InlineData("host:1234", 25565, "host", 1234)]
    [InlineData("[::1]:25565", 25565, "::1", 25565)]
    [InlineData("[2001:db8::1]", 25565, "2001:db8::1", 25565)]
    [InlineData("2001:db8::1", 25565, "2001:db8::1", 25565)]
    public void TryParse_AcceptsValidAddresses(
        string value,
        int defaultPort,
        string expectedHost,
        int expectedPort)
    {
        Assert.True(ServerAddress.TryParse(value, defaultPort, out var host, out var port));

        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:abc")]
    [InlineData(":25565")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("[]")]
    public void TryParse_RejectsInvalidAddresses(string? value)
    {
        Assert.False(ServerAddress.TryParse(value, 25565, out _, out _));
    }

    [Theory]
    [InlineData("mc.example.com", 25565, "mc.example.com")]
    [InlineData("mc.example.com", 25566, "mc.example.com:25566")]
    [InlineData("  host  ", 25565, "host")]
    [InlineData("::1", 25565, "[::1]")]
    [InlineData("::1", 25570, "[::1]:25570")]
    [InlineData("[::1]", 25565, "[::1]")]
    public void Format_HandlesDefaultPortAndIpv6(string host, int port, string expected)
    {
        Assert.Equal(expected, ServerAddress.Format(host, port));
    }

    [Fact]
    public void ServerEntry_Address_OmitsDefaultPort()
    {
        var entry = new ServerEntry { Host = "mc.example.com" };

        Assert.Equal("mc.example.com", entry.Address);

        entry.Port = 25566;

        Assert.Equal("mc.example.com:25566", entry.Address);
    }
}
