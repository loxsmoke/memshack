using System.Text.Json;
using System.Text.Json.Nodes;
using MemShack.Application.Graphs;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.McpServer;
using MemShack.Tests.KnowledgeGraph;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Mcp;

[TestClass]
public sealed class MemShackMcpServerTests
{
    [TestMethod]
    public async Task Initialize_ReturnsFixtureCompatibleEnvelope()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);
        var request = LoadFixture("initialize-request.json").AsObject();
        var expected = LoadFixture("initialize-response.json").AsObject();

        var response = await server.HandleRequestAsync(request);

        Assert.NotNull(response);
        Assert.Equal(expected["jsonrpc"]!.GetValue<string>(), response!["jsonrpc"]!.GetValue<string>());
        Assert.Equal(expected["result"]!["protocolVersion"]!.GetValue<string>(), response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(expected["result"]!["serverInfo"]!["name"]!.GetValue<string>(), response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(expected["result"]!["serverInfo"]!["version"]!.GetValue<string>(), response["result"]!["serverInfo"]!["version"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task NotificationsInitialized_ReturnsNull_AndToolsListContainsAllTools()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);

        var notification = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject(),
        });
        var listResponse = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list",
            ["params"] = new JsonObject(),
        });

        Assert.Null(notification);
        var tools = (JsonArray)listResponse!["result"]!["tools"]!;
        var names = tools.Select(tool => tool!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(McpToolNames.All.Count, names.Count);
        Assert.Contains(McpToolNames.Status, names);
        Assert.Contains(McpToolNames.Search, names);
        Assert.Contains(McpToolNames.AddDrawer, names);
        Assert.Contains(McpToolNames.KgAdd, names);
    }

    [TestMethod]
    public async Task UnknownToolAndUnknownMethod_ReturnMinus32601()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);

        var unknownTool = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "nonexistent_tool",
                ["arguments"] = new JsonObject(),
            },
        });
        var unknownMethod = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 4,
            ["method"] = "unknown/method",
            ["params"] = new JsonObject(),
        });

        Assert.Equal(-32601, unknownTool!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, unknownMethod!["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task RunAsync_ProcessesJsonRpcOverStdio()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.EnsureCollectionAsync(CollectionNames.Drawers);

        var input = new StringReader(string.Join(Environment.NewLine,
            JsonSerializer.Serialize(LoadFixture("initialize-request.json")),
            JsonSerializer.Serialize(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["method"] = "notifications/initialized",
                ["params"] = new JsonObject(),
            }),
            JsonSerializer.Serialize(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = McpToolNames.Status,
                    ["arguments"] = new JsonObject(),
                },
            })));
        var output = new StringWriter();
        var error = new StringWriter();

        await server.RunAsync(input, output, error);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2.0", JsonNode.Parse(lines[0])!["jsonrpc"]!.GetValue<string>());
        var statusPayload = ParseToolText((JsonObject)JsonNode.Parse(lines[1])!);
        Assert.Equal(0, statusPayload["total_drawers"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ReadTools_ReturnStatusTaxonomyAndSearchResults()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp, seedPalace: true);

        var status = ToObject(await server.InvokeToolAsync(McpToolNames.Status));
        var wings = ToObject(await server.InvokeToolAsync(McpToolNames.ListWings));
        var rooms = ToObject(await server.InvokeToolAsync(McpToolNames.ListRooms, new JsonObject { ["wing"] = "project" }));
        var taxonomy = ToObject(await server.InvokeToolAsync(McpToolNames.GetTaxonomy));
        var search = ToObject(await server.InvokeToolAsync(McpToolNames.Search, new JsonObject { ["query"] = "JWT authentication tokens" }));

        Assert.Equal(4, status["total_drawers"]!.GetValue<int>());
        Assert.Equal(3, wings["wings"]!["project"]!.GetValue<int>());
        Assert.True(rooms["rooms"]!["backend"]!.GetValue<int>() > 0);
        Assert.Null(rooms["rooms"]!["planning"]);
        Assert.Equal(2, taxonomy["taxonomy"]!["project"]!["backend"]!.GetValue<int>());
        Assert.NotEmpty((JsonArray)search["results"]!);
    }

    [TestMethod]
    public async Task WriteTools_AddDeleteAndDuplicateCheckWork()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.EnsureCollectionAsync(CollectionNames.Drawers);

        var added = ToObject(await server.InvokeToolAsync(McpToolNames.AddDrawer, new JsonObject
        {
            ["wing"] = "test_wing",
            ["room"] = "test_room",
            ["content"] = "This is a test memory about Python decorators and metaclasses.",
        }));
        var duplicateCheckResponse = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 6,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = McpToolNames.CheckDuplicate,
                ["arguments"] = new JsonObject
                {
                    ["content"] = "This is a test memory about Python decorators and metaclasses.",
                    ["threshold"] = "0.5",
                },
            },
        });
        var duplicateAdded = ToObject(await server.InvokeToolAsync(McpToolNames.AddDrawer, new JsonObject
        {
            ["wing"] = "test_wing",
            ["room"] = "test_room",
            ["content"] = "This is a test memory about Python decorators and metaclasses.",
        }));
        var deleted = ToObject(await server.InvokeToolAsync(McpToolNames.DeleteDrawer, new JsonObject
        {
            ["drawer_id"] = added["drawer_id"]!.GetValue<string>(),
        }));

        Assert.True(added["success"]!.GetValue<bool>());
        Assert.StartsWith("drawer_test_wing_test_room_", added["drawer_id"]!.GetValue<string>());
        Assert.True(ParseToolText(duplicateCheckResponse!)["is_duplicate"]!.GetValue<bool>());
        Assert.False(duplicateAdded["success"]!.GetValue<bool>());
        Assert.Equal("duplicate", duplicateAdded["reason"]!.GetValue<string>());
        Assert.True(deleted["success"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task KnowledgeGraphTools_ReturnExpectedShapes()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp, seedKg: true);

        var added = ToObject(await server.InvokeToolAsync(McpToolNames.KgAdd, new JsonObject
        {
            ["subject"] = "Alice",
            ["predicate"] = "likes",
            ["object"] = "coffee",
            ["valid_from"] = "2025-01-01",
        }));
        var query = ToObject(await server.InvokeToolAsync(McpToolNames.KgQuery, new JsonObject { ["entity"] = "Max" }));
        var invalidated = ToObject(await server.InvokeToolAsync(McpToolNames.KgInvalidate, new JsonObject
        {
            ["subject"] = "Max",
            ["predicate"] = "does",
            ["object"] = "chess",
            ["ended"] = "2026-03-01",
        }));
        var timeline = ToObject(await server.InvokeToolAsync(McpToolNames.KgTimeline, new JsonObject { ["entity"] = "Alice" }));
        var stats = ToObject(await server.InvokeToolAsync(McpToolNames.KgStats));

        Assert.True(added["success"]!.GetValue<bool>());
        Assert.True(query["count"]!.GetValue<int>() > 0);
        Assert.True(invalidated["success"]!.GetValue<bool>());
        Assert.True(timeline["count"]!.GetValue<int>() > 0);
        Assert.True(stats["entities"]!.GetValue<int>() >= 4);
    }

    [TestMethod]
    public async Task GraphTools_ReturnTraversalSuggestionsTunnelsAndStats()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await SeedGraphDrawersAsync(store, temp);

        var traversal = ToArray(await server.InvokeToolAsync(McpToolNames.Traverse, new JsonObject
        {
            ["start_room"] = "backend",
            ["max_hops"] = 2.0,
        }));
        var missing = ToObject(await server.InvokeToolAsync(McpToolNames.Traverse, new JsonObject
        {
            ["start_room"] = "back",
        }));
        var tunnels = ToArray(await server.InvokeToolAsync(McpToolNames.FindTunnels));
        var stats = ToObject(await server.InvokeToolAsync(McpToolNames.GraphStats));

        Assert.NotEmpty(traversal);
        Assert.Equal("backend", traversal[0]!["room"]!.GetValue<string>());
        Assert.Equal("Room 'back' not found", missing["error"]!.GetValue<string>());
        Assert.Contains("backend", ((JsonArray)missing["suggestions"]!).Select(node => node!.GetValue<string>()));
        Assert.NotEmpty(tunnels);
        Assert.True(stats["tunnel_rooms"]!.GetValue<int>() >= 1);
    }

    [TestMethod]
    public async Task DiaryTools_WriteReadAndCoerceIntegerArguments()
    {
        using var temp = new TemporaryDirectory();
        var server = await CreateServerAsync(temp);
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        await store.EnsureCollectionAsync(CollectionNames.Drawers);

        var written = ToObject(await server.InvokeToolAsync(McpToolNames.DiaryWrite, new JsonObject
        {
            ["agent_name"] = "TestAgent",
            ["entry"] = "Today we discussed authentication patterns.",
            ["topic"] = "architecture",
        }));
        await server.InvokeToolAsync(McpToolNames.DiaryWrite, new JsonObject
        {
            ["agent_name"] = "TestAgent",
            ["entry"] = "Second entry about migrations.",
            ["topic"] = "planning",
        });
        var read = ToObject(await server.InvokeToolAsync(McpToolNames.DiaryRead, new JsonObject
        {
            ["agent_name"] = "TestAgent",
        }));
        var coercedReadResponse = await server.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 7,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = McpToolNames.DiaryRead,
                ["arguments"] = new JsonObject
                {
                    ["agent_name"] = "TestAgent",
                    ["last_n"] = "1",
                },
            },
        });
        var empty = ToObject(await server.InvokeToolAsync(McpToolNames.DiaryRead, new JsonObject
        {
            ["agent_name"] = "Nobody",
        }));

        Assert.True(written["success"]!.GetValue<bool>());
        Assert.Equal(2, read["total"]!.GetValue<int>());
        Assert.Contains("architecture", ((JsonArray)read["entries"]!).Select(entry => entry!["topic"]!.GetValue<string>()));
        Assert.Equal(1, ParseToolText(coercedReadResponse!)["showing"]!.GetValue<int>());
        Assert.Empty((JsonArray)empty["entries"]!);
    }

    private static async Task<MemShackMcpServer> CreateServerAsync(
        TemporaryDirectory temp,
        bool seedPalace = false,
        bool seedKg = false)
    {
        var store = seedPalace
            ? await SeededPalaceFactory.CreateAsync(temp)
            : new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var kg = seedKg
            ? await KnowledgeGraphTestFactory.CreateSeededStoreAsync(temp)
            : KnowledgeGraphTestFactory.CreateStore(temp);

        return new MemShackMcpServer(CreateConfig(temp.GetPath("palace")), store, kg, new PalaceGraphBuilder());
    }

    private static MempalaceConfigSnapshot CreateConfig(string palacePath)
    {
        var hallKeywords = MempalaceDefaults.HallKeywords.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.ToArray(),
            StringComparer.Ordinal);

        return new MempalaceConfigSnapshot(
            palacePath,
            CollectionNames.Drawers,
            new Dictionary<string, string>(StringComparer.Ordinal),
            MempalaceDefaults.TopicWings.ToArray(),
            hallKeywords);
    }

    private static async Task SeedGraphDrawersAsync(ChromaCompatibilityVectorStore store, TemporaryDirectory temp)
    {
        await store.AddDrawerAsync(CollectionNames.Drawers, new DrawerRecord("drawer_project_backend", "Backend auth details", new DrawerMetadata
        {
            Wing = "project",
            Room = "backend",
            SourceFile = temp.GetPath("src", "backend.txt"),
            ChunkIndex = 0,
            AddedBy = "seed",
            FiledAt = "2026-04-07T09:00:00",
            Hall = "technical",
            Date = "2026-04-01",
        }));
        await store.AddDrawerAsync(CollectionNames.Drawers, new DrawerRecord("drawer_notes_backend", "Backend planning notes", new DrawerMetadata
        {
            Wing = "notes",
            Room = "backend",
            SourceFile = temp.GetPath("notes", "backend.txt"),
            ChunkIndex = 0,
            AddedBy = "seed",
            FiledAt = "2026-04-07T09:05:00",
            Hall = "planning",
            Date = "2026-04-02",
        }));
        await store.AddDrawerAsync(CollectionNames.Drawers, new DrawerRecord("drawer_notes_planning", "Roadmap planning notes", new DrawerMetadata
        {
            Wing = "notes",
            Room = "planning",
            SourceFile = temp.GetPath("notes", "roadmap.txt"),
            ChunkIndex = 0,
            AddedBy = "seed",
            FiledAt = "2026-04-07T09:10:00",
            Hall = "planning",
            Date = "2026-04-03",
        }));
    }

    private static JsonNode LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("mcp", name)))!;

    private static JsonObject ToObject(object? value) => (JsonObject)JsonSerializer.SerializeToNode(value)!;

    private static JsonArray ToArray(object? value) => (JsonArray)JsonSerializer.SerializeToNode(value)!;

    private static JsonNode ParseToolText(JsonObject response)
    {
        var content = (JsonArray)response["result"]!["content"]!;
        var text = content[0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!;
    }
}
