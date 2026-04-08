using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IPalaceGraphBuilder
{
    PalaceGraphSnapshot BuildGraph(IEnumerable<DrawerMetadata> metadata);

    PalaceTraversalResult Traverse(PalaceGraphSnapshot snapshot, string startRoom, int maxHops = 2);

    IReadOnlyList<PalaceTunnel> FindTunnels(PalaceGraphSnapshot snapshot, string? wingA = null, string? wingB = null);

    PalaceGraphStatistics GraphStats(PalaceGraphSnapshot snapshot);
}
