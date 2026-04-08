using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface ITextChunker
{
    IReadOnlyList<TextChunk> ChunkText(string content);
}
