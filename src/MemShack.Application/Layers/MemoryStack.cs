using MemShack.Application.Search;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;

namespace MemShack.Application.Layers;

public sealed class MemoryStack
{
    private readonly string _collectionName;
    private readonly string _palacePath;
    private readonly IVectorStore _vectorStore;

    public MemoryStack(
        IVectorStore vectorStore,
        string palacePath,
        string? identityPath = null,
        string collectionName = CollectionNames.Drawers)
    {
        _vectorStore = vectorStore;
        _palacePath = palacePath;
        IdentityPath = identityPath
            ?? Path.Combine(
                MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
                ConfigFileNames.IdentityText);
        _collectionName = collectionName;

        L0 = new Layer0(IdentityPath);
        L1 = new Layer1(vectorStore, collectionName);
        L2 = new Layer2(vectorStore, collectionName);
        L3 = new Layer3(new MemorySearchService(vectorStore, palacePath, collectionName));
    }

    public string IdentityPath { get; }

    public Layer0 L0 { get; }

    public Layer1 L1 { get; }

    public Layer2 L2 { get; }

    public Layer3 L3 { get; }

    public async Task<string> WakeUpAsync(string? wing = null, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>
        {
            L0.Render(),
            string.Empty,
            await L1.GenerateAsync(wing, cancellationToken),
        };

        return string.Join('\n', parts);
    }

    public Task<string> RecallAsync(
        string? wing = null,
        string? room = null,
        int nResults = 10,
        CancellationToken cancellationToken = default) =>
        L2.RetrieveAsync(wing, room, nResults, cancellationToken);

    public Task<string> SearchAsync(
        string query,
        string? wing = null,
        string? room = null,
        int nResults = 5,
        CancellationToken cancellationToken = default) =>
        L3.SearchAsync(query, wing, room, nResults, cancellationToken);

    public async Task<MemoryStackStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var collections = await _vectorStore.ListCollectionsAsync(cancellationToken);
        var totalDrawers = collections.Contains(_collectionName, StringComparer.Ordinal)
            ? (await _vectorStore.GetDrawersAsync(_collectionName, cancellationToken: cancellationToken)).Count
            : 0;

        return new MemoryStackStatus(
            _palacePath,
            new MemoryLayerStatus(IdentityPath, File.Exists(IdentityPath), L0.TokenEstimate()),
            new MemoryLayerStatus(Description: "Auto-generated from top palace drawers"),
            new MemoryLayerStatus(Description: "Wing/room filtered retrieval"),
            new MemoryLayerStatus(Description: "Full semantic search via ChromaDB"),
            totalDrawers);
    }
}
