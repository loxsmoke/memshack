using MemShack.Core.Models;
using MemShack.Infrastructure.Sqlite.KnowledgeGraph;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.KnowledgeGraph;

internal static class KnowledgeGraphTestFactory
{
    public static SqliteKnowledgeGraphStore CreateStore(TemporaryDirectory temp) =>
        new(temp.GetPath("knowledge_graph.sqlite3"));

    public static async Task<SqliteKnowledgeGraphStore> CreateSeededStoreAsync(TemporaryDirectory temp)
    {
        var store = CreateStore(temp);

        await store.AddTripleAsync(new TripleRecord("Alice", "parent_of", "Max", ValidFrom: "2015-04-01"));
        await store.AddTripleAsync(new TripleRecord("Alice", "works_at", "Acme Corp", ValidFrom: "2020-01-01", ValidTo: "2024-01-31"));
        await store.AddTripleAsync(new TripleRecord("Alice", "works_at", "NewCo", ValidFrom: "2024-02-01"));
        await store.AddTripleAsync(new TripleRecord("Max", "does", "swimming", ValidFrom: "2025-01-01"));
        await store.AddTripleAsync(new TripleRecord("Max", "does", "chess", ValidFrom: "2025-01-15"));

        return store;
    }
}
