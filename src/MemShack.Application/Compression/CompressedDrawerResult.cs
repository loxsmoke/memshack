namespace MemShack.Application.Compression;

public sealed record CompressedDrawerResult(
    string DrawerId,
    string Wing,
    string Room,
    string SourceFile,
    string CompressedText,
    AaakCompressionStats Stats);
