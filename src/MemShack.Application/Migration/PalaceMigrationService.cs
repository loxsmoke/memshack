using Microsoft.Data.Sqlite;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Migration;

public sealed class PalaceMigrationService
{
    private readonly Func<string, IVectorStore> _vectorStoreFactory;
    private readonly Func<string, CancellationToken, Task>? _preparePalaceForSwapAsync;

    public PalaceMigrationService(
        Func<string, IVectorStore> vectorStoreFactory,
        Func<string, CancellationToken, Task>? preparePalaceForSwapAsync = null)
    {
        _vectorStoreFactory = vectorStoreFactory;
        _preparePalaceForSwapAsync = preparePalaceForSwapAsync;
    }

    public async Task<PalaceMigrationResult> MigrateAsync(
        string palacePath,
        string collectionName = CollectionNames.Drawers,
        bool dryRun = false,
        Action<PalaceMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPalacePath = Path.GetFullPath(palacePath);
        var databasePath = Path.Combine(resolvedPalacePath, "chroma.sqlite3");
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException($"No palace database found at {databasePath}");
        }

        var sourceVersion = await DetectChromaDbVersionAsync(databasePath, cancellationToken);
        var drawers = await ExtractDrawersFromSqliteAsync(databasePath, cancellationToken);
        var summaries = BuildSummaries(drawers);

        if (dryRun || drawers.Count == 0)
        {
            return new PalaceMigrationResult(
                resolvedPalacePath,
                databasePath,
                sourceVersion,
                dryRun,
                drawers.Count,
                0,
                null,
                summaries);
        }

        await PreparePalaceForSwapAsync(resolvedPalacePath, cancellationToken);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = $"{resolvedPalacePath}.pre-migrate.{timestamp}";
        var tempPalacePath = Path.Combine(
            Path.GetDirectoryName(resolvedPalacePath) ?? resolvedPalacePath,
            $"{Path.GetFileName(resolvedPalacePath)}.migrate.{Guid.NewGuid():N}");

        CopyDirectory(resolvedPalacePath, backupPath);

        var store = _vectorStoreFactory(tempPalacePath);
        await store.EnsureCollectionAsync(collectionName, cancellationToken);

        var imported = 0;
        foreach (var drawer in drawers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await store.AddDrawerAsync(collectionName, drawer, cancellationToken))
            {
                imported++;
            }

