using System.IO.Compression;
using System.Text;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Worlds;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class WorldServiceTests
{
    [Fact]
    public void ListFromRoot_ReadsWorldsFromArbitraryFolder()
    {
        using var temp = new TempDirectory();
        var worldsRoot = Path.Combine(temp.Root, "server");
        WorldTestHelpers.CreateWorld(worldsRoot, "world", "World");
        Directory.CreateDirectory(Path.Combine(worldsRoot, "not-a-world"));

        var service = new WorldService();

        var worlds = service.ListFromRoot(worldsRoot);

        var world = Assert.Single(worlds);
        Assert.Equal("world", world.Name);
        Assert.Equal("World", world.DisplayName);
    }

    [Fact]
    public async Task CreateBackupFromFolder_RequiresLevelDat()
    {
        using var temp = new TempDirectory();
        var worldDir = Path.Combine(temp.Root, "world");
        Directory.CreateDirectory(worldDir);

        var service = new WorldService();

        await Assert.ThrowsAsync<LauncherException>(
            () => service.CreateBackupFromFolderAsync(worldDir, temp.Root));
    }

    [Fact]
    public async Task RestoreBackupToRoot_RejectsPathTraversalEntries()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Root, "evil.zip");
        var worldsRoot = Path.Combine(temp.Root, "server");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var level = archive.CreateEntry("level.dat");
            using (var stream = level.Open())
            {
                stream.Write(new byte[] { 1, 2, 3 });
            }

            archive.CreateEntry("../evil.txt");
        }

        var service = new WorldService();

        await Assert.ThrowsAsync<LauncherException>(
            () => service.RestoreBackupToRootAsync(zipPath, worldsRoot));
        Assert.False(File.Exists(Path.Combine(temp.Root, "evil.txt")));
    }

    [Fact]
    public void DeleteBackup_RejectsPathOutsideBackupsRoot()
    {
        using var temp = new TempDirectory();
        var backupsRoot = Path.Combine(temp.Root, "backups");
        Directory.CreateDirectory(backupsRoot);

        var external = Path.Combine(temp.Root, "external.zip");
        File.WriteAllBytes(external, new byte[] { 1, 2, 3 });

        var service = new WorldService();

        Assert.Throws<LauncherException>(() => service.DeleteBackup(backupsRoot, external));
        Assert.True(File.Exists(external));

        var inside = Path.Combine(backupsRoot, "world.zip");
        File.WriteAllBytes(inside, new byte[] { 1, 2, 3 });

        Assert.True(service.DeleteBackup(backupsRoot, inside));
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public async Task ListBackups_ReadsOldManifestWithoutServerId()
    {
        using var temp = new TempDirectory();
        var backupsRoot = Path.Combine(temp.Root, "backups");
        Directory.CreateDirectory(backupsRoot);
        var zipPath = Path.Combine(backupsRoot, "old-world-20250102-030405.zip");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry(WorldService.ManifestFileName);
            using (var writer = new StreamWriter(
                       manifest.Open(),
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(
                    """{"formatVersion":1,"worldName":"old","displayName":"Old World","createdUtc":"2025-01-02T03:04:05Z","sizeBytes":3}""");
            }

            var level = archive.CreateEntry("level.dat");
            using (var stream = level.Open())
            {
                stream.Write(new byte[] { 1, 2, 3 });
            }
        }

        var service = new WorldService();

        var backup = Assert.Single(service.ListBackups(backupsRoot));
        Assert.Equal("old", backup.WorldName);
        Assert.Equal("Old World", backup.DisplayName);
        Assert.Null(backup.ServerId);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), backup.CreatedUtc);

        var worldsRoot = Path.Combine(temp.Root, "server");
        var restored = await service.RestoreBackupToRootAsync(zipPath, worldsRoot);

        Assert.Equal(Path.Combine(worldsRoot, "old"), restored);
        Assert.True(File.Exists(Path.Combine(restored, "level.dat")));
    }

    [Fact]
    public async Task OldInstanceSignatures_StillWork()
    {
        using var temp = new TempDirectory();
        var gameDir = Path.Combine(temp.Root, "game");
        var worldDir = WorldTestHelpers.CreateWorld(
            Path.Combine(gameDir, "saves"),
            "world",
            "World");

        var instance = new InstanceProfile { GameDir = gameDir };
        var service = new WorldService(ServicePaths(temp));

        var world = Assert.Single(service.List(instance));
        Assert.Equal("world", world.Name);
        Assert.NotNull(service.Get(instance, "world"));
        Assert.Equal(
            Path.GetFullPath(worldDir),
            Path.GetFullPath(Path.Combine(service.GetSavesDirectory(instance), "world")));

        var zip = await service.CreateBackupAsync(instance, "world", temp.Root);
        Assert.True(File.Exists(zip));

        Assert.True(service.Delete(instance, "world", moveToBackups: true));
        Assert.False(Directory.Exists(worldDir));

        var removed = Path.Combine(
            temp.Root,
            "localdata",
            "backups",
            "removed-worlds");
        Assert.True(Directory.Exists(removed));
        Assert.NotEmpty(Directory.EnumerateDirectories(removed, "world", SearchOption.AllDirectories));
    }

    private static LauncherPaths ServicePaths(TempDirectory temp) =>
        new(
            Path.Combine(temp.Root, "appdata"),
            Path.Combine(temp.Root, "localdata"));
}
