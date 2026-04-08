using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Compression;

public sealed class DrawerCompressionService
{
    private readonly AaakDialect _dialect;
    private readonly IVectorStore _vectorStore;

    public DrawerCompressionService(IVectorStore vectorStore, AaakDialect? dialect = null)
    {
        _vectorStore = vectorStore;
        _dialect = dialect ?? new AaakDialect();
    }

    public async Task<CompressionRunResult> RunAsync(
        string? wing = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var drawers = await _vectorStore.GetDrawersAsync(CollectionNames.Drawers, wing: wing, cancellationToken: cancellationToken);
        if (!dryRun)
        {
            await _vectorStore.EnsureCollectionAsync(CollectionNames.Compressed, cancellationToken);
        }

        var entries = new List<CompressedDrawerResult>();
        var totalOriginalChars = 0;
        var totalCompressedChars = 0;
        var totalOriginalTokens = 0;
        var totalCompressedTokens = 0;

        foreach (var drawer in drawers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compressedText = _dialect.Compress(drawer.Text, drawer.Metadata);
            var stats = _dialect.CompressionStats(drawer.Text, compressedText);

            entries.Add(
                new CompressedDrawerResult(
                    drawer.Id,
                    drawer.Metadata.Wing,
                    drawer.Metadata.Room,
                    drawer.Metadata.SourceFile,
                    compressedText,
                    stats));

            totalOriginalChars += stats.OriginalChars;
            totalCompressedChars += stats.CompressedChars;
            totalOriginalTokens += stats.OriginalTokens;
            totalCompressedTokens += stats.CompressedTokens;

            if (dryRun)
            {
                continue;
            }

            var compressedDrawer = new DrawerRecord(
                drawer.Id,
                compressedText,
                drawer.Metadata with
                {
                    CompressionRatio = stats.Ratio,
                    OriginalTokens = stats.OriginalTokens,
                    CompressedTokens = stats.CompressedTokens,
                });

            if (await _vectorStore.AddDrawerAsync(CollectionNames.Compressed, compressedDrawer, cancellationToken))
            {
                continue;
            }

            await _vectorStore.DeleteDrawerAsync(CollectionNames.Compressed, drawer.Id, cancellationToken);
            await _vectorStore.AddDrawerAsync(CollectionNames.Compressed, compressedDrawer, cancellationToken);
        }

        return new CompressionRunResult(
            drawers.Count,
            entries.Count,
            totalOriginalChars,
            totalCompressedChars,
            totalOriginalTokens,
            totalCompressedTokens,
            dryRun,
            entries);
    }
}
