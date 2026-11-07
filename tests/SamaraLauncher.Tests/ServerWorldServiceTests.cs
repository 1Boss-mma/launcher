using System.IO.Compression;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.ServerHost;
using SamaraLauncher.Core.Worlds;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ServerWorldServiceTests
{
    [Fact]
    public void Inspect_ReadsLevelNameWorldsAndLastPlayed()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir, "Мой мир");

        WorldTestHelpers.CreateWorld(
            serverDir,
            "Мой мир",
            "Мой мир",
            seed: 42,
            lastPlayed: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        WorldTestHelpers.CreateWorld(serverDir, "creative", "Creative");

        var service = CreateService(temp);

        var snapshot = service.Inspect(profile);

        Assert.Null(snapshot.Error);
        Assert.Equal("Мой мир", snapshot.LevelName);
        Assert.Equal(2, snapshot.Worlds.Count);

        var active = Assert.Single(snapshot.Worlds, world => world.IsActive);
        Assert.Equal("Мой мир", active.Name);
        Assert.Equal("Мой мир", active.DisplayName);
        Assert.Equal(42, active.Seed);
        Assert.True(active.IsValid);
        Assert.True(active.Exists);
        Assert.Equal(serverDir, snapshot.ServerDir);
        Assert.Equal(
            Path.Combine(temp.Root, "localdata", "backups", "servers", "srv", "worlds"),
            snapshot.BackupsRoot);
    }

    [Fact]
    public void Inspect_MissingActiveWorld_ReturnsSyntheticRecord()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir, "lost-world");

        var snapshot = CreateService(temp).Inspect(profile);

        var active = Assert.Single(snapshot.Worlds);
        Assert.True(active.IsActive);
        Assert.False(active.Exists);
        Assert.False(active.IsValid);
        Assert.Equal("lost-world", active.Name);
    }

    [Fact]
    public void Inspect_ExcludesInfrastructureFolders()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);

        WorldTestHelpers.CreateWorld(serverDir, "world", "World");

        foreach (var folder in new[]
                 {
                     "mods", "logs", "libraries", "config", "defaultconfigs",
                     "crash-reports", "runtime", "tools", "backups"
                 })
        {
            WorldTestHelpers.WriteLevelDat(Path.Combine(serverDir, folder), folder);
        }

        var snapshot = CreateService(temp).Inspect(profile);

        var world = Assert.Single(snapshot.Worlds);
        Assert.Equal("world", world.Name);
    }

    [Fact]
    public async Task CreateWorld_WritesLevelNameAndSeedWithoutCreatingFolder()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");

        var service = CreateService(temp);

        var created = await service.CreateWorldAsync(profile, "Новый мир", "12345");

        Assert.Equal("Новый мир", created);
        Assert.False(Directory.Exists(Path.Combine(serverDir, "Новый мир")));

        var properties = ServerProperties.Load(Path.Combine(serverDir, "server.properties"));
        Assert.Equal("Новый мир", properties.Get("level-name"));
        Assert.Equal("12345", properties.Get("level-seed"));

        var safety = Assert.Single(service.ListBackups(profile));
        Assert.Equal("world", safety.WorldName);

        var manifest = WorldBackupManifestStore.TryRead(safety.Path);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.FormatVersion);
        Assert.Equal("srv", manifest.ServerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("evil/world")]
    [InlineData("evil\\world")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData("name ")]
    public async Task CreateWorld_RejectsInvalidNames(string name)
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);

        var service = CreateService(temp);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.CreateWorldAsync(profile, name));
    }

    [Fact]
    public async Task CreateWorld_RejectsSeedWithNewLine()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);

        var service = CreateService(temp);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.CreateWorldAsync(profile, "newworld", "1\n2"));
    }

    [Fact]
    public async Task CreateWorld_RejectsRunningServer()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);

        var service = CreateService(temp, isRunning: () => true);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => service.CreateWorldAsync(profile, "newworld"));

        Assert.Contains("остановите", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateWorld_ChangesLevelNameAndBacksUpPrevious()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");
        WorldTestHelpers.CreateWorld(serverDir, "creative", "Creative");

        var service = CreateService(temp);

        await service.ActivateWorldAsync(profile, "creative");

        var properties = ServerProperties.Load(Path.Combine(serverDir, "server.properties"));
        Assert.Equal("creative", properties.Get("level-name"));

        var safety = Assert.Single(service.ListBackups(profile));
        Assert.Equal("world", safety.WorldName);
    }

    [Fact]
    public async Task ActivateWorld_RejectsMissingWorld()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);

        var service = CreateService(temp);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.ActivateWorldAsync(profile, "missing"));
    }

    [Fact]
    public async Task Backup_CreatesZipWithManifestAndDimensionFolders()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        var worldDir = WorldTestHelpers.CreateWorld(serverDir, "world", "Красивый мир");

        Directory.CreateDirectory(Path.Combine(worldDir, "DIM-1"));
        WorldTestHelpers.WriteLevelDat(Path.Combine(worldDir, "DIM-1"), "Nether");
        Directory.CreateDirectory(Path.Combine(worldDir, "DIM1", "region"));
        File.WriteAllBytes(Path.Combine(worldDir, "DIM1", "region", "r.0.0.mca"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(Path.Combine(worldDir, "dimensions", "samara"));
        WorldTestHelpers.WriteLevelDat(Path.Combine(worldDir, "dimensions", "samara"), "Custom");

        var service = CreateService(temp);

        var zipPath = await service.BackupAsync(profile, "world");

        Assert.True(File.Exists(zipPath));
        Assert.StartsWith(
            Path.Combine(temp.Root, "localdata", "backups", "servers", "srv", "worlds"),
            Path.GetFullPath(zipPath),
            StringComparison.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.NotNull(archive.GetEntry("level.dat"));
        Assert.NotNull(archive.GetEntry("DIM-1/level.dat"));
        Assert.NotNull(archive.GetEntry("DIM1/region/r.0.0.mca"));
        Assert.NotNull(archive.GetEntry("dimensions/samara/level.dat"));

        var manifest = WorldBackupManifestStore.TryRead(archive);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.FormatVersion);
        Assert.Equal("world", manifest.WorldName);
        Assert.Equal("srv", manifest.ServerId);
        Assert.Equal("Красивый мир", manifest.DisplayName);
    }

    [Fact]
    public async Task Backup_AppliesRetentionPerWorld()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");
        WorldTestHelpers.CreateWorld(serverDir, "other", "Other");

        var service = CreateService(temp);

        await service.BackupAsync(profile, "other");
        await service.BackupAsync(profile, "world", retention: 1);
        await service.BackupAsync(profile, "world", retention: 1);

        var backups = service.ListBackups(profile);

        Assert.Single(backups, backup => backup.WorldName == "world");
        Assert.Single(backups, backup => backup.WorldName == "other");
    }

    [Fact]
    public async Task Backup_RejectsRunningServer()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");

        var service = CreateService(temp, isRunning: () => true);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.BackupAsync(profile, "world"));
    }

    [Fact]
    public async Task Restore_OverwritesWorldAndCreatesSafetyBackup()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        var worldDir = WorldTestHelpers.CreateWorld(serverDir, "world", "World");
        File.WriteAllText(Path.Combine(worldDir, "old.txt"), "old");

        var service = CreateService(temp);
        await service.BackupAsync(profile, "world");

        File.Delete(Path.Combine(worldDir, "old.txt"));
        File.WriteAllText(Path.Combine(worldDir, "changed.txt"), "changed");

        var backup = Assert.Single(service.ListBackups(profile));
        var restored = await service.RestoreAsync(profile, backup);

        Assert.Equal(Path.GetFullPath(worldDir), Path.GetFullPath(restored));
        Assert.True(File.Exists(Path.Combine(worldDir, "old.txt")));
        Assert.False(File.Exists(Path.Combine(worldDir, "changed.txt")));

        // Оригинальный архив + страховочный бэкап активного мира.
        Assert.True(service.ListBackups(profile).Count >= 2);
    }

    [Fact]
    public async Task Restore_RejectsRunningServer()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");

        var service = CreateService(temp);
        await service.BackupAsync(profile, "world");
        var backup = Assert.Single(service.ListBackups(profile));

        var running = CreateService(temp, isRunning: () => true);

        await Assert.ThrowsAsync<LauncherException>(
            () => running.RestoreAsync(profile, backup));
    }

    [Fact]
    public async Task DeleteWorld_MovesInactiveWorldToRemovedWorlds()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");
        var otherDir = WorldTestHelpers.CreateWorld(serverDir, "creative", "Creative");

        var service = CreateService(temp);

        Assert.True(await service.DeleteWorldAsync(profile, "creative"));
        Assert.False(Directory.Exists(otherDir));

        var removedRoot = ServerWorldService.GetServerRemovedWorldsDirectory(ServicePaths(temp), "srv");
        var removed = Assert.Single(
            Directory.EnumerateDirectories(removedRoot, "creative", SearchOption.AllDirectories));
        Assert.Equal("creative", Path.GetFileName(removed));
    }

    [Fact]
    public async Task DeleteWorld_RejectsActiveWorld()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir, "world");
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");

        var service = CreateService(temp);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => service.DeleteWorldAsync(profile, "world"));

        Assert.Contains("активный", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteWorld_RejectsRunningServer()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "World");
        WorldTestHelpers.CreateWorld(serverDir, "creative", "Creative");

        var service = CreateService(temp, isRunning: () => true);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.DeleteWorldAsync(profile, "creative"));
    }

    [Fact]
    public async Task ListBackups_ReadsManifestAndDeleteRemovesArchive()
    {
        using var temp = new TempDirectory();
        var serverDir = Path.Combine(temp.Root, "server");
        var profile = WorldTestHelpers.CreateServer(serverDir);
        WorldTestHelpers.CreateWorld(serverDir, "world", "Мир для бэкапа");

        var service = CreateService(temp);
        var zip = await service.BackupAsync(profile, "world");

        var backup = Assert.Single(service.ListBackups(profile));
        Assert.Equal(zip, backup.Path);
        Assert.Equal("world", backup.WorldName);
        Assert.Equal("Мир для бэкапа", backup.DisplayName);
        Assert.True(backup.SizeBytes > 0);
        Assert.Equal("srv", backup.ServerId);
        Assert.NotEqual(default, backup.CreatedUtc);

        Assert.True(await service.DeleteBackupAsync(profile, backup));
        Assert.Empty(service.ListBackups(profile));
    }

    private static ServerWorldService CreateService(
        TempDirectory temp,
        Func<bool>? isRunning = null)
    {
        var paths = ServicePaths(temp);
        var worlds = new WorldService(paths);
        return new ServerWorldService(paths, isRunning ?? (() => false), worlds);
    }

    private static LauncherPaths ServicePaths(TempDirectory temp) =>
        new(
            Path.Combine(temp.Root, "appdata"),
            Path.Combine(temp.Root, "localdata"));
}
