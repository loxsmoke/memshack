using MemShack.Application.Chunking;
using MemShack.Application.Mining;
using MemShack.Application.Scanning;
using MemShack.Core.Constants;
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
        Assert.StartsWith("drawer_test_project_backend_", drawers[0].Id);
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
    public async Task RoomCountsFollowPythonPathOnlySummaryBehavior()
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
        Assert.True(result.RoomCounts.ContainsKey("general"));
        Assert.Equal("documentation", drawers[0].Metadata.Room);
    }
}
