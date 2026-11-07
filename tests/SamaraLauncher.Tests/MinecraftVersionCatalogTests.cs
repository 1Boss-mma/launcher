using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class MinecraftVersionCatalogTests
{
    private const string ManifestJson = """
        {
          "latest": { "release": "1.20.4", "snapshot": "24w05a" },
          "versions": [
            {
              "id": "1.19.4",
              "type": "release",
              "url": "https://example.invalid/1.19.4.json",
              "sha1": "aaa",
              "releaseTime": "2023-03-14T12:00:00+00:00",
              "time": "2023-03-14T12:00:00+00:00"
            },
            {
              "id": "1.20.4",
              "type": "release",
              "url": "https://example.invalid/1.20.4.json",
              "sha1": "bbb",
              "releaseTime": "2023-12-07T12:00:00+00:00",
              "time": "2023-12-07T12:00:00+00:00"
            },
            {
              "id": "24w05a",
              "type": "snapshot",
              "url": "https://example.invalid/24w05a.json",
              "sha1": "ccc",
              "releaseTime": "2024-02-01T12:00:00+00:00",
              "time": "2024-02-01T12:00:00+00:00"
            },
            {
              "id": "b1.7.3",
              "type": "old_beta",
              "url": "https://example.invalid/b1.7.3.json",
              "sha1": "ddd",
              "releaseTime": "2011-07-08T12:00:00+00:00",
              "time": "2011-07-08T12:00:00+00:00"
            }
          ]
        }
        """;

    [Fact]
    public void ParseManifest_SortsNewestFirstAndReadsAllTypes()
    {
        var versions = MinecraftVersionCatalog.ParseManifest(ManifestJson);

        Assert.Equal(
            new[] { "24w05a", "1.20.4", "1.19.4", "b1.7.3" },
            versions.Select(version => version.Id));

        Assert.Equal("snapshot", versions[0].Type);
        Assert.True(versions[0].IsSnapshot);
        Assert.True(versions[1].IsRelease);
        Assert.Equal("https://example.invalid/1.20.4.json", versions[1].Url);
        Assert.Equal("bbb", versions[1].Sha1);
        Assert.Equal(
            new DateTimeOffset(2023, 12, 7, 12, 0, 0, TimeSpan.Zero),
            versions[1].ReleaseTime);
    }

    [Fact]
    public void ParseManifest_SortsByIdWhenDatesAreMissing()
    {
        const string json = """
            {
              "versions": [
                { "id": "1.2", "type": "release", "url": "u2" },
                { "id": "1.10", "type": "release", "url": "u10" },
                { "id": "1.1", "type": "release", "url": "u1" }
              ]
            }
            """;

        var versions = MinecraftVersionCatalog.ParseManifest(json);

        Assert.Equal(new[] { "1.10", "1.2", "1.1" }, versions.Select(version => version.Id));
    }

    [Fact]
    public void Cache_SerializeAndParse_RoundTripsVersionsAndTimestamp()
    {
        var fetchedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var versions = new[]
        {
            new MinecraftCatalogVersion(
                "1.20.4",
                "release",
                "https://example.invalid/1.20.4.json",
                "bbb",
                new DateTimeOffset(2023, 12, 7, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2023, 12, 7, 12, 0, 0, TimeSpan.Zero))
        };

        var json = MinecraftVersionCatalog.SerializeCache(fetchedAt, versions);
        var cache = MinecraftVersionCatalog.ParseCache(json);

        Assert.NotNull(cache);
        Assert.Equal(fetchedAt, cache!.FetchedAt);
        var version = Assert.Single(cache.Versions);
        Assert.Equal("1.20.4", version.Id);
        Assert.Equal("bbb", version.Sha1);
    }

    [Fact]
    public void Cache_FileRoundTrip_UsesCacheDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new LauncherPaths(
            Path.Combine(temp.Root, "app"),
            Path.Combine(temp.Root, "local"));
        var catalog = new MinecraftVersionCatalog(paths);
        var fetchedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var versions = new[]
        {
            new MinecraftCatalogVersion(
                "1.21",
                "release",
                "https://example.invalid/1.21.json",
                "sha",
                new DateTimeOffset(2024, 6, 13, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 6, 13, 12, 0, 0, TimeSpan.Zero))
        };

        catalog.SaveCache(fetchedAt, versions);
        var loaded = catalog.TryLoadCache();

        Assert.Equal(
            Path.Combine(paths.CacheDir, MinecraftVersionCatalog.CacheFileName),
            catalog.CachePath);
        Assert.NotNull(loaded);
        Assert.Equal(fetchedAt, loaded!.FetchedAt);
        Assert.Equal("1.21", Assert.Single(loaded.Versions).Id);
    }

    [Fact]
    public void ParseCache_ReturnsNullForBrokenJson()
    {
        Assert.Null(MinecraftVersionCatalog.ParseCache("not json"));
        Assert.Null(MinecraftVersionCatalog.ParseCache("{\"versions\":[]}"));
    }
}
