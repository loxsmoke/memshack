using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Graphs;

public sealed class PalaceGraphBuilder : IPalaceGraphBuilder
{
    public PalaceGraphSnapshot BuildGraph(IEnumerable<DrawerMetadata> metadata)
    {
        var roomData = new Dictionary<string, MutableRoomData>(StringComparer.Ordinal);

        foreach (var item in metadata)
        {
            if (string.IsNullOrWhiteSpace(item.Room) ||
                string.Equals(item.Room, "general", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Wing))
            {
                continue;
            }

            if (!roomData.TryGetValue(item.Room, out var data))
            {
                data = new MutableRoomData();
                roomData[item.Room] = data;
            }

            data.Wings.Add(item.Wing);
            if (!string.IsNullOrWhiteSpace(item.Hall))
            {
                data.Halls.Add(item.Hall);
            }

            if (!string.IsNullOrWhiteSpace(item.Date))
            {
                data.Dates.Add(item.Date);
            }

            data.Count++;
        }

        var edges = new List<PalaceGraphEdge>();
        foreach (var (room, data) in roomData)
        {
            var wings = data.Wings.Order(StringComparer.Ordinal).ToArray();
            if (wings.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < wings.Length; i++)
            {
                for (var j = i + 1; j < wings.Length; j++)
                {
                    foreach (var hall in data.Halls.Order(StringComparer.Ordinal))
                    {
                        edges.Add(new PalaceGraphEdge(room, wings[i], wings[j], hall, data.Count));
                    }
                }
            }
        }

        var nodes = roomData.ToDictionary(
            pair => pair.Key,
            pair => new PalaceGraphNode(
                pair.Value.Wings.Order(StringComparer.Ordinal).ToArray(),
                pair.Value.Halls.Order(StringComparer.Ordinal).ToArray(),
                pair.Value.Count,
                pair.Value.Dates.Order(StringComparer.Ordinal).TakeLast(5).ToArray()),
            StringComparer.Ordinal);

        return new PalaceGraphSnapshot(nodes, edges);
    }

    public PalaceTraversalResult Traverse(PalaceGraphSnapshot snapshot, string startRoom, int maxHops = 2)
    {
        if (!snapshot.Nodes.TryGetValue(startRoom, out var start))
        {
            return new PalaceTraversalResult(
                false,
                [],
                $"Room '{startRoom}' not found",
                FuzzyMatch(startRoom, snapshot.Nodes));
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { startRoom };
        var results = new List<PalaceTraversalItem>
        {
            new(startRoom, start.Wings, start.Halls, start.Count, 0),
        };

        var frontier = new Queue<(string Room, int Depth)>();
        frontier.Enqueue((startRoom, 0));

        while (frontier.Count > 0)
        {
            var (currentRoom, depth) = frontier.Dequeue();
            if (depth >= maxHops)
            {
                continue;
            }

            var current = snapshot.Nodes[currentRoom];
            var currentWings = current.Wings.ToHashSet(StringComparer.Ordinal);

            foreach (var (room, data) in snapshot.Nodes)
            {
                if (visited.Contains(room))
                {
                    continue;
                }

                var sharedWings = data.Wings.Where(currentWings.Contains).Order(StringComparer.Ordinal).ToArray();
                if (sharedWings.Length == 0)
                {
                    continue;
                }

                visited.Add(room);
                results.Add(new PalaceTraversalItem(room, data.Wings, data.Halls, data.Count, depth + 1, sharedWings));

                if (depth + 1 < maxHops)
                {
                    frontier.Enqueue((room, depth + 1));
                }
            }
        }

        var ordered = results
            .OrderBy(item => item.Hop)
            .ThenByDescending(item => item.Count)
            .Take(50)
            .ToArray();

        return new PalaceTraversalResult(true, ordered);
    }

    public IReadOnlyList<PalaceTunnel> FindTunnels(PalaceGraphSnapshot snapshot, string? wingA = null, string? wingB = null)
    {
        return snapshot.Nodes
            .Where(pair => pair.Value.Wings.Count >= 2)
            .Where(pair => wingA is null || pair.Value.Wings.Contains(wingA))
            .Where(pair => wingB is null || pair.Value.Wings.Contains(wingB))
            .Select(pair => new PalaceTunnel(
                pair.Key,
                pair.Value.Wings,
                pair.Value.Halls,
                pair.Value.Count,
                pair.Value.Dates.LastOrDefault()))
            .OrderByDescending(tunnel => tunnel.Count)
            .Take(50)
            .ToArray();
    }

    public PalaceGraphStatistics GraphStats(PalaceGraphSnapshot snapshot)
    {
        var roomsPerWing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes.Values)
        {
            foreach (var wing in node.Wings)
            {
                roomsPerWing[wing] = roomsPerWing.TryGetValue(wing, out var count)
                    ? count + 1
                    : 1;
            }
        }

        var topTunnels = snapshot.Nodes
            .Where(pair => pair.Value.Wings.Count >= 2)
            .OrderByDescending(pair => pair.Value.Wings.Count)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(pair => new PalaceTunnel(pair.Key, pair.Value.Wings, pair.Value.Halls, pair.Value.Count))
            .ToArray();

        return new PalaceGraphStatistics(
            snapshot.Nodes.Count,
            snapshot.Nodes.Values.Count(node => node.Wings.Count >= 2),
            snapshot.Edges.Count,
            roomsPerWing.OrderByDescending(pair => pair.Value).ToDictionary(),
            topTunnels);
    }

    private static IReadOnlyList<string> FuzzyMatch(string query, IReadOnlyDictionary<string, PalaceGraphNode> nodes)
    {
        var queryLower = query.ToLowerInvariant();
        return nodes.Keys
            .Select(room =>
            {
                if (room.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                {
                    return new ScoredCandidate(room, 1.0);
                }

                var partialMatch = queryLower
                    .Split('-', StringSplitOptions.RemoveEmptyEntries)
                    .Any(word => room.Contains(word, StringComparison.OrdinalIgnoreCase));

                return partialMatch ? new ScoredCandidate(room, 0.5) : new ScoredCandidate(room, 0.0);
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Room, StringComparer.Ordinal)
            .Take(5)
            .Select(candidate => candidate.Room)
            .ToArray();
    }

    private sealed class MutableRoomData
    {
        public HashSet<string> Wings { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Halls { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> Dates { get; } = new(StringComparer.Ordinal);

        public int Count { get; set; }
    }

    private sealed record ScoredCandidate(string Room, double Score);
}
