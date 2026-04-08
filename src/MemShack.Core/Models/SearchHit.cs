namespace MemShack.Core.Models;

public sealed record SearchHit(
    string Text,
    string Wing,
    string Room,
    string SourceFile,
    double Similarity,
    IReadOnlyDictionary<string, object?>? Metadata = null);
