using System.Xml.Linq;
using MemShack.Tests.Utilities;
using System.Text.RegularExpressions;

namespace MemShack.Tests.Packaging;

[TestClass]
public sealed class ToolPackagingTests
{
    [TestMethod]
    public void CliProject_ContainsExpectedDotNetToolMetadata()
    {
        var projectPath = Path.Combine(FixturePaths.RepoRootPath, "src", "MemShack.Cli", "MemShack.Cli.csproj");
        var document = XDocument.Load(projectPath);
        var version = Assert.NotNull(
            GetProperty(document, "Version"),
            "MemShack.Cli.csproj should define a Version property.");

        Assert.Equal("true", GetProperty(document, "PackAsTool"), "MemShack.Cli.csproj should be packaged as a .NET tool.");
        Assert.Equal("LoxSmoke.Mems", GetProperty(document, "PackageId"), "MemShack.Cli.csproj should use the expected NuGet package ID.");
        Assert.Equal("mems", GetProperty(document, "ToolCommandName"), "MemShack.Cli.csproj should expose the mems tool command.");
        Assert.True(!string.IsNullOrWhiteSpace(version), "The CLI Version property should not be null, empty, or whitespace.");
        Assert.True(
            Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"),
            "The CLI Version property should use semantic version format number.number.number.");
        Assert.Equal("nuget", GetProperty(document, "PackageOutputPath"), "MemShack.Cli.csproj should write packed tool packages to the project-local nuget folder.");
        Assert.Equal("README.md", GetProperty(document, "PackageReadmeFile"), "MemShack.Cli.csproj should include README.md as the package readme file.");
        Assert.Equal("LoxSmoke", GetProperty(document, "Authors"), "MemShack.Cli.csproj should set the package author to LoxSmoke.");
        Assert.Equal(
            "MemShack CLI packaged as a .NET tool for mining, searching, and maintaining MemPalace-compatible memory stores.",
            GetProperty(document, "Description"),
            "MemShack.Cli.csproj should preserve the expected package description.");
        Assert.Equal(
            "memory;ai;llm;rag;cli;dotnet-tool;mcp",
            GetProperty(document, "PackageTags"),
            "MemShack.Cli.csproj should preserve the expected package tags.");
    }

    [TestMethod]
    public void ToolPackagingAssets_Exist()
    {
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "src", "MemShack.Cli", "README.md")),
            "The packaged CLI README.md file should exist under src/MemShack.Cli.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "hooks", "README.md")),
            "The repo-local hook guide should exist at hooks/README.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "hooks", "memshack_save_hook.sh")),
            "The save hook script should exist at hooks/memshack_save_hook.sh.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "hooks", "memshack_precompact_hook.sh")),
            "The pre-compact hook script should exist at hooks/memshack_precompact_hook.sh.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "instructions", "README.md")),
            "The instruction asset guide should exist at instructions/README.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "instructions", "codex.md")),
            "The Codex instruction asset should exist at instructions/codex.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "instructions", "claude-code.md")),
            "The Claude Code instruction asset should exist at instructions/claude-code.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, ".agents", "plugins", "marketplace.json")),
            "The repo-local plugin marketplace file should exist at .agents/plugins/marketplace.json.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "plugins", "memshack", ".codex-plugin", "plugin.json")),
            "The repo-local MemShack plugin manifest should exist at plugins/memshack/.codex-plugin/plugin.json.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "integrations", "openclaw", "SKILL.md")),
            "The OpenClaw skill asset should exist at integrations/openclaw/SKILL.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "docs", "tool-installation.md")),
            "The contributor packaging guide should exist at docs/tool-installation.md.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "tools", "test-tool-install.ps1")),
            "The PowerShell packaging smoke test script should exist at tools/test-tool-install.ps1.");
        Assert.True(
            File.Exists(Path.Combine(FixturePaths.RepoRootPath, "tools", "test-tool-install.sh")),
            "The bash packaging smoke test script should exist at tools/test-tool-install.sh.");
    }

    [TestMethod]
    public void CliProject_DoesNotPackBundledChromaSidecarAssets()
    {
        var projectPath = Path.Combine(FixturePaths.RepoRootPath, "src", "MemShack.Cli", "MemShack.Cli.csproj");
        var document = XDocument.Load(projectPath);
        var chromaItem = document.Root?
            .Elements("ItemGroup")
            .Elements("None")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("Include"), @"chroma\**\*", StringComparison.Ordinal));

        Assert.Null(chromaItem, "MemShack.Cli.csproj should no longer package placeholder bundled Chroma sidecar assets.");
    }

    [TestMethod]
    public void CliProject_CopiesHookAndInstructionAssetsIntoToolOutput()
    {
        var projectPath = Path.Combine(FixturePaths.RepoRootPath, "src", "MemShack.Cli", "MemShack.Cli.csproj");
        var document = XDocument.Load(projectPath);
        var contentItems = document.Root?
            .Elements("ItemGroup")
            .Elements("Content")
            .ToArray() ?? [];

        Assert.Contains(
            contentItems,
            element => string.Equals((string?)element.Attribute("Include"), @"..\..\hooks\**\*", StringComparison.Ordinal));
        Assert.Contains(
            contentItems,
            element => string.Equals((string?)element.Attribute("Include"), @"..\..\instructions\**\*", StringComparison.Ordinal));
        Assert.Contains(
            contentItems,
            element => string.Equals((string?)element.Attribute("Include"), @"..\..\integrations\**\*", StringComparison.Ordinal));
    }

    private static string? GetProperty(XDocument document, string propertyName) =>
        document.Root?
            .Elements("PropertyGroup")
            .Elements(propertyName)
            .Select(element => element.Value)
            .FirstOrDefault();
}
