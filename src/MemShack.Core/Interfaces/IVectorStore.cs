using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IVectorStore
{
    Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    Task<bool> AddDrawerAsync(
        string collectionName,
        DrawerRecord drawer,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteDrawerAsync(
        string collectionName,
        string drawerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit>> SearchAsync(
        string collectionName,
        string query,
        int limit,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DrawerRecord>> GetDrawersAsync(
        string collectionName,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasSourceFileAsync(
        string collectionName,
        string sourceFile,
        string? embeddingSignature = null,
        long? sourceMtimeUtcMs = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSourceFileAsync(
        string collectionName,
        string sourceFile,
        CancellationToken cancellationToken = default);
}
