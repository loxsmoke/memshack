using MemShack.Core.Constants;
using MemShack.Core.Interfaces;

namespace MemShack.Application.Layers;

public sealed class Layer1
{
    public const int MaxDrawers = 15;
    public const int MaxChars = 3200;

    private readonly string _collectionName;
    private readonly IVectorStore _vectorStore;

    public Layer1(IVectorStore vectorStore, string collectionName = CollectionNames.Drawers)
    {
        _vectorStore = vectorStore;
        _collectionName = collectionName;
    }

    public async Task<string> GenerateAsync(string? wing = null, CancellationToken cancellationToken = default)
    {
        if (!await HasPalaceAsync(cancellationToken))
        {
            return "## L1 - No palace found. Run: mempalace mine <dir>";
        }

        var drawers = await _vectorStore.GetDrawersAsync(_collectionName, wing, cancellationToken: cancellationToken);
        if (drawers.Count == 0)
        {
            return "## L1 - No memories yet.";
        }

        var scored = drawers
            .Select(drawer => (Score: GetImportance(drawer.Metadata), Drawer: drawer))
            .OrderByDescending(item => item.Score)
            .Take(MaxDrawers)
            .ToArray();

        var byRoom = scored
            .GroupBy(item => item.Drawer.Metadata.Room, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        var lines = new List<string> { "## L1 - ESSENTIAL STORY" };
        var totalLength = 0;

        foreach (var roomEntries in byRoom)
        {
            var roomLine = $"\n[{roomEntries.Key}]";
            lines.Add(roomLine);
            totalLength += roomLine.Length;

            foreach (var (_, drawer) in roomEntries)
            {
                var source = Path.GetFileName(drawer.Metadata.SourceFile);
                var snippet = drawer.Text.Trim().Replace("\n", " ", StringComparison.Ordinal);
                if (snippet.Length > 200)
                {
                    snippet = snippet[..197] + "...";
                }

                var entryLine = $"  - {snippet}";
                if (!string.IsNullOrWhiteSpace(source))
                {
                    entryLine += $"  ({source})";
                }

                if (totalLength + entryLine.Length > MaxChars)
                {
                    lines.Add("  ... (more in L3 search)");
                    return string.Join('\n', lines);
                }

                lines.Add(entryLine);
                totalLength += entryLine.Length;
            }
        }

        return string.Join('\n', lines);
    }

    private async Task<bool> HasPalaceAsync(CancellationToken cancellationToken)
    {
        var collections = await _vectorStore.ListCollectionsAsync(cancellationToken);
        return collections.Contains(_collectionName, StringComparer.Ordinal);
    }

    private static double GetImportance(Core.Models.DrawerMetadata metadata)
    {
        if (metadata.Importance.HasValue)
        {
            return metadata.Importance.Value;
        }

        if (metadata.EmotionalWeight.HasValue)
        {
            return metadata.EmotionalWeight.Value;
        }

        if (metadata.Weight.HasValue)
        {
            return metadata.Weight.Value;
        }

        return 3;
    }
}
