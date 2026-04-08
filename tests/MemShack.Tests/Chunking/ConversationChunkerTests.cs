using MemShack.Application.Chunking;

namespace MemShack.Tests.Chunking;

[TestClass]
public sealed class ConversationChunkerTests
{
    [TestMethod]
    public void ChunkExchanges_UsesQuotedTurnsWhenPresent()
    {
        var chunker = new ConversationChunker();
        var content = """
            > What is memory?
            Memory is the persistence that lets a system keep working across time and state changes.

            > Why does it matter?
            It enables continuity, planning, and reliable retrieval later on.

            > How do we build it?
            We build it with structure, indexing, and stable contracts.
            """;

        var chunks = chunker.ChunkExchanges(content);

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("> What is memory?", chunks[0].Content);
        Assert.Contains("continuity", chunks[1].Content);
    }

    [TestMethod]
    public void ChunkExchanges_FallsBackToParagraphs()
    {
        var chunker = new ConversationChunker();
        var content = """
            This paragraph is long enough to be treated as a standalone chunk because it contains more than the minimum number of characters.

            This is a second paragraph with enough detail to become its own chunk as well, which lets us verify the fallback logic cleanly.
            """;

        var chunks = chunker.ChunkExchanges(content);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
    }
}
