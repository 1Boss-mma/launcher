using System.Text;
using SamaraLauncher.Core.Logs;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class LogEncodingTests
{
    [Fact]
    public void ReadLauncherLog_TruncatedUtf8Tail_IsDetectedAsUtf8()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");

        var text = "[12:00:00] [main/INFO]: Первая строка\n" +
                   "[12:00:01] [main/INFO]: Последняя строка: привет";
        var bytes = Encoding.UTF8.GetBytes(text);
        var truncatedChar = Encoding.UTF8.GetBytes("ы");
        File.WriteAllBytes(path, bytes.Concat(truncatedChar[..1]).ToArray());

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, maxLines: 10);

        Assert.Equal(2, lines.Count);
        Assert.Equal("[12:00:00] [main/INFO]: Первая строка", lines[0].Text);
        Assert.Equal("[12:00:01] [main/INFO]: Последняя строка: привет", lines[1].Text);
        Assert.DoesNotContain('\uFFFD', lines[1].Text);
    }

    [Fact]
    public void ReadLauncherLog_Cp1251Log_FallsBackToCp1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");

        var expected = new[]
        {
            "[12:00:00] [main/INFO]: Загрузка модов",
            "[12:00:01] [main/ERROR]: Ошибка: файл не найден"
        };
        File.WriteAllBytes(path, cp1251.GetBytes(string.Join("\n", expected)));

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, maxLines: 10);

        Assert.Equal(expected, lines.Select(line => line.Text));
    }

    [Fact]
    public void ReadLauncherLog_Utf8EncodingSampleCutInsideCharacter_IsDetectedAsUtf8()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");

        var firstLine = new string('a', 4095) + "Ж";
        var secondLine = "[12:00:01] [main/INFO]: вторая строка";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(firstLine + "\n" + secondLine));

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, maxLines: 10);

        Assert.Equal(2, lines.Count);
        Assert.Equal(firstLine, lines[0].Text);
        Assert.Equal(secondLine, lines[1].Text);
    }

    [Fact]
    public void ReadLauncherLog_FileLargerThanTailBuffer_ReturnsLastLinesInOrder()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");

        const int lineCount = 30_000;
        var allLines = new List<string>(lineCount);
        var content = new StringBuilder();
        for (var i = 0; i < lineCount; i++)
        {
            var line = $"line {i:D6}: the quick brown fox jumps over the lazy dog";
            allLines.Add(line);
            content.Append(line).Append('\n');
        }

        File.WriteAllText(path, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        const int requested = 25;
        var expected = allLines.Skip(allLines.Count - requested).ToArray();

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, requested);

        Assert.Equal(requested, lines.Count);
        Assert.Equal(expected, lines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public void ReadLauncherLog_FewerLinesThanRequested_ReturnsAllInOrder()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");

        var expected = new[] { "first", "second", "third" };
        File.WriteAllText(path, string.Join("\n", expected), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, maxLines: 100);

        Assert.Equal(expected, lines.Select(line => line.Text));
    }

    [Fact]
    public void ReadLauncherLog_EmptyFile_ReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "latest.log");
        File.WriteAllBytes(path, Array.Empty<byte>());

        using var service = new LogService();
        var lines = service.ReadLauncherLog(path, maxLines: 10);

        Assert.Empty(lines);
    }
}
