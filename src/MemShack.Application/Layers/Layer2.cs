using MemShack.Core.Constants;
using MemShack.Core.Interfaces;

namespace MemShack.Application.Layers;

public sealed class Layer2
{
    private readonly string _collectionName;
    private readonly IVectorStore _vectorStore;

    public Layer2(IVectorStore vectorStore, string collectionName = CollectionNames.Drawers)
    {
        _vectorStore = vectorStore;
        _collectionName = collectionName;
    }

    public async Task<string> RetrieveAsync(
        string? wing = null,
        string? room = null,
        int nResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (!await HasPalaceAsync(cancellationToken))
        {
            return "No palace found.";
        }

        var drawers = await _vectorStore.GetDrawersAsync(_collectionName, wing, room, cancellationToken);
        if (drawers.Count == 0)
        {
            var label = BuildFilterLabel(wing, room);
            return label.Length > 0 ? $"No drawers found for {label}." : "No drawers found.";
        }

        var lines = new List<string> { $"## L2 - ON-DEMAND ({drawers.Count} drawers)" };
        foreach (var drawer in drawers.Take(nResults))
        {
            var roomName = drawer.Metadata.Room;
            var source = Path.GetFileName(drawer.Metadata.SourceFile);
            var snippet = drawer.Text.Trim().Replace("\n", " ", StringComparison.Ordinal);
            if (snippet.Length > 300)
            {
                snippet = snippet[..297] + "...";
            }

            var entry = $"  [{roomName}] {snippet}";
            if (!string.IsNullOrWhiteSpace(source))
            {
                entry += $"  ({source})";
            }

            lines.Add(entry);
        }

        return string.Join('\n', lines);
    }

    private async Task<bool> HasPalaceAsync(CancellationToken cancellationToken)
    {
        var collections = await _vectorStore.ListCollectionsAsync(cancellationToken);
        return collections.Contains(_collectionName, StringComparer.Ordinal);
    }

    private static string BuildFilterLabel(string? wing, string? room)
    {
        var label = string.Empty;
        if (!string.IsNullOrWhiteSpace(wing))
        {
            label = $"wing={wing}";
        }

        if (!string.IsNullOrWhiteSpace(room))
        {
            label = label.Length > 0 ? $"{label} room={room}" : $"room={room}";
        }

        return label;
    }
}
