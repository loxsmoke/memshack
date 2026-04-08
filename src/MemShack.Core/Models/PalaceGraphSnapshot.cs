namespace MemShack.Core.Models;

public sealed record PalaceGraphSnapshot(
    IReadOnlyDictionary<string, PalaceGraphNode> Nodes,
    IReadOnlyList<PalaceGraphEdge> Edges);
