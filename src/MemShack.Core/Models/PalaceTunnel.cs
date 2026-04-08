namespace MemShack.Core.Models;

public sealed record PalaceTunnel(
    string Room,
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    string? Recent = null);
