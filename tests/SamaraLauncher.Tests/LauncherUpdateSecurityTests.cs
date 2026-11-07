using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Update;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LauncherUpdateSecurityTests
{
    private const string ValidSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ")]
    public async Task CheckAsync_RequiresValidSha256InManifest(string? sha256)
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(_ => JsonResponse(
            ManifestJson("2.0.0", "https://example.com/Launcher-2.0.0.exe", sha256)));
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => service.CheckAsync(
                "https://example.com/latest.json",
                "1.0.0",
                CancellationToken.None));

        Assert.Contains("sha256", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_RejectsManifestWithoutSha256()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(_ => JsonResponse(
            """
            {
              "version": "2.0.0",
              "url": "https://example.com/Launcher-2.0.0.exe"
            }
            """));
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => service.CheckAsync(
                "https://example.com/latest.json",
                "1.0.0",
                CancellationToken.None));

        Assert.Contains("sha256", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://example.com/latest.json")]
    [InlineData("file:///C:/temp/latest.json")]
    [InlineData("relative/latest.json")]
    public async Task CheckAsync_RejectsUnsupportedManifestUrl(string url)
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(_ =>
            throw new InvalidOperationException("HTTP-запрос не должен выполняться."));
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);

        await Assert.ThrowsAsync<LauncherException>(
            () => service.CheckAsync(url, "1.0.0", CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectFromHttpsToHttp()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://example.com/Launcher-1.0.0.exe")
        });
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);
        var manifest = CreateManifest("https://example.com/Launcher-1.0.0.exe");

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => service.DownloadAsync(manifest, null, CancellationToken.None));

        Assert.Contains("HTTP", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(GetUpdateFiles(temp));
    }

    [Fact]
    public async Task DownloadAsync_RequiresSha256BeforeDownloading()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);
        var manifest = CreateManifest("https://example.com/Launcher-1.0.0.exe", sha256: "");

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => service.DownloadAsync(manifest, null, CancellationToken.None));

        Assert.Contains("sha256", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "updates")));
    }

    [Fact]
    public async Task DownloadAsync_DeletesFileWithMismatchedSha256()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri)
        });
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);
        var manifest = CreateManifest("https://example.com/Launcher-1.0.0.exe");

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => service.DownloadAsync(manifest, null, CancellationToken.None));

        Assert.Contains("сумма", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(GetUpdateFiles(temp));
    }

    [Fact]
    public async Task DownloadAsync_AcceptsPrefixedUppercaseSha256()
    {
        using var temp = new TempDirectory();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var expectedHash = SHA256.HashData(payload);
        var handler = new StubHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri)
        });
        using var client = new HttpClient(handler);
        var service = new LauncherUpdateService(new LauncherPaths(localDataDir: temp.Root), client);
        var manifest = CreateManifest(
            "https://example.com/Launcher-1.0.0.exe",
            "sha256:" + Convert.ToHexString(expectedHash));

        var path = await service.DownloadAsync(manifest, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    private static UpdateManifest CreateManifest(string url, string sha256 = ValidSha256) =>
        new()
        {
            Version = "1.0.0",
            Url = url,
            Sha256 = sha256
        };

    private static string ManifestJson(string version, string url, string? sha256) =>
        JsonSerializer.Serialize(new
        {
            version,
            url,
            sha256
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string[] GetUpdateFiles(TempDirectory temp)
    {
        var updatesDir = Path.Combine(temp.Root, "updates");
        return Directory.Exists(updatesDir)
            ? Directory.GetFiles(updatesDir)
            : Array.Empty<string>();
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(_handler(request));
        }
    }
}
