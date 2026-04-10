using MemShack.Application.Chunking;
using MemShack.Application.Mining;
using MemShack.Application.Scanning;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.Config.Projects;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Mining;

[TestClass]
public sealed class ProjectMinerIntegrationTests
{
    [TestMethod]
    public async Task MinesProjectFilesIntoDrawerCollection()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: test_project
            rooms:
              - name: backend
                description: Backend code
                keywords:
                  - api
              - name: general
                description: General
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(Path.Combine(projectRoot, "backend", "app.py"), string.Join('\n', Enumerable.Repeat("def main(): return 'hello world'", 40)));
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        var result = await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(result.DrawersFiled > 0);
        Assert.Single(result.RoomCounts);
        Assert.Single(result.FileResults);
        Assert.Equal("backend", drawers[0].Metadata.Room);
        Assert.Equal("test_project", drawers[0].Metadata.Wing);
        Assert.Equal("mempalace", drawers[0].Metadata.AddedBy);
        Assert.Matches(@"^drawer_test_project_backend_[a-f0-9]{24}$", drawers[0].Id);
    }

    [TestMethod]
    public async Task DryRunDoesNotWriteAndSecondRunSkipsDuplicates()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: docs_project
            rooms:
              - name: documentation
                description: Docs
              - name: general
                description: General
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.WriteAllText(Path.Combine(projectRoot, "docs", "guide.md"), string.Join("\n\n", Enumerable.Repeat("Guide paragraph with documentation details.", 25)));

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        var dryRun = await miner.MineAsync(projectRoot, dryRun: true);
        var firstRun = await miner.MineAsync(projectRoot);
        var secondRun = await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(dryRun.DrawersFiled > 0);
        Assert.Empty(await store.GetDrawersAsync(CollectionNames.Drawers, wing: "missing"));
        Assert.Equal(firstRun.DrawersFiled, drawers.Count);
        Assert.Equal(0, secondRun.DrawersFiled);
        Assert.True(secondRun.FilesSkipped > 0);
    }

    [TestMethod]
    public async Task ReindexesSourceFileWhenEmbeddingSignatureChanges()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: docs_project
            rooms:
              - name: documentation
                description: Docs
              - name: general
                description: General
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var sourceFile = Path.Combine(projectRoot, "docs", "guide.md");
        File.WriteAllText(sourceFile, string.Join("\n\n", Enumerable.Repeat("Guide paragraph with documentation details.", 25)));

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_docs_project_documentation_old",
                "legacy text",
                new DrawerMetadata
                {
                    Wing = "docs_project",
                    Room = "documentation",
                    SourceFile = sourceFile,
                    ChunkIndex = 0,
                    AddedBy = "legacy",
                    FiledAt = "2026-04-01T00:00:00",
                    EmbeddingSignature = "legacy-signature",
                }));

        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        var result = await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(result.DrawersFiled > 0);
        Assert.DoesNotContain(drawers, drawer => drawer.Id == "drawer_docs_project_documentation_old");
        Assert.All(drawers, drawer => Assert.Equal(EmbeddingSignatures.Current, drawer.Metadata.EmbeddingSignature));
    }

    [TestMethod]
    public async Task ModifiedSourceFile_IsReminedWhenStoredMtimeIsStale()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: docs_project
            rooms:
              - name: documentation
                description: Docs
              - name: general
                description: General
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        var sourceFile = Path.Combine(projectRoot, "docs", "guide.md");
        File.WriteAllText(sourceFile, string.Join("\n\n", Enumerable.Repeat("Guide paragraph with documentation details.", 25)));
        var staleMtime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_docs_project_documentation_old",
                "legacy text",
                new DrawerMetadata
                {
                    Wing = "docs_project",
                    Room = "documentation",
                    SourceFile = sourceFile,
                    SourceMtimeUtcMs = staleMtime,
                    ChunkIndex = 0,
                    AddedBy = "legacy",
                    FiledAt = "2026-04-01T00:00:00",
                    EmbeddingSignature = EmbeddingSignatures.Current,
                }));

        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc));

        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        var result = await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(result.DrawersFiled > 0);
        Assert.DoesNotContain(drawers, drawer => drawer.Id == "drawer_docs_project_documentation_old");
        Assert.All(drawers, drawer => Assert.NotNull(drawer.Metadata.SourceMtimeUtcMs));
        Assert.All(drawers, drawer => Assert.NotEqual(staleMtime, drawer.Metadata.SourceMtimeUtcMs));
    }

    [TestMethod]
    public async Task RoomCountsUseActualDetectedRoom()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: docs_project
            rooms:
              - name: documentation
                description: Docs
                keywords:
                  - docs
              - name: general
                description: General
            """);
        File.WriteAllText(
            Path.Combine(projectRoot, "README.md"),
            string.Join('\n', Enumerable.Repeat("documentation docs guide reference", 40)));

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        var result = await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.Single(result.RoomCounts);
        Assert.True(result.RoomCounts.ContainsKey("documentation"));
        Assert.Equal("documentation", drawers[0].Metadata.Room);
    }

    [TestMethod]
    public async Task PathKeywordsWinBeforeContentScoring()
    {
        using var temp = new TemporaryDirectory();
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "mempalace.yaml"), """
            wing: docs_project
            rooms:
              - name: scripts
                description: Scripts
                keywords:
                  - tools
              - name: fixtures
                description: Fixtures
                keywords:
                  - fixtures
                  - sample
              - name: general
                description: General
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "tools"));
        File.WriteAllText(
            Path.Combine(projectRoot, "tools", "live_validation.py"),
            string.Join('\n', Enumerable.Repeat("fixture sample fixture sample fixture sample", 40)));

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ProjectMiner(
            new YamlProjectPalaceConfigLoader(),
            new ProjectScanner(),
            new TextChunker(),
            store);

        await miner.MineAsync(projectRoot);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.NotEmpty(drawers);
        Assert.All(drawers, drawer => Assert.Equal("scripts", drawer.Metadata.Room));
    }
}
