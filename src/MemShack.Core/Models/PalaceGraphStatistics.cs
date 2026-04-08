namespace MemShack.Core.Models;

public sealed record PalaceGraphStatistics(
    int TotalRooms,
    int TunnelRooms,
    int TotalEdges,
    IReadOnlyDictionary<string, int> RoomsPerWing,
    IReadOnlyList<PalaceTunnel> TopTunnels);
