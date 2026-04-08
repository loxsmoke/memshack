namespace MemShack.Core.Models;

public sealed record PalaceGraphNode(
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    IReadOnlyList<string> Dates);
