using SamaraLauncher.Core.Backups;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class BackupServiceTests
{
    [Theory]
    [InlineData(null, "instance")]
    [InlineData("", "instance")]
    [InlineData("   ", "instance")]
    [InlineData(".hidden.", "hidden")]
    [InlineData("  my-pack  ", "my-pack")]
    [InlineData("my.pack", "my.pack")]
    public void SanitizeInstanceId_NormalizesInput(string? instanceId, string expected)
    {
        Assert.Equal(expected, BackupService.SanitizeInstanceId(instanceId));
    }

    [Fact]
    public void SanitizeInstanceId_ReplacesAllInvalidFileNameCharacters()
    {
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var ch in invalid)
        {
            var result = BackupService.SanitizeInstanceId("ab" + ch + "cd");

            Assert.NotEmpty(result);
            Assert.False(result.Contains(ch));
        }
    }
}
