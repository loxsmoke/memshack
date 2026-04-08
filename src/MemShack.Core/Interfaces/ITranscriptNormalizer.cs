namespace MemShack.Core.Interfaces;

public interface ITranscriptNormalizer
{
    string NormalizeFromFile(string filePath);

    string NormalizeContent(string content, string? extension = null);
}
