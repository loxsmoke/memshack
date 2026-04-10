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

    [TestMethod]
    public async Task SourceFileChecksRespectEmbeddingSignatureAndCanDeleteStaleEntries()
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
                    EmbeddingSignature = "legacy-signature",
                }));

        var hasLegacy = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, "legacy-signature");
        var hasCurrent = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, EmbeddingSignatures.Current);
        var deleted = await store.DeleteSourceFileAsync(CollectionNames.Drawers, sourceFile);

        Assert.True(hasLegacy);
        Assert.False(hasCurrent);
        Assert.True(deleted);
        Assert.Empty(await store.GetDrawersAsync(CollectionNames.Drawers));
    }

    [TestMethod]
    public async Task SourceFileChecksRespectStoredSourceMtime()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var sourceFile = temp.GetPath("src", "app.py");
        var sourceMtimeUtcMs = 1_744_156_800_000L;

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
                    SourceMtimeUtcMs = sourceMtimeUtcMs,
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-07T12:00:00",
                    EmbeddingSignature = EmbeddingSignatures.Current,
                }));

        var hasMatchingMtime = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, EmbeddingSignatures.Current, sourceMtimeUtcMs);
        var hasStaleMtime = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, EmbeddingSignatures.Current, sourceMtimeUtcMs + 1);

        Assert.True(hasMatchingMtime);
        Assert.False(hasStaleMtime);
    }

    [TestMethod]
    public async Task SearchAndEnumeration_StayStableAcrossLargerCollections()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));

        for (var index = 0; index < 60; index++)
        {
            var room = index % 2 == 0 ? "backend" : "docs";
            var text = index == 42
                ? "needle keyword unique phrase for ranked retrieval"
                : $"background context chunk {index} with general project notes";
            await store.AddDrawerAsync(
                CollectionNames.Drawers,
                new DrawerRecord(
                    $"drawer_project_{room}_{index}",
                    text,
                    new DrawerMetadata
                    {
                        Wing = "project",
                        Room = room,
                        SourceFile = temp.GetPath("src", $"file-{index}.txt"),
                        ChunkIndex = index,
                        AddedBy = "test",
                        FiledAt = "2026-04-09T10:00:00",
                    }));
        }

        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers, wing: "project");
        var search = await store.SearchAsync(CollectionNames.Drawers, "needle keyword unique phrase", 5, wing: "project");

        Assert.Equal(60, drawers.Count);
        Assert.True(search.Count <= 5);
        Assert.NotEmpty(search);
        Assert.Equal(Path.GetFileName(temp.GetPath("src", "file-42.txt")), Path.GetFileName(search[0].SourceFile));
    }
}
