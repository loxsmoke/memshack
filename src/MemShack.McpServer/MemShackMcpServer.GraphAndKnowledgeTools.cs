using System.Text.Json.Nodes;
using MemShack.Core.Models;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private async Task<object?> ToolKnowledgeGraphQueryAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var entity = GetRequiredString(arguments, "entity");
        var asOf = GetOptionalString(arguments, "as_of");
        var direction = GetOptionalString(arguments, "direction") ?? "both";
        var facts = await _knowledgeGraphStore.QueryEntityAsync(entity, asOf, direction, cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity"] = entity,
            ["as_of"] = asOf,
            ["facts"] = facts.Select(ToTripleDictionary).ToArray(),
            ["count"] = facts.Count,
        };
    }

    private async Task<object?> ToolKnowledgeGraphAddAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var subject = GetRequiredString(arguments, "subject");
        var predicate = GetRequiredString(arguments, "predicate");
        var @object = GetRequiredString(arguments, "object");
        var validFrom = GetOptionalString(arguments, "valid_from");
        var sourceCloset = GetOptionalString(arguments, "source_closet");

        var tripleId = await _knowledgeGraphStore.AddTripleAsync(
            new TripleRecord(subject, predicate, @object, ValidFrom: validFrom, SourceCloset: sourceCloset),
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["triple_id"] = tripleId,
            ["fact"] = $"{subject} -> {predicate} -> {@object}",
        };
    }

    private async Task<object?> ToolKnowledgeGraphInvalidateAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var subject = GetRequiredString(arguments, "subject");
        var predicate = GetRequiredString(arguments, "predicate");
        var @object = GetRequiredString(arguments, "object");
        var ended = GetOptionalString(arguments, "ended");

        await _knowledgeGraphStore.InvalidateAsync(subject, predicate, @object, ended, cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["fact"] = $"{subject} -> {predicate} -> {@object}",
            ["ended"] = ended ?? "today",
        };
    }

    private async Task<object?> ToolKnowledgeGraphTimelineAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var entity = GetOptionalString(arguments, "entity");
        var timeline = await _knowledgeGraphStore.TimelineAsync(entity, cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity"] = entity ?? "all",
            ["timeline"] = timeline.Select(ToTripleDictionary).ToArray(),
            ["count"] = timeline.Count,
        };
    }

    private async Task<object?> ToolKnowledgeGraphStatsAsync(JsonObject _, CancellationToken cancellationToken)
    {
        var stats = await _knowledgeGraphStore.StatsAsync(cancellationToken);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entities"] = stats.Entities,
            ["triples"] = stats.Triples,
            ["current_facts"] = stats.CurrentFacts,
            ["expired_facts"] = stats.ExpiredFacts,
            ["relationship_types"] = stats.RelationshipTypes,
        };
    }

    private async Task<object?> ToolTraverseAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var startRoom = GetRequiredString(arguments, "start_room");
        var maxHops = GetInt(arguments, "max_hops", 2);

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var graph = await BuildGraphSnapshotAsync(cancellationToken);
        var traversal = _graphBuilder.Traverse(graph, startRoom, maxHops);
        if (!traversal.Found)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = traversal.Error,
                ["suggestions"] = traversal.Suggestions ?? Array.Empty<string>(),
            };
        }

        return traversal.Results.Select(result =>
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["room"] = result.Room,
                ["wings"] = result.Wings,
                ["halls"] = result.Halls,
                ["count"] = result.Count,
                ["hop"] = result.Hop,
            };

            if (result.ConnectedVia is not null)
            {
                item["connected_via"] = result.ConnectedVia;
            }

            return item;
        }).ToArray();
    }

    private async Task<object?> ToolFindTunnelsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var wingA = GetOptionalString(arguments, "wing_a");
        var wingB = GetOptionalString(arguments, "wing_b");

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var graph = await BuildGraphSnapshotAsync(cancellationToken);
        return _graphBuilder.FindTunnels(graph, wingA, wingB)
            .Select(tunnel => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["room"] = tunnel.Room,
                ["wings"] = tunnel.Wings,
                ["halls"] = tunnel.Halls,
                ["count"] = tunnel.Count,
                ["recent"] = tunnel.Recent ?? string.Empty,
            })
            .ToArray();
    }

    private async Task<object?> ToolGraphStatsAsync(JsonObject _, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var graph = await BuildGraphSnapshotAsync(cancellationToken);
        var stats = _graphBuilder.GraphStats(graph);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_rooms"] = stats.TotalRooms,
            ["tunnel_rooms"] = stats.TunnelRooms,
            ["total_edges"] = stats.TotalEdges,
            ["rooms_per_wing"] = stats.RoomsPerWing,
            ["top_tunnels"] = stats.TopTunnels
                .Where(tunnel => tunnel.Wings.Count >= 2)
                .Select(tunnel => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["room"] = tunnel.Room,
                    ["wings"] = tunnel.Wings,
                    ["count"] = tunnel.Count,
                })
                .ToArray(),
        };
    }
}
