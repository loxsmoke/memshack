using MemShack.Application.Compression;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Compression;

[TestClass]
public sealed class DrawerCompressionServiceTests
{
    [TestMethod]
    public async Task RunAsync_WritesCompressedDrawersAndStats()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_1",
                "We decided to simplify the MemShack architecture because the old database pipeline kept failing.",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = temp.GetPath("src", "memshack.txt"),
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-07T12:00:00",
                }));

        var service = new DrawerCompressionService(
            store,
            new AaakDialect(new Dictionary<string, string>(StringComparer.Ordinal) { ["MemShack"] = "MEM" }));

        var result = await service.RunAsync();
        var compressed = await store.GetDrawersAsync(CollectionNames.Compressed);

        Assert.Equal(1, result.DrawersScanned);
        Assert.Equal(1, result.DrawersCompressed);
        Assert.Single(compressed);
        Assert.Contains("MEM", compressed[0].Text);
        Assert.NotNull(compressed[0].Metadata.CompressionRatio);
        Assert.NotNull(compressed[0].Metadata.OriginalTokens);
        Assert.NotNull(compressed[0].Metadata.CompressedTokens);
    }

    [TestMethod]
    public void Compress_ExtractsTopicsAndFlags()
    {
        var dialect = new AaakDialect(new Dictionary<string, string>(StringComparer.Ordinal) { ["Riley"] = "RIL" });

        var compressed = dialect.Compress(
            "Riley decided to migrate the architecture because the database deploy kept breaking.");

        Assert.Contains("RIL", compressed);
        Assert.Contains("architecture", compressed);
        Assert.Contains("DECISION", compressed);
        Assert.Contains("TECHNICAL", compressed);
    }
}
