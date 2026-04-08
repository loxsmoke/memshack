using MemShack.Application.Chunking;

namespace MemShack.Tests.Chunking;

[TestClass]
public sealed class TextChunkerTests
{
    [TestMethod]
    public void ChunkText_SplitsLongContentOnParagraphBoundary()
    {
        var chunker = new TextChunker(chunkSize: 120, chunkOverlap: 20, minChunkSize: 20);
        var content = string.Join(
            "\n\n",
            Enumerable.Range(1, 4).Select(index => $"Paragraph {index}: " + new string('a', 70)));

        var chunks = chunker.ChunkText(content);

        Assert.True(chunks.Count >= 2);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.ChunkIndex));
        Assert.Contains("Paragraph 1", chunks[0].Content);
    }
}
