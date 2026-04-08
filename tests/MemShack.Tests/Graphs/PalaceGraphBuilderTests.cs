using MemShack.Application.Graphs;
using MemShack.Core.Models;

namespace MemShack.Tests.Graphs;

[TestClass]
public sealed class PalaceGraphBuilderTests
{
    private readonly PalaceGraphBuilder _builder = new();

    [TestMethod]
    public void BuildGraph_CreatesNodesAndTunnelEdges()
    {
        var snapshot = _builder.BuildGraph(
        [
            new DrawerMetadata { Wing = "project", Room = "backend", Hall = "technical", Date = "2026-04-01" },
            new DrawerMetadata { Wing = "notes", Room = "backend", Hall = "technical", Date = "2026-04-02" },
            new DrawerMetadata { Wing = "notes", Room = "planning", Hall = "memory", Date = "2026-04-03" },
        ]);

        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Edges);
        Assert.Equal("backend", snapshot.Edges[0].Room);
    }

    [TestMethod]
    public void Traverse_ReturnsSuggestionsForMissingRoom()
    {
        var snapshot = _builder.BuildGraph(
        [
            new DrawerMetadata { Wing = "project", Room = "backend", Hall = "technical" },
            new DrawerMetadata { Wing = "notes", Room = "planning", Hall = "memory" },
        ]);

        var result = _builder.Traverse(snapshot, "back", maxHops: 2);

        Assert.False(result.Found);
        Assert.Contains("backend", result.Suggestions!);
    }

    [TestMethod]
    public void GraphStats_TracksTunnelRoomsPerWing()
    {
        var snapshot = _builder.BuildGraph(
        [
            new DrawerMetadata { Wing = "project", Room = "backend", Hall = "technical" },
            new DrawerMetadata { Wing = "notes", Room = "backend", Hall = "technical" },
            new DrawerMetadata { Wing = "project", Room = "frontend", Hall = "technical" },
        ]);

        var stats = _builder.GraphStats(snapshot);
        var tunnels = _builder.FindTunnels(snapshot);

        Assert.Equal(2, stats.TotalRooms);
        Assert.Equal(1, stats.TunnelRooms);
        Assert.Equal(2, stats.RoomsPerWing["project"]);
        Assert.Single(tunnels);
        Assert.Equal("backend", tunnels[0].Room);
    }
}
