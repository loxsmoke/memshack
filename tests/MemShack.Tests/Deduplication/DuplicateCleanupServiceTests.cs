using MemShack.Application.Deduplication;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Deduplication;

[TestClass]
public sealed class DuplicateCleanupServiceTests
{
    [TestMethod]
    public async Task DeduplicateAsync_WithWingFilter_OnlyDeletesWithinThatWing()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.EnsureCollectionAsync(CollectionNames.Drawers);

        foreach (var drawer in CreateDuplicateDrawers("alpha", "/repo/src/auth.py"))
        {
            await store.AddDrawerAsync(CollectionNames.Drawers, drawer);
        }

        foreach (var drawer in CreateDuplicateDrawers("beta", "/repo/src/auth.py"))
        {
            await store.AddDrawerAsync(CollectionNames.Drawers, drawer);
        }

        var service = new DuplicateCleanupService(store);
        var result = await service.DeduplicateAsync(CollectionNames.Drawers, wing: "alpha", dryRun: false);
        var remaining = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.Equal(4, result.DeletedCount);
        Assert.Equal(6, remaining.Count);
        Assert.Equal(1, remaining.Count(drawer => drawer.Metadata.Wing == "alpha"));
        Assert.Equal(5, remaining.Count(drawer => drawer.Metadata.Wing == "beta"));
    }

    [TestMethod]
    public async Task GetStatsAsync_ReportsCandidateGroups()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.EnsureCollectionAsync(CollectionNames.Drawers);

        foreach (var drawer in CreateDuplicateDrawers("alpha", "/repo/src/auth.py"))
        {
            await store.AddDrawerAsync(CollectionNames.Drawers, drawer);
        }

        var service = new DuplicateCleanupService(store);
        var stats = await service.GetStatsAsync(CollectionNames.Drawers, wing: "alpha");

        Assert.Equal(5, stats.TotalDrawers);
        Assert.Equal(1, stats.SourceGroupCount);
        Assert.Equal(5, stats.DrawersInCandidateGroups);
        Assert.Equal("/repo/src/auth.py", Assert.Single(stats.LargestGroups).SourceFile);
    }

    private static IReadOnlyList<DrawerRecord> CreateDuplicateDrawers(string wing, string sourceFile)
    {
        var texts = new[]
        {
            "JWT authentication refresh tokens protect the backend API and rotate sessions safely.",
            "JWT authentication refresh tokens protect the backend API and rotate sessions safely.",
            "JWT authentication refresh tokens protect the backend API and rotate sessions safely. Extra note.",
            "JWT authentication refresh tokens protect the backend API and rotate sessions safely.",
            "JWT authentication refresh tokens protect the backend API and rotate sessions safely.",
        };

        return texts
            .Select(
                (text, index) => new DrawerRecord(
                    $"{wing}-{index}",
                    text,
                    new DrawerMetadata
                    {
                        Wing = wing,
                        Room = "src",
                        SourceFile = sourceFile,
                        ChunkIndex = index,
                    }))
            .ToArray();
    }
}
