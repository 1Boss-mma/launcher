using System.Text;
using SamaraLauncher.Core.ServerHost;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ServerPropertiesTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmptySet()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");

        var properties = ServerProperties.Load(path);

        Assert.Null(properties.Get("motd"));
        Assert.Equal("fallback", properties.Get("motd", "fallback"));
        Assert.Equal(42, properties.GetInt("server-port", 42));
        Assert.False(properties.GetBool("online-mode", false));
    }

    [Fact]
    public void Roundtrip_PreservesCommentsOrderUnknownKeysAndBrokenLines()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");

        var original = string.Join("\r\n", new[]
        {
            "# комментарий",
            "! ещё комментарий",
            "motd=a=b",
            "server-port=25565",
            "unknown-key = значение",
            "broken line without equals",
            "",
            "online-mode=false",
            ""
        });

        File.WriteAllText(path, original, new UTF8Encoding(false));

        var properties = ServerProperties.Load(path);
        properties.Set("server-port", 25566);
        properties.Set("motd", "Самара");
        properties.Save(path);

        var text = File.ReadAllText(path);

        Assert.Contains("# комментарий", text);
        Assert.Contains("! ещё комментарий", text);
        Assert.Contains("unknown-key = значение", text);
        Assert.Contains("broken line without equals", text);
        Assert.Contains("\r\n", text);

        var reloaded = ServerProperties.Load(path);

        Assert.Equal("Самара", reloaded.Get("motd"));
        Assert.Equal(25566, reloaded.GetInt("server-port"));
        Assert.Equal("значение", reloaded.Get("unknown-key"));
    }

    [Fact]
    public void Get_SplitsOnlyOnFirstEquals()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");
        File.WriteAllText(path, "motd=hello=world=!\n");

        var properties = ServerProperties.Load(path);

        Assert.Equal("hello=world=!", properties.Get("motd"));
    }

    [Fact]
    public void BoolAndInt_AreWrittenAndParsed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");

        var properties = ServerProperties.Load(path);
        properties.Set("online-mode", true);
        properties.Set("white-list", false);
        properties.Set("max-players", 6);
        properties.Save(path);

        var text = File.ReadAllText(path);

        Assert.Contains("online-mode=true", text);
        Assert.Contains("white-list=false", text);
        Assert.Contains("max-players=6", text);

        var reloaded = ServerProperties.Load(path);

        Assert.True(reloaded.GetBool("online-mode"));
        Assert.False(reloaded.GetBool("white-list", true));
        Assert.Equal(6, reloaded.GetInt("max-players"));
        Assert.Equal(0, reloaded.GetInt("missing"));
    }

    [Fact]
    public void Save_CreatesFileWithUtf8WithoutBom()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "nested", "server.properties");

        var properties = ServerProperties.Load(path);
        properties.Set("motd", "Самара");
        properties.Save(path);

        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Самара", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Set_ExistingKey_DoesNotDuplicateLine()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");
        File.WriteAllText(path, "motd=old\n");

        var properties = ServerProperties.Load(path);
        properties.Set("motd", "new");
        properties.Save(path);

        var lines = File
            .ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines, line => line.StartsWith("motd=", StringComparison.Ordinal));
        Assert.Equal("motd=new", Assert.Single(lines));
    }

    [Fact]
    public void Set_NewKey_AppendsToEnd()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "server.properties");
        File.WriteAllText(path, "motd=old\n");

        var properties = ServerProperties.Load(path);
        properties.Set("server-port", 25565);
        properties.Save(path);

        var lines = File
            .ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("server-port=25565", lines[^1]);
    }
}
