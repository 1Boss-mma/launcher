using SamaraLauncher.Core.Minecraft;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ForgeVersionCatalogTests
{
    private const string MetadataXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>net.minecraftforge</groupId>
          <artifactId>forge</artifactId>
          <versioning>
            <latest>1.21.1-52.0.1</latest>
            <release>1.21.1-52.0.1</release>
            <versions>
              <version>1.20.1-47.4.5</version>
              <version>1.20.1-47.4.9</version>
              <version>1.20.1-47.4.28</version>
              <version>1.20.1-47.4.0</version>
              <version>1.20.2-48.1.0</version>
              <version>1.21.1-52.0.1</version>
            </versions>
          </versioning>
        </metadata>
        """;

    [Fact]
    public void ParseLatest_ChoosesHighestVersionForExactMinecraftPrefix()
    {
        Assert.Equal("47.4.28", ForgeVersionCatalog.ParseLatest(MetadataXml, "1.20.1"));
    }

    [Fact]
    public void ParseLatest_IgnoresOtherMinecraftVersions()
    {
        Assert.Equal("48.1.0", ForgeVersionCatalog.ParseLatest(MetadataXml, "1.20.2"));
        Assert.Equal("52.0.1", ForgeVersionCatalog.ParseLatest(MetadataXml, "1.21.1"));
    }

    [Fact]
    public void ParseLatest_NoMatchingVersion_ReturnsNull()
    {
        Assert.Null(ForgeVersionCatalog.ParseLatest(MetadataXml, "1.19.4"));
        Assert.Null(ForgeVersionCatalog.ParseLatest("", "1.20.1"));
    }

    [Fact]
    public void ParseLatest_UsesNumericComparison()
    {
        const string xml = """
            <metadata>
              <versioning>
                <versions>
                  <version>1.20.1-47.4.9</version>
                  <version>1.20.1-47.4.10</version>
                </versions>
              </versioning>
            </metadata>
            """;

        Assert.Equal("47.4.10", ForgeVersionCatalog.ParseLatest(xml, "1.20.1"));
    }
}
