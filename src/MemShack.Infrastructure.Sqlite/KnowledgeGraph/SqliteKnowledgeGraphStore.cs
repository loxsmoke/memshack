using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace MemShack.Infrastructure.Sqlite.KnowledgeGraph;

public sealed class SqliteKnowledgeGraphStore : IKnowledgeGraphStore
{
    private const int SqliteBusyTimeoutSeconds = 10;

    public SqliteKnowledgeGraphStore(string? dbPath = null)
    {
        DatabasePath = ResolveDatabasePath(dbPath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        InitializeDatabase();
    }

    public string DatabasePath { get; }

    public static string NormalizeEntityId(string name) =>
        name
            .ToLowerInvariant()
            .Replace(" ", "_", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);

    public static string NormalizePredicate(string predicate) =>
        predicate
            .ToLowerInvariant()
            .Replace(" ", "_", StringComparison.Ordinal);

    public async Task<string> AddEntityAsync(EntityRecord entity, CancellationToken cancellationToken = default)
    {
        var entityId = NormalizeEntityId(entity.Name);
        var propertiesJson = JsonSerializer.Serialize(entity.Properties ?? new Dictionary<string, string>(StringComparer.Ordinal));

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO entities (id, name, type, properties)
            VALUES ($id, $name, $type, $properties)
            """;
        command.Parameters.AddWithValue("$id", entityId);
        command.Parameters.AddWithValue("$name", entity.Name);
        command.Parameters.AddWithValue("$type", entity.Type);
        command.Parameters.AddWithValue("$properties", propertiesJson);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return entityId;
    }

    public async Task<string> AddTripleAsync(TripleRecord triple, CancellationToken cancellationToken = default)
    {
        var subjectId = NormalizeEntityId(triple.Subject);
        var objectId = NormalizeEntityId(triple.Object);
        var predicate = NormalizePredicate(triple.Predicate);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await InsertEntityIfMissingAsync(connection, transaction, subjectId, triple.Subject, cancellationToken);
        await InsertEntityIfMissingAsync(connection, transaction, objectId, triple.Object, cancellationToken);

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT id
                FROM triples
                WHERE subject = $subject
                  AND predicate = $predicate
                  AND object = $object
                  AND valid_to IS NULL
                """;
            existingCommand.Parameters.AddWithValue("$subject", subjectId);
            existingCommand.Parameters.AddWithValue("$predicate", predicate);
            existingCommand.Parameters.AddWithValue("$object", objectId);

            var existingId = await existingCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                await transaction.RollbackAsync(cancellationToken);
                return existingId;
            }
        }

        var tripleId = GenerateTripleId(subjectId, predicate, objectId, triple.ValidFrom, triple.ValidTo);

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO triples (
                    id,
                    subject,
                    predicate,
                    object,
                    valid_from,
                    valid_to,
                    confidence,
                    source_closet,
                    source_file)
                VALUES (
                    $id,
                    $subject,
                    $predicate,
                    $object,
                    $validFrom,
                    $validTo,
                    $confidence,
                    $sourceCloset,
                    $sourceFile)
                """;
            insertCommand.Parameters.AddWithValue("$id", tripleId);
            insertCommand.Parameters.AddWithValue("$subject", subjectId);
            insertCommand.Parameters.AddWithValue("$predicate", predicate);
            insertCommand.Parameters.AddWithValue("$object", objectId);
            insertCommand.Parameters.AddWithValue("$validFrom", (object?)triple.ValidFrom ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$validTo", (object?)triple.ValidTo ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$confidence", triple.Confidence);
            insertCommand.Parameters.AddWithValue("$sourceCloset", (object?)triple.SourceCloset ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$sourceFile", (object?)triple.SourceFile ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return tripleId;
    }

    public async Task InvalidateAsync(
        string subject,
        string predicate,
        string @object,
        string? ended = null,
        CancellationToken cancellationToken = default)
    {
        var subjectId = NormalizeEntityId(subject);
        var objectId = NormalizeEntityId(@object);
        var normalizedPredicate = NormalizePredicate(predicate);
        var endedDate = ended ?? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE triples
            SET valid_to = $ended
            WHERE subject = $subject
              AND predicate = $predicate
              AND object = $object
              AND valid_to IS NULL
            """;
        command.Parameters.AddWithValue("$ended", endedDate);
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$predicate", normalizedPredicate);
        command.Parameters.AddWithValue("$object", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TripleRecord>> QueryEntityAsync(
        string entity,
        string? asOf = null,
        string direction = "outgoing",
        CancellationToken cancellationToken = default)
    {
        var entityId = NormalizeEntityId(entity);
        var results = new List<TripleRecord>();

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        if (direction is "outgoing" or "both")
        {
            await using var outgoingCommand = connection.CreateCommand();
            outgoingCommand.CommandText = BuildQueryEntitySql(incoming: false, hasAsOf: !string.IsNullOrWhiteSpace(asOf));
            outgoingCommand.Parameters.AddWithValue("$entityId", entityId);
            if (!string.IsNullOrWhiteSpace(asOf))
            {
                outgoingCommand.Parameters.AddWithValue("$asOf", asOf);
            }

            await using var reader = await outgoingCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadTriple(reader, entity, reader.GetString(reader.GetOrdinal("obj_name")), "outgoing"));
            }
        }

