using MemShack.Application.Chunking;
using MemShack.Application.Extraction;
using MemShack.Application.Mining;
using MemShack.Application.Normalization;
using MemShack.Core.Constants;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Mining;

[TestClass]
public sealed class ConversationMinerIntegrationTests
{
    [TestMethod]
    public async Task MinesConversationExchangesWithConvoMetadata()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile("chat.txt", """
            > What is memory?
            Memory is persistence.

            > Why does it matter?
            It enables continuity.

            > How do we build it?
            With structured storage.
            """);

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ConversationMiner(
            new TranscriptNormalizer(),
            new ConversationChunker(),
            new GeneralMemoryExtractor(),
            store);

        var result = await miner.MineAsync(temp.Root, wing: "test_convos");
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(result.DrawersFiled >= 2);
        Assert.All(drawers, drawer => Assert.Equal("convos", drawer.Metadata.IngestMode));
        Assert.All(drawers, drawer => Assert.Equal("exchange", drawer.Metadata.ExtractMode));
        Assert.All(drawers, drawer => Assert.Equal("test_convos", drawer.Metadata.Wing));
    }

    [TestMethod]
    public async Task MinesGeneralExtractModeIntoMemoryTypeRooms()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile("session.jsonl", """
            {"type":"session_meta","cwd":"C:\\dev\\mempalace"}
            {"type":"event_msg","payload":{"type":"user_message","message":"We decided to use SQLite because it keeps the first migration simple."}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"That sounds good. The bug is fixed now and it works."}}
            """);

        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var miner = new ConversationMiner(
            new TranscriptNormalizer(),
            new ConversationChunker(),
            new GeneralMemoryExtractor(),
            store);

        var result = await miner.MineAsync(temp.Root, wing: "test_general", extractMode: "general");
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(result.DrawersFiled > 0);
        Assert.Contains(drawers, drawer => drawer.Metadata.Room == "decision" || drawer.Metadata.Room == "milestone");
        Assert.All(drawers, drawer => Assert.Equal("general", drawer.Metadata.ExtractMode));
    }

    [TestMethod]
    public void ScanConversationFiles_SkipsMetaJsonAndIgnoredFolders()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile("chat.txt", "hello");
        temp.WriteFile("tool-results/result.json", "{}");
        temp.WriteFile("logs/chat.meta.json", "{}");
        temp.WriteFile("nested/session.jsonl", "{}");

        var files = ConversationMiner.ScanConversationFiles(temp.Root);

        Assert.Contains(files, file => file.EndsWith("chat.txt", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("session.jsonl", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("tool-results", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.EndsWith(".meta.json", StringComparison.Ordinal));
    }
}
