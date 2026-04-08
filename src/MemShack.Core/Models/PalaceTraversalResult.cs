namespace MemShack.Core.Models;

public sealed record PalaceTraversalResult(
    bool Found,
    IReadOnlyList<PalaceTraversalItem> Results,
    string? Error = null,
    IReadOnlyList<string>? Suggestions = null);
