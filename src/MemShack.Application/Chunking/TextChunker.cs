using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Chunking;

public sealed class TextChunker : ITextChunker
{
    public const int DefaultChunkSize = 800;
    public const int DefaultChunkOverlap = 100;
    public const int DefaultMinChunkSize = 50;

    private readonly int _chunkSize;
    private readonly int _chunkOverlap;
    private readonly int _minChunkSize;

    public TextChunker(
        int chunkSize = DefaultChunkSize,
        int chunkOverlap = DefaultChunkOverlap,
        int minChunkSize = DefaultMinChunkSize)
    {
        _chunkSize = chunkSize;
        _chunkOverlap = chunkOverlap;
        _minChunkSize = minChunkSize;
    }

    public IReadOnlyList<TextChunk> ChunkText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var trimmed = content.Trim();
        var chunks = new List<TextChunk>();
        var start = 0;

        while (start < trimmed.Length)
        {
            var end = Math.Min(start + _chunkSize, trimmed.Length);

            if (end < trimmed.Length)
            {
                var midpoint = start + (_chunkSize / 2);
                var paragraphBoundary = trimmed.LastIndexOf("\n\n", end - 1, end - start, StringComparison.Ordinal);
                if (paragraphBoundary > midpoint)
                {
                    end = paragraphBoundary;
                }
                else
                {
                    var lineBoundary = trimmed.LastIndexOf('\n', end - 1, end - start);
                    if (lineBoundary > midpoint)
                    {
                        end = lineBoundary;
                    }
                }
            }

            if (end <= start)
            {
                end = Math.Min(start + _chunkSize, trimmed.Length);
            }

            var chunkContent = trimmed[start..end].Trim();
            if (chunkContent.Length >= _minChunkSize)
            {
                chunks.Add(new TextChunk(chunkContent, chunks.Count));
            }

            if (end >= trimmed.Length)
            {
                break;
            }

            start = Math.Max(end - _chunkOverlap, start + 1);
        }

        return chunks;
    }
}
