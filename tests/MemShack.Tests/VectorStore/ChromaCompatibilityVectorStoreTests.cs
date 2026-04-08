using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.VectorStore;

[TestClass]
public sealed class ChromaCompatibilityVectorStoreTests
{
    [TestMethod]
    public async Task PreservesAccessToNamedCollections()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));

        await store.EnsureCollectionAsync(CollectionNames.Drawers);
        await store.EnsureCollectionAsync(CollectionNames.Compressed);

        var added = await store.AddDrawerAsync(
            CollectionNames.Compressed,
            new DrawerRecord(
                "drawer_test_general_1",
                "compressed memory",
                new DrawerMetadata
                {
                    Wing = "test",
                    Room = "general",
                    SourceFile = temp.GetPath("memory.txt"),
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-07T12:00:00",
                }));

        var collections = await store.ListCollectionsAsync();
        var compressed = await store.GetDrawersAsync(CollectionNames.Compressed);

        Assert.True(added);
        Assert.Contains(CollectionNames.Drawers, collections);
        Assert.Contains(CollectionNames.Compressed, collections);
        Assert.Single(compressed);
    }

    [TestMethod]
    public async Task TracksSourceFilesAndSimpleSearch()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var sourceFile = temp.GetPath("src", "app.py");

        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_1",
                "JWT authentication tokens are stored with refresh cookies",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = sourceFile,
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-07T12:00:00",
                }));

        var hasSourceFile = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile);
        var search = await store.SearchAsync(CollectionNames.Drawers, "authentication jwt", 3, wing: "project");

        Assert.True(hasSourceFile);
        Assert.Single(search);
        Assert.Equal("backend", search[0].Room);
        Assert.True(search[0].Similarity > 0);
    }
}
