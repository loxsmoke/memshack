using MemShack.Application.Search;
using MemShack.Core.Models;

namespace MemShack.Application.Layers;

public sealed class Layer3
{
    private readonly MemorySearchService _searchService;

    public Layer3(MemorySearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task<string> SearchAsync(
        string query,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await _searchService.SearchMemoriesAsync(query, wing, room, nResults, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return result.Error.StartsWith("No palace found", StringComparison.Ordinal)
                ? "No palace found."
                : result.Error;
        }

        if (result.Results.Count == 0)
        {
            return "No results found.";
        }

        var lines = new List<string> { $"## L3 - SEARCH RESULTS for \"{query}\"" };
        foreach (var (hit, index) in result.Results.Select((value, i) => (value, i + 1)))
        {
            var snippet = hit.Text.Trim().Replace("\n", " ", StringComparison.Ordinal);
            if (snippet.Length > 300)
            {
                snippet = snippet[..297] + "...";
            }

            lines.Add($"  [{index}] {hit.Wing}/{hit.Room} (sim={hit.Similarity})");
            lines.Add($"      {snippet}");
            if (!string.IsNullOrWhiteSpace(hit.SourceFile))
            {
                lines.Add($"      src: {hit.SourceFile}");
            }
        }

        return string.Join('\n', lines);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchRawAsync(
        string query,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await _searchService.SearchMemoriesAsync(query, wing, room, nResults, cancellationToken);
        return result.Error is null ? result.Results : [];
    }
}
