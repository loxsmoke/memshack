namespace MemShack.Application.Entities;

public sealed record EntityLookupResult(
    string Type,
    double Confidence,
    string Source,
    string Name,
    bool NeedsDisambiguation,
    IReadOnlyList<string>? Context = null,
    string? DisambiguatedBy = null);
