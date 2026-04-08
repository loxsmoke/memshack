namespace MemShack.Core.Models;

public sealed record MemoryLayerStatus(
    string? Path = null,
    bool? Exists = null,
    int? Tokens = null,
    string? Description = null);