            progress?.Invoke(new PalaceMigrationProgress(imported, drawers.Count));
        }

        await PreparePalaceForSwapAsync(tempPalacePath, cancellationToken);

        if (Directory.Exists(resolvedPalacePath))
        {
            Directory.Delete(resolvedPalacePath, recursive: true);
        }

        Directory.Move(tempPalacePath, resolvedPalacePath);

        return new PalaceMigrationResult(
            resolvedPalacePath,
            databasePath,
            sourceVersion,
            false,
            drawers.Count,
            imported,
            backupPath,
            summaries);
    }

    private static async Task<string> DetectChromaDbVersionAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(collections)";
        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "schema_str", StringComparison.Ordinal))
            {
                return "1.x";
            }
        }

        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='embeddings_queue'";
        var hasEmbeddingsQueue = await tableCommand.ExecuteScalarAsync(cancellationToken);
        return hasEmbeddingsQueue is not null && hasEmbeddingsQueue != DBNull.Value
            ? "0.6.x"
            : "unknown";
    }

    private static async Task<IReadOnlyList<DrawerRecord>> ExtractDrawersFromSqliteAsync(string databasePath, CancellationToken cancellationToken)
    {
        var drawers = new List<DrawerRecord>();
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        const string drawerSql = """
            SELECT e.embedding_id,
                   MAX(CASE WHEN em.key = 'chroma:document' THEN em.string_value END) AS document
            FROM embeddings e
            JOIN embedding_metadata em ON em.id = e.id
            GROUP BY e.embedding_id
            """;

        await using var drawerCommand = connection.CreateCommand();
        drawerCommand.CommandText = drawerSql;
        await using var reader = await drawerCommand.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var drawerId = reader.GetString(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var document = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(document))
            {
                continue;
            }

            var metadata = await ReadMetadataAsync(connection, drawerId, cancellationToken);
            drawers.Add(new DrawerRecord(drawerId, document, ToDrawerMetadata(metadata)));
        }

        return drawers;
    }

    private static async Task<Dictionary<string, object?>> ReadMetadataAsync(
        SqliteConnection connection,
        string drawerId,
        CancellationToken cancellationToken)
    {
        const string metadataSql = """
            SELECT em.key, em.string_value, em.int_value, em.float_value, em.bool_value
            FROM embedding_metadata em
            JOIN embeddings e ON e.id = em.id
            WHERE e.embedding_id = $embeddingId
              AND em.key NOT LIKE 'chroma:%'
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = metadataSql;
        command.Parameters.AddWithValue("$embeddingId", drawerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            metadata[key] =
                !reader.IsDBNull(1) ? reader.GetString(1) :
                !reader.IsDBNull(2) ? reader.GetInt64(2) :
                !reader.IsDBNull(3) ? reader.GetDouble(3) :
                !reader.IsDBNull(4) ? reader.GetBoolean(4) :
                null;
        }

        return metadata;
    }

    private static DrawerMetadata ToDrawerMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        return new DrawerMetadata
        {
            Wing = GetString(metadata, "wing"),
            Room = GetString(metadata, "room"),
            SourceFile = GetString(metadata, "source_file"),
            SourceMtimeUtcMs = GetNullableLong(metadata, MetadataKeys.SourceMtime),
            ChunkIndex = GetInt(metadata, "chunk_index"),
            AddedBy = GetString(metadata, "added_by"),
            FiledAt = GetString(metadata, "filed_at"),
            EmbeddingSignature = GetNullableString(metadata, MetadataKeys.EmbeddingSignature),
            IngestMode = GetNullableString(metadata, "ingest_mode"),
            ExtractMode = GetNullableString(metadata, "extract_mode"),
            Hall = GetNullableString(metadata, "hall"),
            Topic = GetNullableString(metadata, "topic"),
            Type = GetNullableString(metadata, "type"),
            Agent = GetNullableString(metadata, "agent"),
            Date = GetNullableString(metadata, "date"),
            Importance = GetNullableDouble(metadata, "importance"),
            EmotionalWeight = GetNullableDouble(metadata, "emotional_weight"),
            Weight = GetNullableDouble(metadata, "weight"),
            CompressionRatio = GetNullableDouble(metadata, "compression_ratio"),
            OriginalTokens = GetNullableInt(metadata, "original_tokens"),
            CompressedTokens = GetNullableInt(metadata, "compressed_tokens"),
        };
    }

    private static IReadOnlyList<PalaceMigrationWingSummary> BuildSummaries(IReadOnlyList<DrawerRecord> drawers)
    {
        return drawers
            .GroupBy(drawer => string.IsNullOrWhiteSpace(drawer.Metadata.Wing) ? "?" : drawer.Metadata.Wing, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(
                wingGroup => new PalaceMigrationWingSummary(
                    wingGroup.Key,
                    wingGroup.Count(),
                    wingGroup.GroupBy(drawer => string.IsNullOrWhiteSpace(drawer.Metadata.Room) ? "?" : drawer.Metadata.Room, StringComparer.Ordinal)
                        .OrderByDescending(group => group.Count())
                        .ThenBy(group => group.Key, StringComparer.Ordinal)
                        .Select(group => new PalaceMigrationRoomSummary(group.Key, group.Count()))
                        .ToArray()))
            .ToArray();
    }

    private async Task PreparePalaceForSwapAsync(string palacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_preparePalaceForSwapAsync is null)
        {
            return;
        }

        await _preparePalaceForSwapAsync(palacePath, cancellationToken);
    }

    private static string GetString(IReadOnlyDictionary<string, object?> metadata, string key) =>
        GetNullableString(metadata, key) ?? string.Empty;

    private static string? GetNullableString(IReadOnlyDictionary<string, object?> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static int GetInt(IReadOnlyDictionary<string, object?> metadata, string key) =>
        GetNullableInt(metadata, key) ?? 0;

    private static int? GetNullableInt(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null,
        };
    }

    private static long? GetNullableLong(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue => (long)doubleValue,
            string stringValue when long.TryParse(stringValue, out var parsed) => parsed,
            _ => null,
        };
    }

    private static double? GetNullableDouble(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            string stringValue when double.TryParse(stringValue, out var parsed) => parsed,
            _ => null,
        };
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }
}

public sealed record PalaceMigrationResult(
    string PalacePath,
    string DatabasePath,
    string SourceVersion,
    bool DryRun,
    int DrawersExtracted,
    int DrawersImported,
    string? BackupPath,
    IReadOnlyList<PalaceMigrationWingSummary> Wings);

public sealed record PalaceMigrationWingSummary(
    string Wing,
    int DrawerCount,
    IReadOnlyList<PalaceMigrationRoomSummary> Rooms);

public sealed record PalaceMigrationRoomSummary(string Room, int DrawerCount);

public sealed record PalaceMigrationProgress(int DrawersImported, int TotalDrawers);
