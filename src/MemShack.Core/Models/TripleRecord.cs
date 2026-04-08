namespace MemShack.Core.Models;

public sealed record TripleRecord(
    string Subject,
    string Predicate,
    string Object,
    string? ValidFrom = null,
    string? ValidTo = null,
    double Confidence = 1.0,
    string? SourceCloset = null,
    string? SourceFile = null,
    string? Id = null,
    string? Direction = null,
    bool Current = true);
