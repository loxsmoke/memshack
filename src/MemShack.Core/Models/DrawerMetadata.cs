namespace MemShack.Core.Models;

public sealed record DrawerMetadata
{
    public string Wing { get; init; } = string.Empty;

    public string Room { get; init; } = string.Empty;

    public string SourceFile { get; init; } = string.Empty;

    public long? SourceMtimeUtcMs { get; init; }

    public int ChunkIndex { get; init; }

    public string AddedBy { get; init; } = string.Empty;

    public string FiledAt { get; init; } = string.Empty;

    public string? EmbeddingSignature { get; init; }

    public string? IngestMode { get; init; }

    public string? ExtractMode { get; init; }

    public string? Hall { get; init; }

    public string? Topic { get; init; }

    public string? Type { get; init; }

    public string? Agent { get; init; }

    public string? Date { get; init; }

    public double? Importance { get; init; }

    public double? EmotionalWeight { get; init; }

    public double? Weight { get; init; }

    public double? CompressionRatio { get; init; }

    public int? OriginalTokens { get; init; }

    public int? CompressedTokens { get; init; }
}
