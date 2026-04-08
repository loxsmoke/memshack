using System.Text.Json;
using System.Text.RegularExpressions;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Infrastructure.VectorStore.Collections;

public sealed class ChromaCompatibilityVectorStore : IVectorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex TokenPattern = new(@"\b[a-z0-9_]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public ChromaCompatibilityVectorStore(string palacePath)
    {
        PalacePath = Path.GetFullPath(palacePath);
        CollectionsPath = Path.Combine(PalacePath, "collections");
        Directory.CreateDirectory(CollectionsPath);
    }

    public string PalacePath { get; }

    public string CollectionsPath { get; }

    public async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var collection = await LoadCollectionAsync(collectionName, cancellationToken);
            await SaveCollectionAsync(collection, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(CollectionsPath);
        IReadOnlyList<string> collections = Directory.EnumerateFiles(CollectionsPath, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(collections);
    }

    public async Task<bool> AddDrawerAsync(
        string collectionName,
        DrawerRecord drawer,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var collection = await LoadCollectionAsync(collectionName, cancellationToken);
            if (collection.Drawers.Any(existing => existing.Id == drawer.Id))
            {
                return false;
            }

            collection.Drawers.Add(drawer);
            await SaveCollectionAsync(collection, cancellationToken);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> DeleteDrawerAsync(
        string collectionName,
        string drawerId,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var collection = await LoadCollectionAsync(collectionName, cancellationToken);
            var removed = collection.Drawers.RemoveAll(drawer => drawer.Id == drawerId) > 0;
            if (removed)
            {
                await SaveCollectionAsync(collection, cancellationToken);
            }

            return removed;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string collectionName,
        string query,
        int limit,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default)
    {
        var drawers = await GetDrawersAsync(collectionName, wing, room, cancellationToken);
        var queryTokens = Tokenize(query);

        return drawers
            .Select(drawer => new SearchHit(
                drawer.Text,
                drawer.Metadata.Wing,
                drawer.Metadata.Room,
                drawer.Metadata.SourceFile,
                CalculateSimilarity(queryTokens, Tokenize(drawer.Text)),
                ToMetadataDictionary(drawer.Metadata)))
            .Where(hit => hit.Similarity > 0 || queryTokens.Count == 0)
            .OrderByDescending(hit => hit.Similarity)
            .ThenBy(hit => hit.SourceFile, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<DrawerRecord>> GetDrawersAsync(
        string collectionName,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var collection = await LoadCollectionAsync(collectionName, cancellationToken);
            return collection.Drawers
                .Where(drawer => wing is null || drawer.Metadata.Wing == wing)
                .Where(drawer => room is null || drawer.Metadata.Room == room)
                .OrderBy(drawer => drawer.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> HasSourceFileAsync(
        string collectionName,
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourceFile = Path.GetFullPath(sourceFile);
        var drawers = await GetDrawersAsync(collectionName, cancellationToken: cancellationToken);
        return drawers.Any(drawer => string.Equals(Path.GetFullPath(drawer.Metadata.SourceFile), normalizedSourceFile, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CollectionDocument> LoadCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var path = GetCollectionPath(collectionName);
        if (!File.Exists(path))
        {
            return new CollectionDocument(collectionName, []);
        }

        await using var stream = File.OpenRead(path);
        var collection = await JsonSerializer.DeserializeAsync<CollectionDocument>(stream, JsonOptions, cancellationToken);
        return collection ?? new CollectionDocument(collectionName, []);
    }

    private async Task SaveCollectionAsync(CollectionDocument collection, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CollectionsPath);
        var path = GetCollectionPath(collection.Name);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, collection, JsonOptions, cancellationToken);
    }

    private string GetCollectionPath(string collectionName) =>
        Path.Combine(CollectionsPath, $"{collectionName}.json");

    private static HashSet<string> Tokenize(string text) =>
        TokenPattern.Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static double CalculateSimilarity(IReadOnlySet<string> queryTokens, IReadOnlySet<string> textTokens)
    {
        if (queryTokens.Count == 0 || textTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(textTokens.Contains);
        return overlap / (double)queryTokens.Count;
    }

    private static IReadOnlyDictionary<string, object?> ToMetadataDictionary(DrawerMetadata metadata)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["wing"] = metadata.Wing,
            ["room"] = metadata.Room,
            ["source_file"] = metadata.SourceFile,
            ["chunk_index"] = metadata.ChunkIndex,
            ["added_by"] = metadata.AddedBy,
            ["filed_at"] = metadata.FiledAt,
            ["ingest_mode"] = metadata.IngestMode,
            ["extract_mode"] = metadata.ExtractMode,
            ["hall"] = metadata.Hall,
            ["topic"] = metadata.Topic,
            ["type"] = metadata.Type,
            ["agent"] = metadata.Agent,
            ["date"] = metadata.Date,
            ["importance"] = metadata.Importance,
            ["emotional_weight"] = metadata.EmotionalWeight,
            ["weight"] = metadata.Weight,
            ["compression_ratio"] = metadata.CompressionRatio,
            ["original_tokens"] = metadata.OriginalTokens,
            ["compressed_tokens"] = metadata.CompressedTokens,
        };
    }

    private sealed record CollectionDocument(string Name, List<DrawerRecord> Drawers);
}
