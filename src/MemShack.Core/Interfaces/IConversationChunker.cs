using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IConversationChunker
{
    IReadOnlyList<TextChunk> ChunkExchanges(string content);
}
