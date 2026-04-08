namespace MemShack.Application.Compression;

public sealed record CompressionRunResult(
    int DrawersScanned,
    int DrawersCompressed,
    int TotalOriginalChars,
    int TotalCompressedChars,
    int TotalOriginalTokens,
    int TotalCompressedTokens,
    bool DryRun,
    IReadOnlyList<CompressedDrawerResult> Entries);
