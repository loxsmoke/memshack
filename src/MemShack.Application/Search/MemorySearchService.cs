using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Search;

public sealed class MemorySearchService
{
    private readonly string _collectionName;
    private readonly string _palacePath;
    private readonly IVectorStore _vectorStore;

    public MemorySearchService(IVectorStore vectorStore, string palacePath, string collectionName = CollectionNames.Drawers)
    {
        _vectorStore = vectorStore;
        _palacePath = palacePath;
        _collectionName = collectionName;
    }

    public async Task<SearchMemoriesResult> SearchMemoriesAsync(
        string query,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken cancellationToken = default)
    {
        var filters = new SearchFilters(wing, room);
        if (!await HasPalaceAsync(cancellationToken))
        {
            return new SearchMemoriesResult(query, filters, [], $"No palace found at {_palacePath}");
        }

        try
        {
            var hits = await _vectorStore.SearchAsync(_collectionName, query, nResults, wing, room, cancellationToken);
            var normalized = hits
                .Select(hit => new SearchHit(
                    hit.Text,
                    hit.Wing,
                    hit.Room,
                    Path.GetFileName(hit.SourceFile),
                    Math.Round(hit.Similarity, 3),
                    hit.Metadata))
                .ToArray();

            return new SearchMemoriesResult(query, filters, normalized);
        }
        catch (Exception exception)
        {
            return new SearchMemoriesResult(query, filters, [], $"Search error: {exception.Message}");
        }
    }

    public async Task<string> FormatSearchAsync(
        string query,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await SearchMemoriesAsync(query, wing, room, nResults, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return result.Error;
        }

        if (result.Results.Count == 0)
        {
            return $"\n  No results found for: \"{query}\"";
        }

        var lines = new List<string>
        {
            string.Empty,
            new string('=', 60),
            $"  Results for: \"{query}\"",
        };

        if (!string.IsNullOrWhiteSpace(wing))
        {
            lines.Add($"  Wing: {wing}");
        }

        if (!string.IsNullOrWhiteSpace(room))
        {
            lines.Add($"  Room: {room}");
        }

        lines.Add(new string('=', 60));
        lines.Add(string.Empty);

        foreach (var (hit, index) in result.Results.Select((value, i) => (value, i + 1)))
        {
            lines.Add($"  [{index}] {hit.Wing} / {hit.Room}");
            lines.Add($"      Source: {hit.SourceFile}");
            lines.Add($"      Match:  {hit.Similarity}");
            lines.Add(string.Empty);

            foreach (var line in hit.Text.Trim().Split('\n'))
            {
                lines.Add($"      {line}");
            }

            lines.Add(string.Empty);
            lines.Add($"  {new string('-', 56)}");
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    private async Task<bool> HasPalaceAsync(CancellationToken cancellationToken)
    {
        var collections = await _vectorStore.ListCollectionsAsync(cancellationToken);
        return collections.Contains(_collectionName, StringComparer.Ordinal);
    }
}
