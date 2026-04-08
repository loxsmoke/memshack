using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Chunking;

public sealed class ConversationChunker : IConversationChunker
{
    private const int MinChunkSize = 30;

    public IReadOnlyList<TextChunk> ChunkExchanges(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = content.Split('\n');
        var quoteLineCount = lines.Count(line => line.TrimStart().StartsWith('>'));

        return quoteLineCount >= 3
            ? ChunkByExchange(lines)
            : ChunkByParagraph(content);
    }

    private static IReadOnlyList<TextChunk> ChunkByExchange(IReadOnlyList<string> lines)
    {
        var chunks = new List<TextChunk>();
        var index = 0;

        while (index < lines.Count)
        {
            var line = lines[index];
            if (!line.TrimStart().StartsWith('>'))
            {
                index++;
                continue;
            }

            var userTurn = line.Trim();
            index++;

            var assistantLines = new List<string>();
            while (index < lines.Count)
            {
                var nextLine = lines[index];
                var trimmed = nextLine.Trim();
                if (trimmed.StartsWith('>') || trimmed.StartsWith("---", StringComparison.Ordinal))
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    assistantLines.Add(trimmed);
                }

                index++;
            }

            var assistantResponse = string.Join(' ', assistantLines.Take(8));
            var chunkContent = string.IsNullOrWhiteSpace(assistantResponse)
                ? userTurn
                : $"{userTurn}\n{assistantResponse}";

            if (chunkContent.Trim().Length > MinChunkSize)
            {
                chunks.Add(new TextChunk(chunkContent, chunks.Count));
            }
        }

        return chunks;
    }

    private static IReadOnlyList<TextChunk> ChunkByParagraph(string content)
    {
        var chunks = new List<TextChunk>();
        var paragraphs = content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        if (paragraphs.Count <= 1 && content.Count(character => character == '\n') > 20)
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i += 25)
            {
                var group = string.Join('\n', lines.Skip(i).Take(25)).Trim();
                if (group.Length > MinChunkSize)
                {
                    chunks.Add(new TextChunk(group, chunks.Count));
                }
            }

            return chunks;
        }

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > MinChunkSize)
            {
                chunks.Add(new TextChunk(paragraph, chunks.Count));
            }
        }

        return chunks;
    }
}
