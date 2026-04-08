namespace MemShack.Application.Compression;

public sealed record AaakCompressionStats(
    int OriginalTokens,
    int CompressedTokens,
    double Ratio,
    int OriginalChars,
    int CompressedChars);
