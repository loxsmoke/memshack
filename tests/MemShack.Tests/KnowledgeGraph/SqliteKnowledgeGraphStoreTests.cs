using System.Text.Json;
using MemShack.Core.Models;
using MemShack.Infrastructure.Sqlite.KnowledgeGraph;
using MemShack.Tests.Utilities;
using Microsoft.Data.Sqlite;

namespace MemShack.Tests.KnowledgeGraph;

[TestClass]
public sealed class SqliteKnowledgeGraphStoreTests
{
    [TestMethod]
    public async Task InitializesStableSchema()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();

        var tables = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        Assert.Contains("entities", tables);
        Assert.Contains("triples", tables);

        var entityColumns = await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('entities') ORDER BY cid");
        Assert.Equal(["id", "name", "type", "properties", "created_at"], entityColumns);

        var tripleColumns = await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('triples') ORDER BY cid");
        Assert.Equal(
        [
            "id",
            "subject",
            "predicate",
            "object",
            "valid_from",
            "valid_to",
            "confidence",
            "source_closet",
            "source_file",
            "extracted_at",
        ], tripleColumns);

        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode";
        var journalMode = (string)(await journalCommand.ExecuteScalarAsync())!;
        Assert.Equal("wal", journalMode);
    }

    [TestMethod]
    public async Task AddEntity_NormalizesIdAndStoresProperties()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        var entityId = await store.AddEntityAsync(new EntityRecord("Dr. Chen", "person", new Dictionary<string, string> { ["role"] = "mentor" }));

        Assert.Equal("dr._chen", entityId);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT properties FROM entities WHERE id = $id";
        command.Parameters.AddWithValue("$id", entityId);
        var propertiesJson = (string)(await command.ExecuteScalarAsync())!;
        var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(propertiesJson)!;
        Assert.Equal("mentor", properties["role"]);
    }

    [TestMethod]
    public async Task AddEntity_UpsertsInsteadOfDuplicating()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        await store.AddEntityAsync(new EntityRecord("Alice", "person"));
        await store.AddEntityAsync(new EntityRecord("Alice", "engineer"));
        var stats = await store.StatsAsync();

        Assert.Equal(1, stats.Entities);
    }

    [TestMethod]
    public async Task AddTriple_AutoCreatesEntitiesAndPreservesPrefix()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        var tripleId = await store.AddTripleAsync(new TripleRecord("Max", "does", "swimming", ValidFrom: "2025-01-01"));
        var stats = await store.StatsAsync();

        Assert.StartsWith("t_max_does_swimming_", tripleId);
        Assert.Matches(@"^t_max_does_swimming_[a-f0-9]{12}$", tripleId);
        Assert.Equal(2, stats.Entities);
    }

    [TestMethod]
    public async Task AddTriple_DuplicateReturnsExistingId()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        var firstId = await store.AddTripleAsync(new TripleRecord("Alice", "knows", "Bob"));
        var secondId = await store.AddTripleAsync(new TripleRecord("Alice", "knows", "Bob"));

        Assert.Equal(firstId, secondId);
    }

    [TestMethod]
    public async Task InvalidatedTriple_AllowsReAdd()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        var firstId = await store.AddTripleAsync(new TripleRecord("Alice", "works_at", "Acme"));
        await store.InvalidateAsync("Alice", "works_at", "Acme", ended: "2025-01-01");
        var secondId = await store.AddTripleAsync(new TripleRecord("Alice", "works_at", "Acme"));

        Assert.NotEqual(firstId, secondId);
    }

    [TestMethod]
    public async Task QueryEntity_ReturnsOutgoingRelationshipsByDefault()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var results = await store.QueryEntityAsync("Alice");
        var predicates = results.Select(result => result.Predicate).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("parent_of", predicates);
        Assert.Contains("works_at", predicates);
        Assert.All(results, result => Assert.Equal("outgoing", result.Direction));
    }

    [TestMethod]
    public async Task QueryEntity_SupportsIncomingAndBothDirections()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var incoming = await store.QueryEntityAsync("Max", direction: "incoming");
        var both = await store.QueryEntityAsync("Max", direction: "both");

        Assert.Contains(incoming, result => result.Subject == "Alice" && result.Predicate == "parent_of");
        Assert.Contains(both, result => result.Direction == "incoming");
        Assert.Contains(both, result => result.Direction == "outgoing");
    }

    [TestMethod]
    public async Task QueryEntity_AsOfFiltersExpiredFacts()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var oldResults = await store.QueryEntityAsync("Alice", asOf: "2023-06-01");
        var currentResults = await store.QueryEntityAsync("Alice", asOf: "2025-06-01");

        Assert.Contains(oldResults, result => result.Predicate == "works_at" && result.Object == "Acme Corp");
        Assert.DoesNotContain(oldResults, result => result.Predicate == "works_at" && result.Object == "NewCo");
        Assert.Contains(currentResults, result => result.Predicate == "works_at" && result.Object == "NewCo");
        Assert.DoesNotContain(currentResults, result => result.Predicate == "works_at" && result.Object == "Acme Corp");
    }

    [TestMethod]
    public async Task QueryRelationship_NormalizesPredicateAndReturnsMatches()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var results = await store.QueryRelationshipAsync("does");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("does", result.Predicate));
    }

    [TestMethod]
    public async Task Invalidate_MarksFactAsExpired()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        await store.InvalidateAsync("Max", "does", "chess", ended: "2026-01-01");
        var results = await store.QueryEntityAsync("Max");
        var chess = Assert.Single(results, result => result.Object == "chess");

        Assert.Equal("2026-01-01", chess.ValidTo);
        Assert.False(chess.Current);
    }

    [TestMethod]
    public async Task Timeline_SupportsGlobalLimitAndEntityFilter()
    {
        using var temp = new TemporaryDirectory();
        var seeded = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var entityTimeline = await seeded.TimelineAsync("Max");
        Assert.Contains(entityTimeline, result => result.Subject == "Max" || result.Object == "Max");

        using var tempMany = new TemporaryDirectory();
        var storeMany = KnowledgeGraphTestFactory.CreateStore(tempMany);
        for (var index = 0; index < 105; index++)
        {
            await storeMany.AddTripleAsync(new TripleRecord($"entity_{index}", "relates_to", $"entity_{index + 1}"));
        }

        var globalTimeline = await storeMany.TimelineAsync();
        Assert.Equal(100, globalTimeline.Count);

        using var tempEntityLimit = new TemporaryDirectory();
        var storeEntityLimit = KnowledgeGraphTestFactory.CreateStore(tempEntityLimit);
        for (var index = 0; index < 105; index++)
        {
            await storeEntityLimit.AddTripleAsync(new TripleRecord("Hub", "connected_to", $"Leaf {index}", ValidFrom: $"2026-01-{(index % 28) + 1:00}"));
        }

        var limitedEntityTimeline = await storeEntityLimit.TimelineAsync("Hub");
        Assert.Equal(100, limitedEntityTimeline.Count);
    }

    [TestMethod]
    public async Task Stats_MatchesSeededGraphExpectations()
    {
        using var temp = new TemporaryDirectory();
        var store = await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp);

        var stats = await store.StatsAsync();

        Assert.True(stats.Entities >= 4);
        Assert.Equal(5, stats.Triples);
        Assert.Equal(4, stats.CurrentFacts);
        Assert.Equal(1, stats.ExpiredFacts);
        Assert.Contains("does", stats.RelationshipTypes);
        Assert.Contains("works_at", stats.RelationshipTypes);
    }

    [TestMethod]
    public async Task RepeatedOperations_UseWalModeWithoutChangingQueryShapes()
    {
        using var temp = new TemporaryDirectory();
        var store = KnowledgeGraphTestFactory.CreateStore(temp);

        for (var index = 0; index < 25; index++)
        {
            await store.AddTripleAsync(new TripleRecord($"Person {index}", "works_with", $"Person {index + 1}", ValidFrom: "2026-01-01"));
        }

        var relationshipResults = await store.QueryRelationshipAsync("works_with");
        var timeline = await store.TimelineAsync();
        var stats = await store.StatsAsync();

        Assert.Equal(25, relationshipResults.Count);
        Assert.Equal(25, timeline.Count);
        Assert.Equal(26, stats.Entities);
        Assert.Equal(25, stats.Triples);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
