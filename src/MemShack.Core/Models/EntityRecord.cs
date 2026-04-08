namespace MemShack.Core.Models;

public sealed record EntityRecord(
    string Name,
    string Type = "unknown",
    IReadOnlyDictionary<string, string>? Properties = null,
    string? Id = null,
    string? CreatedAt = null);
