using SamaraLauncher.Core.Models;
using System.Text.Json;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LauncherConfigTests
{
    [Theory]
    [InlineData("http://old-test-server:8080/launcher/latest.json")]
    [InlineData("")]
    public void LegacyUpdateUrl_IsIgnoredAndNotSaved(string legacyUrl)
    {
        var json = JsonSerializer.Serialize(new { LauncherUpdateUrl = legacyUrl, CheckLauncherUpdates = true });
        var config = JsonSerializer.Deserialize<LauncherConfig>(json)!;

        Assert.True(config.CheckLauncherUpdates);
        Assert.DoesNotContain("LauncherUpdateUrl", JsonSerializer.Serialize(config));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var config = new LauncherConfig
        {
            Instances =
            {
                new InstanceProfile { Id = "alpha", Name = "Alpha" },
                new InstanceProfile { Id = "beta", Name = "Beta" }
            }
        };

        Assert.Same(config.Instances[0], config.Find("ALPHA"));
        Assert.Same(config.Instances[1], config.Find("beta"));
        Assert.Null(config.Find("missing"));
    }

    [Fact]
    public void Remove_RemovesOnlyMatchingInstance()
    {
        var alpha = new InstanceProfile { Id = "alpha", Name = "Alpha" };
        var beta = new InstanceProfile { Id = "beta", Name = "Beta" };
        var config = new LauncherConfig { Instances = { alpha, beta } };

        Assert.False(config.Remove("missing"));
        Assert.True(config.Remove("ALPHA"));

        Assert.DoesNotContain(alpha, config.Instances);
        Assert.Same(beta, Assert.Single(config.Instances));
    }
}
