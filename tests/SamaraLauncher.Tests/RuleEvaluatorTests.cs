using Xunit;
using LaunchOsRule = SamaraLauncher.Core.Launch.OsRule;
using LaunchRule = SamaraLauncher.Core.Launch.Rule;
using LaunchRuleEvaluator = SamaraLauncher.Core.Launch.RuleEvaluator;
using MinecraftRule = SamaraLauncher.Core.Minecraft.Rule;
using MinecraftRuleEvaluator = SamaraLauncher.Core.Minecraft.RuleEvaluator;

namespace SamaraLauncher.Tests;

public sealed class RuleEvaluatorTests
{
    [Fact]
    public void Minecraft_AllowsWhenThereAreNoRules()
    {
        Assert.True(MinecraftRuleEvaluator.IsAllowed(Array.Empty<MinecraftRule>()));
    }

    [Fact]
    public void Minecraft_AllowsUnconditionalAllowRule()
    {
        var rules = new[] { new MinecraftRule { Action = "allow" } };

        Assert.True(MinecraftRuleEvaluator.IsAllowed(rules));
    }

    [Fact]
    public void Minecraft_LastMatchingRuleWins()
    {
        var allowThenDisallow = new[]
        {
            new MinecraftRule { Action = "allow" },
            new MinecraftRule { Action = "disallow" }
        };

        var disallowThenAllow = new[]
        {
            new MinecraftRule { Action = "disallow" },
            new MinecraftRule { Action = "allow" }
        };

        Assert.False(MinecraftRuleEvaluator.IsAllowed(allowThenDisallow));
        Assert.True(MinecraftRuleEvaluator.IsAllowed(disallowThenAllow));
    }

    [Fact]
    public void Minecraft_MatchesFeatureRulesOnlyWhenFeatureIsEnabled()
    {
        var rules = new[]
        {
            new MinecraftRule
            {
                Action = "allow",
                Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true }
            }
        };

        Assert.False(MinecraftRuleEvaluator.IsAllowed(rules));
        Assert.False(
            MinecraftRuleEvaluator.IsAllowed(
                rules,
                new Dictionary<string, bool> { ["has_custom_resolution"] = false }));
        Assert.True(
            MinecraftRuleEvaluator.IsAllowed(
                rules,
                new Dictionary<string, bool> { ["has_custom_resolution"] = true }));
    }

    [Theory]
    [InlineData(null, "windows", true)]
    [InlineData("linux", "linux", true)]
    [InlineData("linux", "windows", false)]
    [InlineData("windows", "linux", false)]
    public void Launch_RespectsExplicitOsName(string? ruleOs, string actualOs, bool expected)
    {
        var rules = new[]
        {
            new LaunchRule
            {
                Action = "allow",
                Os = ruleOs is null ? null : new LaunchOsRule { Name = ruleOs }
            }
        };

        Assert.Equal(expected, LaunchRuleEvaluator.IsAllowed(rules, actualOs));
    }

    [Fact]
    public void Launch_DisallowMatchesOnlyItsOwnOs()
    {
        var rules = new[]
        {
            new LaunchRule { Action = "allow" },
            new LaunchRule { Action = "disallow", Os = new LaunchOsRule { Name = "linux" } }
        };

        Assert.False(LaunchRuleEvaluator.IsAllowed(rules, "linux"));
        Assert.True(LaunchRuleEvaluator.IsAllowed(rules, "windows"));
    }

    [Fact]
    public void Launch_IgnoresRulesWithFeatures()
    {
        var rules = new[]
        {
            new LaunchRule
            {
                Action = "allow",
                Features = new Dictionary<string, bool> { ["demo"] = true }
            }
        };

        Assert.False(LaunchRuleEvaluator.IsAllowed(rules, "windows"));
    }

    [Fact]
    public void Launch_AllowsWhenRulesAreNull()
    {
        Assert.True(LaunchRuleEvaluator.IsAllowed(null, "windows"));
    }
}
