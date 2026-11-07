using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ModUpdateServiceTests
{
    [Fact]
    public async Task CheckUpdatesAsync_FindsNewerCompatibleVersionFromProjectVersions()
    {
        using var temp = new TempDirectory();
        var modsDir = Path.Combine(temp.Root, "mods");
        var jarPath = Path.Combine(modsDir, "testmod.jar");
        var installedHash = CreateJar(
            jarPath,
            "fabric.mod.json",
            """
            { "id": "testmod", "name": "Test Mod", "version": "1.0.0" }
            """);

        var latestHash = new string('b', 128);
        var hashResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [installedHash] = Version(
                "1.0.0",
                "https://cdn.test/testmod-1.0.0.jar",
                installedHash,
                "forge",
                "1.20.1")
        });

        var versionsResponse = JsonSerializer.Serialize(new object[]
        {
            // Более новая версия, но для другой версии Minecraft — не подходит.
            Version(
                "2.0.0",
                "https://cdn.test/testmod-2.0.0.jar",
                new string('e', 128),
                "forge",
                "1.19.2"),
            Version(
                "1.1.0",
                "https://cdn.test/testmod-1.1.0.jar",
                latestHash,
                "forge",
                "1.20.1"),
            Version(
                "1.0.0",
                "https://cdn.test/testmod-1.0.0.jar",
                installedHash,
                "forge",
                "1.20.1")
        });

        var requests = new ConcurrentQueue<string>();
        using var client = new HttpClient(new FakeHandler(request =>
        {
            requests.Enqueue($"{request.Method.Method} {request.RequestUri?.AbsolutePath}");
            return RouteModrinth(request, hashResponse, versionsResponse);
        }));

        var service = new ModUpdateService(new ModrinthClient(client), downloadClient: client);

        var updates = await service.CheckUpdatesAsync(CreateInstance(temp.Root));

        var update = Assert.Single(updates);
        Assert.Equal("testmod.jar", update.FileName);
        Assert.Equal("testmod", update.ModId);
        Assert.Equal("1.0.0", update.CurrentVersion);
        Assert.Equal("1.1.0", update.LatestVersion);
        Assert.Equal("https://cdn.test/testmod-1.1.0.jar", update.DownloadUrl);
        Assert.Equal(latestHash, update.Sha512);
        Assert.Equal("proj-test", update.ProjectId);
        Assert.Contains("POST /v2/version_files", requests);
        Assert.Contains("GET /v2/project/proj-test/version", requests);
    }

    [Fact]
    public async Task CheckUpdatesAsync_DoesNotReportWhenInstalledVersionIsNewest()
    {
        using var temp = new TempDirectory();
        var modsDir = Path.Combine(temp.Root, "mods");
        var installedHash = CreateJar(
            Path.Combine(modsDir, "testmod.jar"),
            "fabric.mod.json",
            """
            { "id": "testmod", "name": "Test Mod", "version": "2.0.0" }
            """);

        var hashResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [installedHash] = Version(
                "2.0.0",
                "https://cdn.test/testmod-2.0.0.jar",
                installedHash,
                "forge",
                "1.20.1")
        });

        var versionsResponse = JsonSerializer.Serialize(new object[]
        {
            Version(
                "2.0.0",
                "https://cdn.test/testmod-2.0.0.jar",
                installedHash,
                "forge",
                "1.20.1")
        });

        using var client = new HttpClient(new FakeHandler(
            request => RouteModrinth(request, hashResponse, versionsResponse)));
        var service = new ModUpdateService(new ModrinthClient(client), downloadClient: client);

        var updates = await service.CheckUpdatesAsync(CreateInstance(temp.Root));

        Assert.Empty(updates);
    }

    [Fact]
    public async Task CheckUpdatesAsync_TreatsCommonSuffixesAsSameVersion()
    {
        using var temp = new TempDirectory();
        var modsDir = Path.Combine(temp.Root, "mods");
        var installedHash = CreateJar(
            Path.Combine(modsDir, "testmod.jar"),
            "META-INF/mods.toml",
            """
            modLoader="javafml"

            [[mods]]
            modId="testmod"
            version="v1.2.3"
            displayName="Test Mod"
            """);

        var hashResponse = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [installedHash] = VersionWithId(
                "version-installed",
                "1.2.3-forge",
                "https://cdn.test/testmod-1.2.3-forge.jar",
                installedHash,
                "forge",
                "1.20.1")
        });

        var versionsResponse = JsonSerializer.Serialize(new object[]
        {
            VersionWithId(
                "version-rebuild",
                "1.2.3-forge",
                "https://cdn.test/testmod-1.2.3-forge-rebuild.jar",
                new string('c', 128),
                "forge",
                "1.20.1"),
            VersionWithId(
                "version-normalized",
                "1.2.3+mc1.20.1",
                "https://cdn.test/testmod-1.2.3-mc1.20.1.jar",
                new string('d', 128),
                "forge",
                "1.20.1")
        });

        using var client = new HttpClient(new FakeHandler(
            request => RouteModrinth(request, hashResponse, versionsResponse)));
        var service = new ModUpdateService(new ModrinthClient(client), downloadClient: client);

        var updates = await service.CheckUpdatesAsync(CreateInstance(temp.Root));

        Assert.Empty(updates);
    }

    [Fact]
    public async Task DownloadAsync_Sha512MismatchKeepsExistingJarAndThrows()
    {
        using var temp = new TempDirectory();
        var modsDir = Path.Combine(temp.Root, "mods");
        Directory.CreateDirectory(modsDir);

        var targetPath = Path.Combine(modsDir, "testmod.jar");
        var oldBytes = CreateJarBytes(
            "fabric.mod.json",
            """
            { "id": "testmod", "name": "Old", "version": "1.0.0" }
            """);
        await File.WriteAllBytesAsync(targetPath, oldBytes);

        var payload = CreateJarBytes(
            "fabric.mod.json",
            """
            { "id": "testmod", "name": "New", "version": "1.1.0" }
            """);

        using var client = new HttpClient(new FakeHandler(_ => DownloadResponse(payload)));
        var service = new ModUpdateService(downloadClient: client);

        var update = new ModUpdateInfo
        {
            FileName = "testmod.jar",
            LatestVersion = "1.1.0",
            DownloadUrl = "https://cdn.test/testmod-1.1.0.jar",
            Sha512 = new string('0', 128)
        };

        await Assert.ThrowsAsync<LauncherException>(
            () => service.DownloadAsync(update, modsDir));

        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(targetPath));
        Assert.Empty(Directory.GetFiles(modsDir, "*.download"));
        Assert.Empty(Directory.GetFiles(modsDir, "*.old-*"));
    }

    [Fact]
    public async Task DownloadAsync_WithMatchingSha512ReplacesJar()
    {
        using var temp = new TempDirectory();
        var modsDir = Path.Combine(temp.Root, "mods");
        Directory.CreateDirectory(modsDir);

        var targetPath = Path.Combine(modsDir, "testmod.jar");
        await File.WriteAllBytesAsync(
            targetPath,
            CreateJarBytes(
                "fabric.mod.json",
                """
                { "id": "testmod", "name": "Old", "version": "1.0.0" }
                """));

        var payload = CreateJarBytes(
            "fabric.mod.json",
            """
            { "id": "testmod", "name": "New", "version": "1.1.0" }
            """);

        using var client = new HttpClient(new FakeHandler(_ => DownloadResponse(payload)));
        var service = new ModUpdateService(downloadClient: client);

        var update = new ModUpdateInfo
        {
            FileName = "testmod.jar",
            LatestVersion = "1.1.0",
            DownloadUrl = "https://cdn.test/testmod-1.1.0.jar",
            Sha512 = ComputeSha512(payload),
            OriginalSha512 = ComputeSha512(await File.ReadAllBytesAsync(targetPath))
        };

        var path = await service.DownloadAsync(update, modsDir);

        Assert.Equal(targetPath, path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(targetPath));
        Assert.Empty(Directory.GetFiles(modsDir, "*.download"));
    }

    private static InstanceProfile CreateInstance(string gameDir) => new()
    {
        Id = "test",
        Name = "Test",
        GameDir = gameDir,
        MinecraftVersion = "1.20.1"
    };

    private static HttpResponseMessage RouteModrinth(
        HttpRequestMessage request,
        string hashResponse,
        string versionsResponse)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (path.EndsWith("/version_files", StringComparison.Ordinal))
        {
            return JsonResponse(hashResponse);
        }

        if (path.Contains("/project/", StringComparison.Ordinal) &&
            path.EndsWith("/version", StringComparison.Ordinal))
        {
            return JsonResponse(versionsResponse);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static object Version(
        string versionNumber,
        string url,
        string sha512,
        string loader,
        string gameVersion) =>
        VersionWithId("version-" + versionNumber, versionNumber, url, sha512, loader, gameVersion);

    private static object VersionWithId(
        string id,
        string versionNumber,
        string url,
        string sha512,
        string loader,
        string gameVersion) => new
    {
        id,
        project_id = "proj-test",
        name = "Test " + versionNumber,
        version_number = versionNumber,
        version_type = "release",
        date_published = "2026-01-01T00:00:00+00:00",
        changelog = "Changes in " + versionNumber,
        game_versions = new[] { gameVersion },
        loaders = new[] { loader },
        files = new[]
        {
            new
            {
                url,
                filename = "testmod-" + versionNumber + ".jar",
                primary = true,
                hashes = new Dictionary<string, string> { ["sha512"] = sha512 }
            }
        }
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage DownloadResponse(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload)
    };

    private static string CreateJar(string path, string entryName, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = CreateJarBytes(entryName, content);
        File.WriteAllBytes(path, bytes);
        return ComputeSha512(bytes);
    }

    private static byte[] CreateJarBytes(string entryName, string content)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static string ComputeSha512(byte[] bytes) =>
        Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }
    }
}
