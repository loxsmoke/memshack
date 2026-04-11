using System.Globalization;
using System.Text.Json.Nodes;
using MemShack.Application.Search;
using MemShack.Core.Models;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private async Task<object?> ToolStatusAsync(JsonObject _, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        var wings = drawers
            .GroupBy(drawer => drawer.Metadata.Wing, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rooms = drawers
            .GroupBy(drawer => drawer.Metadata.Room, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var limitedWings = LimitCounts(wings, MaxGroupedCountBuckets, out var wingsTruncated);
        var limitedRooms = LimitCounts(rooms, MaxGroupedCountBuckets, out var roomsTruncated);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_drawers"] = drawers.Count,
            ["wings"] = limitedWings,
            ["rooms"] = limitedRooms,
            ["wings_truncated"] = wingsTruncated,
            ["rooms_truncated"] = roomsTruncated,
            ["palace_path"] = _config.PalacePath,
            ["protocol"] = PalaceProtocol,
            ["aaak_dialect"] = AaakSpec,
        };
    }

    private async Task<object?> ToolListWingsAsync(JsonObject _, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        var wings = drawers
            .GroupBy(drawer => drawer.Metadata.Wing, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var limitedWings = LimitCounts(wings, MaxGroupedCountBuckets, out var truncated);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["wings"] = limitedWings,
            ["truncated"] = truncated,
        };
    }

    private async Task<object?> ToolListRoomsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var wing = SanitizeOptionalNullableSlug(arguments, "wing");
        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, wing, cancellationToken: cancellationToken);
        var rooms = drawers
            .GroupBy(drawer => drawer.Metadata.Room, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var limitedRooms = LimitCounts(rooms, MaxGroupedCountBuckets, out var truncated);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["wing"] = wing ?? "all",
            ["rooms"] = limitedRooms,
            ["truncated"] = truncated,
        };
    }

    private async Task<object?> ToolGetTaxonomyAsync(JsonObject _, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        var taxonomy = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var drawer in drawers)
        {
            if (!taxonomy.TryGetValue(drawer.Metadata.Wing, out var rooms))
            {
                rooms = new Dictionary<string, int>(StringComparer.Ordinal);
                taxonomy[drawer.Metadata.Wing] = rooms;
            }

            rooms[drawer.Metadata.Room] = rooms.TryGetValue(drawer.Metadata.Room, out var count)
                ? count + 1
                : 1;
        }

        var limitedTaxonomy = LimitTaxonomy(taxonomy, out var wingsTruncated, out var omittedRoomGroups);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["taxonomy"] = limitedTaxonomy,
            ["wings_truncated"] = wingsTruncated,
            ["omitted_room_groups"] = omittedRoomGroups,
        };
    }

    private Task<object?> ToolGetAaakSpecAsync(JsonObject _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        object result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["aaak_spec"] = AaakSpec,
        };

        return Task.FromResult<object?>(result);
    }

    private async Task<object?> ToolSearchAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = SanitizeContent(arguments, "query");
        var limit = ClampPositiveInt(GetInt(arguments, "limit", 5), 5, 25);
        var wing = SanitizeOptionalNullableSlug(arguments, "wing");
        var room = SanitizeOptionalNullableSlug(arguments, "room");

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var service = new MemorySearchService(_vectorStore, _config.PalacePath, _config.CollectionName);
        var result = await service.SearchMemoriesAsync(query, wing, room, limit, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = result.Error,
                ["query"] = query,
                ["filters"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["wing"] = wing,
                    ["room"] = room,
                },
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = result.Query,
            ["filters"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["wing"] = wing,
                ["room"] = room,
            },
            ["results"] = result.Results.Select(hit => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = hit.Text,
                ["wing"] = hit.Wing,
                ["room"] = hit.Room,
                ["source_file"] = hit.SourceFile,
                ["similarity"] = hit.Similarity,
            }).ToArray(),
        };
    }

    private async Task<object?> ToolCheckDuplicateAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var content = SanitizeContent(arguments, "content");
        var threshold = ClampThreshold(GetDouble(arguments, "threshold", 0.9));

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var matches = await FindDuplicateMatchesAsync(content, threshold, cancellationToken);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_duplicate"] = matches.Count > 0,
            ["threshold"] = threshold,
            ["threshold_kind"] = "similarity",
            ["matches"] = matches,
        };
    }

    private async Task<object?> ToolAddDrawerAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var wing = SanitizeRequiredSlug(arguments, "wing");
        var room = SanitizeRequiredSlug(arguments, "room");
        var content = SanitizeContent(arguments, "content");
        var sourceFile = SanitizeOptionalSourceFile(arguments) ?? string.Empty;
        var addedBy = SanitizeAddedBy(arguments);
        var drawerId = CreateDeterministicDrawerId(wing, room, content, sourceFile);

        await _vectorStore.EnsureCollectionAsync(_config.CollectionName, cancellationToken);
        await AppendWriteAheadLogAsync(
            "add_drawer",
            "intent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["drawer_id"] = drawerId,
                ["wing"] = wing,
                ["room"] = room,
                ["source_file"] = sourceFile,
                ["added_by"] = addedBy,
                ["content_sha256"] = Sha256Hex(content),
            },
            cancellationToken);

        var existingDrawer = await FindDrawerByIdAsync(drawerId, cancellationToken);
        if (existingDrawer is not null)
        {
            await AppendWriteAheadLogAsync(
                "add_drawer",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["drawer_id"] = drawerId,
                    ["wing"] = wing,
                    ["room"] = room,
                    ["reason"] = "already_exists",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_exists",
                ["drawer_id"] = drawerId,
                ["wing"] = wing,
                ["room"] = room,
            };
        }

        var duplicateMatches = await FindDuplicateMatchesAsync(content, 0.9, cancellationToken);
        if (duplicateMatches.Count > 0)
        {
            await AppendWriteAheadLogAsync(
                "add_drawer",
                "duplicate",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["drawer_id"] = drawerId,
                    ["wing"] = wing,
                    ["room"] = room,
                    ["match_count"] = duplicateMatches.Count,
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["reason"] = "duplicate",
                ["drawer_id"] = drawerId,
                ["matches"] = duplicateMatches,
            };
        }

        var now = DateTime.Now;
        var drawer = new DrawerRecord(
            drawerId,
            content,
            new DrawerMetadata
            {
                Wing = wing,
                Room = room,
                SourceFile = sourceFile,
                ChunkIndex = 0,
                AddedBy = addedBy,
                FiledAt = now.ToString("O", CultureInfo.InvariantCulture),
            });

        var added = await _vectorStore.AddDrawerAsync(_config.CollectionName, drawer, cancellationToken);
        if (!added)
        {
            await AppendWriteAheadLogAsync(
                "add_drawer",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["drawer_id"] = drawerId,
                    ["wing"] = wing,
                    ["room"] = room,
                    ["reason"] = "already_exists",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_exists",
                ["drawer_id"] = drawerId,
                ["wing"] = wing,
                ["room"] = room,
            };
        }

        await AppendWriteAheadLogAsync(
            "add_drawer",
            "success",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["drawer_id"] = drawerId,
                ["wing"] = wing,
                ["room"] = room,
            },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["noop"] = false,
            ["drawer_id"] = drawerId,
            ["wing"] = wing,
            ["room"] = room,
        };
    }

    private async Task<object?> ToolDeleteDrawerAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var drawerId = SanitizeBoundedText(GetRequiredString(arguments, "drawer_id"), "drawer_id", maxLength: 200, preserveNewlines: false);

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        await AppendWriteAheadLogAsync(
            "delete_drawer",
            "intent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["drawer_id"] = drawerId,
            },
            cancellationToken);

        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        if (!drawers.Any(drawer => string.Equals(drawer.Id, drawerId, StringComparison.Ordinal)))
        {
            await AppendWriteAheadLogAsync(
                "delete_drawer",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["drawer_id"] = drawerId,
                    ["reason"] = "not_found",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["error"] = $"Drawer not found: {drawerId}",
            };
        }

        var deleted = await _vectorStore.DeleteDrawerAsync(_config.CollectionName, drawerId, cancellationToken);
        await AppendWriteAheadLogAsync(
            "delete_drawer",
            deleted ? "success" : "noop",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["drawer_id"] = drawerId,
                ["reason"] = deleted ? null : "not_found",
            },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = deleted,
            ["drawer_id"] = drawerId,
        };
    }

    private async Task<object?> ToolDiaryWriteAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var agentName = SanitizeAgentName(arguments);
        var entry = SanitizeContent(arguments, "entry");
        var topic = SanitizeOptionalSlug(arguments, "topic", "general");

        await _vectorStore.EnsureCollectionAsync(_config.CollectionName, cancellationToken);

        var now = DateTime.Now;
        var wing = $"wing_{NormalizeAgentName(agentName)}";
        var entryId = CreateDeterministicDiaryEntryId(wing, topic, entry);
        await AppendWriteAheadLogAsync(
            "diary_write",
            "intent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["entry_id"] = entryId,
                ["agent"] = agentName,
                ["wing"] = wing,
                ["topic"] = topic,
                ["content_sha256"] = Sha256Hex(entry),
            },
            cancellationToken);

        var existingEntry = await FindDrawerByIdAsync(entryId, cancellationToken);
        if (existingEntry is not null)
        {
            await AppendWriteAheadLogAsync(
                "diary_write",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["entry_id"] = entryId,
                    ["agent"] = agentName,
                    ["wing"] = wing,
                    ["topic"] = topic,
                    ["reason"] = "already_exists",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_exists",
                ["entry_id"] = entryId,
                ["agent"] = agentName,
                ["topic"] = topic,
                ["timestamp"] = existingEntry.Metadata.FiledAt,
            };
        }

        var drawer = new DrawerRecord(
            entryId,
            entry,
            new DrawerMetadata
            {
                Wing = wing,
                Room = "diary",
                SourceFile = string.Empty,
                ChunkIndex = 0,
                AddedBy = "mcp",
                FiledAt = now.ToString("O", CultureInfo.InvariantCulture),
                Hall = "hall_diary",
                Topic = topic,
                Type = "diary_entry",
                Agent = agentName,
                Date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });

        var added = await _vectorStore.AddDrawerAsync(_config.CollectionName, drawer, cancellationToken);
        if (!added)
        {
            await AppendWriteAheadLogAsync(
                "diary_write",
                "noop",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["entry_id"] = entryId,
                    ["agent"] = agentName,
                    ["wing"] = wing,
                    ["topic"] = topic,
                    ["reason"] = "already_exists",
                },
                cancellationToken);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = true,
                ["noop"] = true,
                ["reason"] = "already_exists",
                ["entry_id"] = entryId,
                ["agent"] = agentName,
                ["topic"] = topic,
                ["timestamp"] = now.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        await AppendWriteAheadLogAsync(
            "diary_write",
            "success",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["entry_id"] = entryId,
                ["agent"] = agentName,
                ["wing"] = wing,
                ["topic"] = topic,
            },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["noop"] = false,
            ["entry_id"] = entryId,
            ["agent"] = agentName,
            ["topic"] = topic,
            ["timestamp"] = now.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private async Task<object?> ToolDiaryReadAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var agentName = SanitizeAgentName(arguments);
        var lastN = ClampPositiveInt(GetInt(arguments, "last_n", 10), 10, MaxDiaryReadEntries);

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var wing = $"wing_{NormalizeAgentName(agentName)}";
        var entries = await _vectorStore.GetDrawersAsync(
            _config.CollectionName,
            wing,
            "diary",
            cancellationToken);

        if (entries.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agent"] = agentName,
                ["entries"] = Array.Empty<object>(),
                ["message"] = "No diary entries yet.",
            };
        }

        var recentEntries = entries
            .OrderByDescending(drawer => drawer.Metadata.FiledAt, StringComparer.Ordinal)
            .Take(lastN)
            .Select(drawer => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["date"] = drawer.Metadata.Date ?? string.Empty,
                ["timestamp"] = drawer.Metadata.FiledAt,
                ["topic"] = drawer.Metadata.Topic ?? string.Empty,
                ["content"] = drawer.Text,
            })
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agent"] = agentName,
            ["entries"] = recentEntries,
            ["total"] = entries.Count,
            ["showing"] = recentEntries.Length,
        };
    }
}
