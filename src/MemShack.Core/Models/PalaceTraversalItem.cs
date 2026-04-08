namespace MemShack.Core.Models;

public sealed record PalaceTraversalItem(
    string Room,
    IReadOnlyList<string> Wings,
    IReadOnlyList<string> Halls,
    int Count,
    int Hop,
    IReadOnlyList<string>? ConnectedVia = null);
