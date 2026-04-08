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

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_drawers"] = drawers.Count,
            ["wings"] = wings,
            ["rooms"] = rooms,
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

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["wings"] = wings,
        };
    }

    private async Task<object?> ToolListRoomsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var wing = GetOptionalString(arguments, "wing");
        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, wing, cancellationToken: cancellationToken);
        var rooms = drawers
            .GroupBy(drawer => drawer.Metadata.Room, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["wing"] = wing ?? "all",
            ["rooms"] = rooms,
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

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["taxonomy"] = taxonomy,
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
        var query = GetRequiredString(arguments, "query");
        var limit = GetInt(arguments, "limit", 5);
        var wing = GetOptionalString(arguments, "wing");
        var room = GetOptionalString(arguments, "room");

        var service = new MemorySearchService(_vectorStore, _config.PalacePath, _config.CollectionName);
        var result = await service.SearchMemoriesAsync(query, wing, room, limit, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = result.Error,
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
        var content = GetRequiredString(arguments, "content");
        var threshold = GetDouble(arguments, "threshold", 0.9);

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var matches = await FindDuplicateMatchesAsync(content, threshold, cancellationToken);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_duplicate"] = matches.Count > 0,
            ["matches"] = matches,
        };
    }

    private async Task<object?> ToolAddDrawerAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var wing = GetRequiredString(arguments, "wing");
        var room = GetRequiredString(arguments, "room");
        var content = GetRequiredString(arguments, "content");
        var sourceFile = GetOptionalString(arguments, "source_file") ?? string.Empty;
        var addedBy = GetOptionalString(arguments, "added_by") ?? "mcp";

        await _vectorStore.EnsureCollectionAsync(_config.CollectionName, cancellationToken);

        var duplicateMatches = await FindDuplicateMatchesAsync(content, 0.9, cancellationToken);
        if (duplicateMatches.Count > 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["reason"] = "duplicate",
                ["matches"] = duplicateMatches,
            };
        }

        var now = DateTime.Now;
        var drawerId = $"drawer_{wing}_{room}_{Md5Hex(content[..Math.Min(100, content.Length)] + now.ToString("O", CultureInfo.InvariantCulture))[..16]}";
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
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["error"] = $"Drawer already exists: {drawerId}",
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["drawer_id"] = drawerId,
            ["wing"] = wing,
            ["room"] = room,
        };
    }

    private async Task<object?> ToolDeleteDrawerAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var drawerId = GetRequiredString(arguments, "drawer_id");

        if (!await HasCollectionAsync(cancellationToken))
        {
            return NoPalace();
        }

        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        if (!drawers.Any(drawer => string.Equals(drawer.Id, drawerId, StringComparison.Ordinal)))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["success"] = false,
                ["error"] = $"Drawer not found: {drawerId}",
            };
        }

        var deleted = await _vectorStore.DeleteDrawerAsync(_config.CollectionName, drawerId, cancellationToken);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = deleted,
            ["drawer_id"] = drawerId,
        };
    }

    private async Task<object?> ToolDiaryWriteAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var agentName = GetRequiredString(arguments, "agent_name");
        var entry = GetRequiredString(arguments, "entry");
        var topic = GetOptionalString(arguments, "topic") ?? "general";

        await _vectorStore.EnsureCollectionAsync(_config.CollectionName, cancellationToken);

        var now = DateTime.Now;
        var wing = $"wing_{NormalizeAgentName(agentName)}";
        var entryId = $"diary_{wing}_{now:yyyyMMdd_HHmmss}_{Md5Hex(entry[..Math.Min(50, entry.Length)])[..8]}";

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
            throw new InvalidOperationException($"Diary entry already exists: {entryId}");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["success"] = true,
            ["entry_id"] = entryId,
            ["agent"] = agentName,
            ["topic"] = topic,
            ["timestamp"] = now.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private async Task<object?> ToolDiaryReadAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var agentName = GetRequiredString(arguments, "agent_name");
        var lastN = GetInt(arguments, "last_n", 10);

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
