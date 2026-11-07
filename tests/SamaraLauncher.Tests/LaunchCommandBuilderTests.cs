using SamaraLauncher.Core.Launch;
using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LaunchCommandBuilderTests
{
    [Fact]
    public void SelectGameArguments_SelfContainedForge_UsesOnlyForgeArguments()
    {
        var vanilla = Version(null, "--username", "${auth_player_name}", "--gameDir", "${game_directory}");
        var forge = Version(null, "--launchTarget", "forgeclient", "--gameDir", "${game_directory}");

        var selected = LaunchCommandBuilder.SelectGameArguments(forge, vanilla);

        Assert.Equal(
            new[] { "--launchTarget", "forgeclient", "--gameDir", "${game_directory}" },
            Flatten(selected));
    }

    [Fact]
    public void SelectGameArguments_InheritingForge_AppendsForgeArgumentsToVanilla()
    {
        var vanilla = Version(null, "--username", "${auth_player_name}");
        var forge = Version("1.20.1", "--launchTarget", "forgeclient");

        var selected = LaunchCommandBuilder.SelectGameArguments(forge, vanilla);

        Assert.Equal(
            new[] { "--username", "${auth_player_name}", "--launchTarget", "forgeclient" },
            Flatten(selected));
    }

    [Fact]
    public void SelectGameArguments_ForgeWithoutGameArguments_UsesVanillaArguments()
    {
        var vanilla = Version(null, "--username", "${auth_player_name}", "--gameDir", "${game_directory}");
        var forge = new VersionMetadata { InheritsFrom = "1.20.1" };

        var selected = LaunchCommandBuilder.SelectGameArguments(forge, vanilla);

        Assert.Equal(
            new[] { "--username", "${auth_player_name}", "--gameDir", "${game_directory}" },
            Flatten(selected));
    }

    [Fact]
    public void SelectGameArguments_SelfContainedForge_KeepsSingleGameDir()
    {
        var vanilla = Version(null, "--gameDir", "${game_directory}", "--username", "${auth_player_name}");
        var forge = Version(null, "--gameDir", "${game_directory}", "--launchTarget", "forgeclient");

        var values = Flatten(LaunchCommandBuilder.SelectGameArguments(forge, vanilla));

        Assert.Equal(1, values.Count(value => string.Equals(value, "--gameDir", StringComparison.Ordinal)));
    }

    private static VersionMetadata Version(string? inheritsFrom, params string[] values)
    {
        var version = new VersionMetadata { InheritsFrom = inheritsFrom };
        version.GameArguments.Add(new VersionArgument { Values = values.ToList() });
        return version;
    }

    private static List<string> Flatten(IEnumerable<VersionArgument> arguments) =>
        arguments.SelectMany(argument => argument.Values).ToList();
}
