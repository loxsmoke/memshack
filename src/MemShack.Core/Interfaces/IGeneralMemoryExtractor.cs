using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IGeneralMemoryExtractor
{
    IReadOnlyList<ExtractedMemory> ExtractMemories(string text, double minConfidence = 0.3);
}
