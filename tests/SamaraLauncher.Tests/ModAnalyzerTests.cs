using System.IO.Compression;
using System.Text;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class ModAnalyzerTests
{
    [Fact]
    public void Analyze_DependencyFromMetaInfJars_IsNotMissing()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        var parent = CreateJar(new[]
        {
            ("META-INF/mods.toml", Text(ForgeToml(
                "artifacts",
                "9.5.18",
                ("expandability", "[9.0.0,)")))),
            ("META-INF/jars/expandability-9.0.4.jar", ForgeJar("expandability", "9.0.4"))
        });

        WriteJar(Path.Combine(modsDir, "artifacts-forge-9.5.18.jar"), parent);

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        Assert.Empty(report.MissingDependencies);

        var parentMetadata = Assert.Single(report.Mods);
        var nested = Assert.Single(parentMetadata.NestedMods);
        Assert.Equal("expandability", nested.ModId);
        Assert.Equal("9.0.4", nested.Version);
    }

    [Fact]
    public void Analyze_DependencyFromJarJarMetadataJson_IsNotMissing()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        const string metadataJson =
            """
            {
              "jars": [
                {
                  "identifier": { "group": "com.example", "artifact": "shield_api" },
                  "version": { "range": "[1.2.0,)", "artifactVersion": "1.2.0" },
                  "path": "libs/shield_api-1.2.0.jar"
                }
              ]
            }
            """;

        var parent = CreateJar(new[]
        {
            ("META-INF/mods.toml", Text(ForgeToml(
                "paladins",
                "1.0.0",
                ("shield_api", "(1.1.0,)")))),
            ("META-INF/jarjar/metadata.json", Text(metadataJson)),
            ("libs/shield_api-1.2.0.jar", ForgeJar("shield_api", "1.2.0"))
        });

        WriteJar(Path.Combine(modsDir, "paladins-1.0.0.jar"), parent);

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        Assert.Empty(report.MissingDependencies);
        var nested = Assert.Single(Assert.Single(report.Mods).NestedMods);
        Assert.Equal("shield_api", nested.ModId);
    }

    [Fact]
    public void Analyze_NestedOlderVersion_DoesNotShadowInstalledRootMod()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        // creeperoverhaul.jar вкладывает resourcefullib 2.1.20, но отдельный
        // resourcefullib.jar 2.1.29 новее и должен победить.
        WriteJar(
            Path.Combine(modsDir, "creeperoverhaul-3.0.2-forge.jar"),
            CreateJar(new[]
            {
                ("META-INF/mods.toml", Text(ForgeToml("creeperoverhaul", "3.0.2"))),
                ("META-INF/jars/resourcefullib-forge-2.1.20.jar", ForgeJar("resourcefullib", "2.1.20"))
            }));
        WriteJar(Path.Combine(modsDir, "resourcefullib-2.1.29.jar"), ForgeJar("resourcefullib", "2.1.29"));
        WriteJar(
            Path.Combine(modsDir, "friendsandfoes-5.0.3.jar"),
            ForgeJar("friendsandfoes", "5.0.3", ("resourcefullib", "[2.1.29,)")));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        Assert.Empty(report.MissingDependencies);
    }

    [Fact]
    public void Analyze_MavenStyleVersionWithSuffix_MatchesBracketRange()
    {
        var report = AnalyzeWithDependency("1.20.1-2.2.0", "[1.20.1-2,1.20.1-3)");

        Assert.Empty(report.MissingDependencies);
    }

    [Fact]
    public void Analyze_PrereleaseVersion_DoesNotMatchReleaseMinimum()
    {
        var report = AnalyzeWithDependency("1.0.0-beta", "[1.0.0,)");

        var issue = Assert.Single(report.MissingDependencies);
        Assert.Equal("testlib", issue.ModId);
        Assert.Equal("1.0.0-beta", issue.FoundVersion);
    }

    [Fact]
    public void Analyze_ShortVersion_MatchesExactBracketVersion()
    {
        var report = AnalyzeWithDependency("1.0", "[1.0.0]");

        Assert.Empty(report.MissingDependencies);
    }

    [Fact]
    public void Analyze_ReallyMissingDependency_IsReported()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "owner-1.0.0.jar"),
            ForgeJar("owner", "1.0.0", ("testlib", "[1.0,)")));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        var issue = Assert.Single(report.MissingDependencies);
        Assert.Equal("testlib", issue.ModId);
        Assert.Equal("owner", issue.RequiredBy);
        Assert.Equal("owner-1.0.0.jar", issue.RequiredByFileName);
    }

    [Fact]
    public void Analyze_MultipleModsBlocks_UsesFirstWithoutWarning()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "embeddium.jar"),
            CreateJar(new[]
            {
                ("META-INF/mods.toml", Text(
                    """
                    modLoader="javafml"
                    loaderVersion="[47,)"

                    [[mods]]
                    modId="embeddium"
                    version="0.3.31"

                    [[mods]]
                    modId="embeddium_stub"
                    version="0.3.31"
                    """))
            }));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        var metadata = Assert.Single(report.Mods);
        Assert.Equal("embeddium", metadata.ModId);
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("несколько блоков", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ContainerWithoutRootManifest_ReadsNestedModWithoutWarning()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "kotlinforforge-4.12.0-all.jar"),
            CreateJar(new[]
            {
                ("META-INF/jarjar/kffmod-4.12.0.jar", ForgeJar("kotlinforforge", "4.12.0"))
            }));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        var metadata = Assert.Single(report.Mods);
        var nested = Assert.Single(metadata.NestedMods);
        Assert.Equal("kotlinforforge", nested.ModId);

        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("не найдены метаданные", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_FabricAlternativeRange_AcceptsVersionFromSecondAlternative()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "owner.jar"),
            FabricJar(
                "owner",
                "1.0.0",
                """{ "minecraft": "1.20.1", "testlib": ">=1.0 <2.0 || >=3.0" }"""));
        WriteJar(Path.Combine(modsDir, "testlib.jar"), FabricJar("testlib", "3.5.0"));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        Assert.Empty(report.MissingDependencies);
    }

    [Fact]
    public void Analyze_FabricAlternativeRange_RejectsVersionOutsideAllAlternatives()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "owner.jar"),
            FabricJar(
                "owner",
                "1.0.0",
                """{ "minecraft": "1.20.1", "testlib": ">=1.0 <2.0 || >=3.0" }"""));
        WriteJar(Path.Combine(modsDir, "testlib.jar"), FabricJar("testlib", "2.5.0"));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));

        var issue = Assert.Single(report.MissingDependencies);
        Assert.Equal("testlib", issue.ModId);
    }

    [Fact]
    public void RecomputeDependencyIssues_AfterDisablingDependency_ReportsDisabled()
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "owner-1.0.0.jar"),
            ForgeJar("owner", "1.0.0", ("testlib", "[1.0,)")));
        WriteJar(Path.Combine(modsDir, "testlib-1.2.0.jar"), ForgeJar("testlib", "1.2.0"));

        var report = new ModAnalyzer().Analyze(CreateInstance(temp.Root));
        Assert.Empty(report.MissingDependencies);

        report.Mods.Single(mod => mod.ModId == "testlib").Enabled = false;
        ModAnalyzer.RecomputeDependencyIssues(report);

        var issue = Assert.Single(report.DisabledDependencies);
        Assert.Equal("testlib", issue.ModId);
        Assert.Empty(report.MissingDependencies);
    }

    private static ModAnalysisReport AnalyzeWithDependency(string providerVersion, string range)
    {
        using var temp = new TempDirectory();
        var modsDir = CreateModsDirectory(temp.Root);

        WriteJar(
            Path.Combine(modsDir, "owner-1.0.0.jar"),
            ForgeJar("owner", "1.0.0", ("testlib", range)));
        WriteJar(
            Path.Combine(modsDir, "testlib.jar"),
            ForgeJar("testlib", providerVersion));

        return new ModAnalyzer().Analyze(CreateInstance(temp.Root));
    }

    private static InstanceProfile CreateInstance(string gameDir) => new()
    {
        Id = "test",
        Name = "Test",
        GameDir = gameDir,
        MinecraftVersion = "1.20.1"
    };

    private static string CreateModsDirectory(string root)
    {
        var modsDir = Path.Combine(root, "mods");
        Directory.CreateDirectory(modsDir);
        return modsDir;
    }

    private static void WriteJar(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    private static byte[] ForgeJar(
        string modId,
        string version,
        params (string ModId, string VersionRange)[] dependencies) =>
        CreateJar(new[]
        {
            ("META-INF/mods.toml", Text(ForgeToml(modId, version, dependencies)))
        });

    private static string ForgeToml(
        string modId,
        string version,
        params (string ModId, string VersionRange)[] dependencies)
    {
        var toml = new StringBuilder();
        toml.AppendLine("modLoader=\"javafml\"");
        toml.AppendLine("loaderVersion=\"[47,)\"");
        toml.AppendLine();
        toml.AppendLine("[[mods]]");
        toml.AppendLine($"modId=\"{modId}\"");
        toml.AppendLine($"version=\"{version}\"");
        toml.AppendLine($"displayName=\"{modId}\"");

        foreach (var dependency in dependencies)
        {
            toml.AppendLine();
            toml.AppendLine($"[[dependencies.{modId}]]");
            toml.AppendLine($"modId=\"{dependency.ModId}\"");
            toml.AppendLine("mandatory=true");
            toml.AppendLine($"versionRange=\"{dependency.VersionRange}\"");
            toml.AppendLine("ordering=\"NONE\"");
            toml.AppendLine("side=\"BOTH\"");
        }

        return toml.ToString();
    }

    private static byte[] FabricJar(string modId, string version, string? depends = null) =>
        CreateJar(new[]
        {
            ("fabric.mod.json", Text(depends is null
                ? $$"""{ "id": "{{modId}}", "version": "{{version}}" }"""
                : $$"""{ "id": "{{modId}}", "version": "{{version}}", "depends": {{depends}} }"""))
        });

    private static byte[] CreateJar((string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Text(string value) => new UTF8Encoding(false).GetBytes(value);
}
