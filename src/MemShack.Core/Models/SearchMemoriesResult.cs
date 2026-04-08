namespace MemShack.Core.Models;

public sealed record SearchMemoriesResult(
    string Query,
    SearchFilters Filters,
    IReadOnlyList<SearchHit> Results,
    string? Error = null);
