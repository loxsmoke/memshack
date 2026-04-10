using System.Text.Json.Nodes;
using MemShack.Core.Models;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private async Task<object?> ToolKnowledgeGraphQueryAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var entity = SanitizeEntityName(arguments, "entity");
        var asOf = SanitizeOptionalIsoDate(arguments, "as_of");
        var direction = (GetOptionalString(arguments, "direction") ?? "both").Trim().ToLowerInvariant();
        if (direction is not ("outgoing" or "incoming" or "both"))
        {
            throw new InvalidOperationException("Invalid direction: expected outgoing, incoming, or both.");
        }

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
        var subject = SanitizeEntityName(arguments, "subject");
        var predicate = SanitizeBoundedText(GetRequiredString(arguments, "predicate"), "predicate", maxLength: 80, preserveNewlines: false);
        var @object = SanitizeEntityName(arguments, "object");
        var validFrom = SanitizeOptionalIsoDate(arguments, "valid_from");
        var sourceCloset = SanitizeOptionalEntityName(arguments, "source_closet");

        await AppendWriteAheadLogAsync(
            "kg_add",
            "intent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["subject"] = subject,
                ["predicate"] = predicate,
                ["object"] = @object,
                ["valid_from"] = validFrom,
                ["source_closet"] = sourceCloset,
            },
            cancellationToken);

        var existingTriple = await FindCurrentTripleAsync(subject, predicate, @object, cancellationToken);
        if (existingTriple is not null)
        {
            await AppendWriteAheadLogAsync(
                "kg_add",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["triple_id"] = existingTriple.Id,
                    ["subject"] = subject,
                    ["predicate"] = predicate,
                    ["object"] = @object,
                    ["reason"] = "already_exists",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_exists",
                ["triple_id"] = existingTriple.Id,
                ["fact"] = $"{subject} -> {predicate} -> {@object}",
            };
        }

        var tripleId = await _knowledgeGraphStore.AddTripleAsync(
            new TripleRecord(subject, predicate, @object, ValidFrom: validFrom, SourceCloset: sourceCloset),
            cancellationToken);

        await AppendWriteAheadLogAsync(
            "kg_add",
            "success",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["triple_id"] = tripleId,
                ["subject"] = subject,
                ["predicate"] = predicate,
                ["object"] = @object,
            },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["noop"] = false,
            ["triple_id"] = tripleId,
            ["fact"] = $"{subject} -> {predicate} -> {@object}",
        };
    }

    private async Task<object?> ToolKnowledgeGraphInvalidateAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var subject = SanitizeEntityName(arguments, "subject");
        var predicate = SanitizeBoundedText(GetRequiredString(arguments, "predicate"), "predicate", maxLength: 80, preserveNewlines: false);
        var @object = SanitizeEntityName(arguments, "object");
        var ended = SanitizeOptionalIsoDate(arguments, "ended");

        await AppendWriteAheadLogAsync(
            "kg_invalidate",
            "intent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["subject"] = subject,
                ["predicate"] = predicate,
                ["object"] = @object,
                ["ended"] = ended,
            },
            cancellationToken);

        var existingTriple = await FindCurrentTripleAsync(subject, predicate, @object, cancellationToken);
        if (existingTriple is null)
        {
            await AppendWriteAheadLogAsync(
                "kg_invalidate",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["subject"] = subject,
                    ["predicate"] = predicate,
                    ["object"] = @object,
                    ["ended"] = ended ?? "today",
                    ["reason"] = "already_invalidated",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_invalidated",
                ["fact"] = $"{subject} -> {predicate} -> {@object}",
                ["ended"] = ended ?? "today",
            };
        }

        await _knowledgeGraphStore.InvalidateAsync(subject, predicate, @object, ended, cancellationToken);

        await AppendWriteAheadLogAsync(
            "kg_invalidate",
            "success",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["triple_id"] = existingTriple.Id,
                ["subject"] = subject,
                ["predicate"] = predicate,
                ["object"] = @object,
                ["ended"] = ended ?? "today",
            },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["noop"] = false,
            ["fact"] = $"{subject} -> {predicate} -> {@object}",
            ["ended"] = ended ?? "today",
        };
    }

    private async Task<object?> ToolKnowledgeGraphTimelineAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var entity = SanitizeOptionalEntityName(arguments, "entity");
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
        var startRoom = SanitizeRequiredSlug(arguments, "start_room");
        var maxHops = ClampPositiveInt(GetInt(arguments, "max_hops", 2), 2, 5);

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
        var wingA = SanitizeOptionalNullableSlug(arguments, "wing_a");
        var wingB = SanitizeOptionalNullableSlug(arguments, "wing_b");

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
