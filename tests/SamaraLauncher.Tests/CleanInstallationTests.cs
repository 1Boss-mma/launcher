using System.Text.Json;
using Launcher.Setup;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class CleanInstallationTests
{
    private static readonly string[] Payload = { "Launcher.exe", "tools/packwiz-installer-bootstrap.jar" };

    [Fact]
    public void Clean_RemovesMarkedPreviousInstallationsAndTheirBackups()
    {
        using var temp = new TempDirectory();
        var active = InstallAt(Path.Combine(temp.Root, "active"));
        var previous = InstallAt(Path.Combine(temp.Root, "previous"));
        var backup = InstallAt(previous + ".backup-" + Guid.NewGuid().ToString("N"));

        var retained = CleanInstallation.Clean(active, new[] { previous }, Payload);

        Assert.Empty(retained);
        Assert.False(Directory.Exists(previous));
        Assert.False(Directory.Exists(backup));
        Assert.Equal("application", File.ReadAllText(Path.Combine(active, "Launcher.exe")));
    }

    [Fact]
    public void Clean_PreservesPlayerDataAndUnknownFilesEvenIfListedInManifest()
    {
        using var temp = new TempDirectory();
        var active = InstallAt(Path.Combine(temp.Root, "active"));
        var previous = InstallAt(Path.Combine(temp.Root, "previous"));
        File.WriteAllText(Path.Combine(previous, "config.json"), "settings");
        Directory.CreateDirectory(Path.Combine(previous, "worlds"));
        File.WriteAllText(Path.Combine(previous, "worlds", "world.dat"), "world");
        File.WriteAllText(Path.Combine(previous, "personal.txt"), "personal");
        CleanInstallation.WriteManifest(previous, new[] { "./config.json", "worlds/world.dat", "Launcher.exe" });

        var retained = CleanInstallation.Clean(active, new[] { previous }, Payload);

        Assert.Contains(previous, retained);
        Assert.False(File.Exists(Path.Combine(previous, "Launcher.exe")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(previous, "config.json")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(previous, "worlds", "world.dat")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(previous, "personal.txt")));
    }

    [Fact]
    public void Clean_DoesNotTouchActiveOrUnmarkedDirectories()
    {
        using var temp = new TempDirectory();
        var active = InstallAt(Path.Combine(temp.Root, "active"));
        var unmarked = InstallAt(Path.Combine(temp.Root, "unmarked"));
        File.Delete(Path.Combine(unmarked, CleanInstallation.Marker));

        CleanInstallation.Clean(active, new[] { active, unmarked }, Payload);

        Assert.True(File.Exists(Path.Combine(active, "Launcher.exe")));
        Assert.True(File.Exists(Path.Combine(unmarked, "Launcher.exe")));
    }

    [Fact]
    public void Clean_RejectsEscapingManifestBeforeDeletingAnyFiles()
    {
        using var temp = new TempDirectory();
        var active = InstallAt(Path.Combine(temp.Root, "active"));
        var previous = InstallAt(Path.Combine(temp.Root, "previous"));
        var outside = Path.Combine(temp.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.WriteAllText(Path.Combine(previous, CleanInstallation.Manifest), JsonSerializer.Serialize(new[] { "Launcher.exe", "../outside.txt" }));

        var retained = CleanInstallation.Clean(active, new[] { previous }, Payload);

        Assert.Contains(previous, retained);
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.True(File.Exists(Path.Combine(previous, "Launcher.exe")));
    }

    [Fact]
    public void Clean_PreservesNewInstallationNestedInsidePreviousDirectory()
    {
        using var temp = new TempDirectory();
        var previous = InstallAt(Path.Combine(temp.Root, "previous"));
        var active = InstallAt(Path.Combine(previous, "new"));
        CleanInstallation.WriteManifest(previous, new[] { "Launcher.exe", "new/Launcher.exe" });

        CleanInstallation.Clean(active, new[] { previous }, Payload);

        Assert.True(File.Exists(Path.Combine(active, "Launcher.exe")));
        Assert.False(File.Exists(Path.Combine(previous, "Launcher.exe")));
    }

    private static string InstallAt(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        File.WriteAllText(Path.Combine(root, CleanInstallation.Marker), CleanInstallation.ProductId);
        File.WriteAllText(Path.Combine(root, "Launcher.exe"), "application");
        File.WriteAllText(Path.Combine(root, "tools", "packwiz-installer-bootstrap.jar"), "tool");
        return root;
    }
}
