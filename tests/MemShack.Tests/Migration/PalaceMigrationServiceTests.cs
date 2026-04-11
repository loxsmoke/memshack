using MemShack.Application.Migration;
using MemShack.Core.Constants;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Migration;

[TestClass]
public sealed class PalaceMigrationServiceTests
{
    [TestMethod]
    public async Task MigrateAsync_DryRun_SummarizesSqliteDrawers()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        await ChromaSqliteFixtureBuilder.CreateAsync(
            palacePath,
            [
                new SqliteDrawerSeed(
                    "drawer-1",
                    "JWT authentication protects the backend API.",
                    new Dictionary<string, object?>
                    {
                        ["wing"] = "project",
                        ["room"] = "src",
                        ["source_file"] = "/repo/src/auth.py",
                        ["chunk_index"] = 0,
                    }),
                new SqliteDrawerSeed(
                    "drawer-2",
                    "Migration notes live in the docs folder.",
                    new Dictionary<string, object?>
                    {
                        ["wing"] = "project",
                        ["room"] = "documentation",
                        ["source_file"] = "/repo/docs/migration.md",
                        ["chunk_index"] = 0,
                    }),
            ]);

        var service = new PalaceMigrationService(path => new ChromaCompatibilityVectorStore(path));
        var result = await service.MigrateAsync(palacePath, dryRun: true);

        Assert.Equal("1.x", result.SourceVersion);
        Assert.Equal(2, result.DrawersExtracted);
        Assert.Equal(0, result.DrawersImported);
        Assert.True(result.DryRun);
        Assert.Equal(1, result.Wings.Count);
        Assert.Equal(2, result.Wings[0].Rooms.Count);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(Path.Combine(palacePath, "collections", $"{CollectionNames.Drawers}.json")));
    }

    [TestMethod]
    public async Task MigrateAsync_Live_BacksUpAndRebuildsPalace()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        await ChromaSqliteFixtureBuilder.CreateAsync(
            palacePath,
            [
                new SqliteDrawerSeed(
                    "drawer-1",
                    "JWT authentication protects the backend API.",
                    new Dictionary<string, object?>
                    {
                        ["wing"] = "project",
                        ["room"] = "src",
                        ["source_file"] = "/repo/src/auth.py",
                        ["chunk_index"] = 0,
                    }),
            ]);

        var service = new PalaceMigrationService(path => new ChromaCompatibilityVectorStore(path));
        var result = await service.MigrateAsync(palacePath);

        Assert.False(result.DryRun);
        Assert.Equal(1, result.DrawersExtracted);
        Assert.Equal(1, result.DrawersImported);
        Assert.True(Directory.Exists(Assert.NotNull(result.BackupPath)));

        var rebuiltStore = new ChromaCompatibilityVectorStore(palacePath);
        var drawers = await rebuiltStore.GetDrawersAsync(CollectionNames.Drawers);
        Assert.Equal(1, drawers.Count);
        Assert.Equal("project", drawers[0].Metadata.Wing);
        Assert.Equal("src", drawers[0].Metadata.Room);
    }
}