        if (direction is "incoming" or "both")
        {
            await using var incomingCommand = connection.CreateCommand();
            incomingCommand.CommandText = BuildQueryEntitySql(incoming: true, hasAsOf: !string.IsNullOrWhiteSpace(asOf));
            incomingCommand.Parameters.AddWithValue("$entityId", entityId);
            if (!string.IsNullOrWhiteSpace(asOf))
            {
                incomingCommand.Parameters.AddWithValue("$asOf", asOf);
            }

            await using var reader = await incomingCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadTriple(reader, reader.GetString(reader.GetOrdinal("sub_name")), entity, "incoming"));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<TripleRecord>> QueryRelationshipAsync(
        string predicate,
        string? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPredicate = NormalizePredicate(predicate);
        var results = new List<TripleRecord>();

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                t.id,
                t.predicate,
                t.valid_from,
                t.valid_to,
                t.confidence,
                t.source_closet,
                t.source_file,
                s.name AS sub_name,
                o.name AS obj_name
            FROM triples t
            JOIN entities s ON t.subject = s.id
            JOIN entities o ON t.object = o.id
            WHERE t.predicate = $predicate
            """;
        if (!string.IsNullOrWhiteSpace(asOf))
        {
            command.CommandText += """

                AND (t.valid_from IS NULL OR t.valid_from <= $asOf)
                AND (t.valid_to IS NULL OR t.valid_to >= $asOf)
                """;
        }
        command.Parameters.AddWithValue("$predicate", normalizedPredicate);
        if (!string.IsNullOrWhiteSpace(asOf))
        {
            command.Parameters.AddWithValue("$asOf", asOf);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadTriple(reader, reader.GetString(reader.GetOrdinal("sub_name")), reader.GetString(reader.GetOrdinal("obj_name"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<TripleRecord>> TimelineAsync(
        string? entityName = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TripleRecord>();

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(entityName))
        {
            command.CommandText = """
                SELECT
                    t.id,
                    t.predicate,
                    t.valid_from,
                    t.valid_to,
                    t.confidence,
                    t.source_closet,
                    t.source_file,
                    s.name AS sub_name,
                    o.name AS obj_name
                FROM triples t
                JOIN entities s ON t.subject = s.id
                JOIN entities o ON t.object = o.id
                ORDER BY t.valid_from IS NULL, t.valid_from ASC
                LIMIT 100
                """;
        }
        else
        {
            command.CommandText = """
                SELECT
                    t.id,
                    t.predicate,
                    t.valid_from,
                    t.valid_to,
                    t.confidence,
                    t.source_closet,
                    t.source_file,
                    s.name AS sub_name,
                    o.name AS obj_name
                FROM triples t
                JOIN entities s ON t.subject = s.id
                JOIN entities o ON t.object = o.id
                WHERE t.subject = $entityId OR t.object = $entityId
                ORDER BY t.valid_from IS NULL, t.valid_from ASC
                LIMIT 100
                """;
            command.Parameters.AddWithValue("$entityId", NormalizeEntityId(entityName));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadTriple(reader, reader.GetString(reader.GetOrdinal("sub_name")), reader.GetString(reader.GetOrdinal("obj_name"))));
        }

        return results;
    }

    public async Task<KnowledgeGraphStats> StatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken);

        var entities = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM entities", cancellationToken);
        var triples = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM triples", cancellationToken);
        var currentFacts = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM triples WHERE valid_to IS NULL", cancellationToken);

        await using var predicatesCommand = connection.CreateCommand();
        predicatesCommand.CommandText = "SELECT DISTINCT predicate FROM triples ORDER BY predicate";
        var relationshipTypes = new List<string>();
        await using (var reader = await predicatesCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relationshipTypes.Add(reader.GetString(0));
            }
        }

        return new KnowledgeGraphStats(
            entities,
            triples,
            currentFacts,
            triples - currentFacts,
            relationshipTypes);
    }

    private static string ResolveDatabasePath(string? dbPath)
    {
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            return Path.GetFullPath(PathUtilities.ExpandHome(dbPath));
        }

        return MempalaceDefaults.GetDefaultKnowledgeGraphPath(PathUtilities.GetHomeDirectory());
    }

    private static string GenerateTripleId(string subjectId, string predicate, string objectId, string? validFrom, string? validTo)
    {
        var hashInput = $"{validFrom}|{validTo}|{DateTime.UtcNow:O}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant()[..12];
        return $"t_{subjectId}_{predicate}_{objectId}_{hash}";
    }

    private static string BuildQueryEntitySql(bool incoming, bool hasAsOf)
    {
        var sql = incoming
            ? """
                SELECT
                    t.id,
                    t.predicate,
                    t.valid_from,
                    t.valid_to,
                    t.confidence,
                    t.source_closet,
                    t.source_file,
                    e.name AS sub_name
                FROM triples t
                JOIN entities e ON t.subject = e.id
                WHERE t.object = $entityId
                """
            : """
                SELECT
                    t.id,
                    t.predicate,
                    t.valid_from,
                    t.valid_to,
                    t.confidence,
                    t.source_closet,
                    t.source_file,
                    e.name AS obj_name
                FROM triples t
                JOIN entities e ON t.object = e.id
                WHERE t.subject = $entityId
                """;

        if (!hasAsOf)
        {
            return sql;
        }

        return sql + "\n" + """
            AND (t.valid_from IS NULL OR t.valid_from <= $asOf)
            AND (t.valid_to IS NULL OR t.valid_to >= $asOf)
            """;
    }

    private static TripleRecord ReadTriple(SqliteDataReader reader, string subject, string @object, string? direction = null)
    {
        var validToOrdinal = reader.GetOrdinal("valid_to");
        var validTo = reader.IsDBNull(validToOrdinal)
            ? null
            : reader.GetString(validToOrdinal);

        return new TripleRecord(
            subject,
            reader.GetString(reader.GetOrdinal("predicate")),
            @object,
            reader.IsDBNull(reader.GetOrdinal("valid_from")) ? null : reader.GetString(reader.GetOrdinal("valid_from")),
            validTo,
            reader.GetDouble(reader.GetOrdinal("confidence")),
            reader.IsDBNull(reader.GetOrdinal("source_closet")) ? null : reader.GetString(reader.GetOrdinal("source_closet")),
            reader.IsDBNull(reader.GetOrdinal("source_file")) ? null : reader.GetString(reader.GetOrdinal("source_file")),
            reader.GetString(reader.GetOrdinal("id")),
            direction,
            validTo is null);
    }

    private static async Task InsertEntityIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityId,
        string entityName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO entities (id, name) VALUES ($id, $name)";
        command.Parameters.AddWithValue("$id", entityId);
        command.Parameters.AddWithValue("$name", entityName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // Keep connections short-lived to avoid lingering SQLite file locks across CLI commands and tests.
    // WAL mode preserves the concurrency and durability characteristics we want without holding one
    // process-wide connection open for the entire lifetime of the store.
    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = SqliteBusyTimeoutSeconds,
        }.ToString());

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 10000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 10000;
            """;
        command.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        connection.Open();
        ConfigureConnection(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS entities (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT DEFAULT 'unknown',
                properties TEXT DEFAULT '{}',
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS triples (
                id TEXT PRIMARY KEY,
                subject TEXT NOT NULL,
                predicate TEXT NOT NULL,
                object TEXT NOT NULL,
                valid_from TEXT,
                valid_to TEXT,
                confidence REAL DEFAULT 1.0,
                source_closet TEXT,
                source_file TEXT,
                extracted_at TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (subject) REFERENCES entities(id),
                FOREIGN KEY (object) REFERENCES entities(id)
            );

            CREATE INDEX IF NOT EXISTS idx_triples_subject ON triples(subject);
            CREATE INDEX IF NOT EXISTS idx_triples_object ON triples(object);
            CREATE INDEX IF NOT EXISTS idx_triples_predicate ON triples(predicate);
            CREATE INDEX IF NOT EXISTS idx_triples_valid ON triples(valid_from, valid_to);
            """;
        command.ExecuteNonQuery();
    }
}
