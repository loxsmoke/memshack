using Microsoft.Data.Sqlite;

namespace MemShack.Tests.Utilities;

public static class ChromaSqliteFixtureBuilder
{
    public static async Task<string> CreateAsync(
        string palacePath,
        IEnumerable<SqliteDrawerSeed> drawers,
        string version = "1.x")
    {
        Directory.CreateDirectory(palacePath);
        var databasePath = Path.Combine(palacePath, "chroma.sqlite3");

        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            version == "1.x"
                ? """
                  CREATE TABLE collections (
                      id TEXT PRIMARY KEY,
                      name TEXT NOT NULL,
                      schema_str TEXT NULL
                  );
                  """
                : """
                  CREATE TABLE collections (
                      id TEXT PRIMARY KEY,
                      name TEXT NOT NULL
                  );
                  """);
        await ExecuteAsync(connection, "CREATE TABLE embeddings (id INTEGER PRIMARY KEY, embedding_id TEXT NOT NULL);");
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE embedding_metadata (
                id INTEGER NOT NULL,
                key TEXT NOT NULL,
                string_value TEXT NULL,
                int_value INTEGER NULL,
                float_value REAL NULL,
                bool_value INTEGER NULL
            );
            """);

        if (version == "0.6.x")
        {
            await ExecuteAsync(connection, "CREATE TABLE embeddings_queue (id INTEGER PRIMARY KEY, created_at TEXT NULL);");
        }

        await ExecuteAsync(connection, "INSERT INTO collections (id, name" + (version == "1.x" ? ", schema_str" : string.Empty) + ") VALUES ('c1', 'mempalace_drawers'" + (version == "1.x" ? ", '{}'" : string.Empty) + ");");

        var rowId = 1;
        foreach (var drawer in drawers)
        {
            await using (var embeddingCommand = connection.CreateCommand())
            {
                embeddingCommand.CommandText = "INSERT INTO embeddings (id, embedding_id) VALUES ($id, $embeddingId);";
                embeddingCommand.Parameters.AddWithValue("$id", rowId);
                embeddingCommand.Parameters.AddWithValue("$embeddingId", drawer.Id);
                await embeddingCommand.ExecuteNonQueryAsync();
            }

            await InsertMetadataAsync(connection, rowId, "chroma:document", drawer.Text);
            foreach (var (key, value) in drawer.Metadata)
            {
                await InsertMetadataAsync(connection, rowId, key, value);
            }

            rowId++;
        }

        return databasePath;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertMetadataAsync(SqliteConnection connection, int id, string key, object? value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO embedding_metadata (id, key, string_value, int_value, float_value, bool_value)
            VALUES ($id, $key, $stringValue, $intValue, $floatValue, $boolValue);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$stringValue", value is string stringValue ? stringValue : DBNull.Value);
        command.Parameters.AddWithValue("$intValue", value is int intValue ? intValue : value is long longValue ? longValue : DBNull.Value);
        command.Parameters.AddWithValue("$floatValue", value is double doubleValue ? doubleValue : value is float floatValue ? floatValue : DBNull.Value);
        command.Parameters.AddWithValue("$boolValue", value is bool boolValue ? (boolValue ? 1 : 0) : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record SqliteDrawerSeed(string Id, string Text, IReadOnlyDictionary<string, object?> Metadata);
