using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemShack.Application.Graphs;
using MemShack.Application.Search;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;
using MemShack.Infrastructure.Config;
using MemShack.Infrastructure.Sqlite.KnowledgeGraph;
using MemShack.Infrastructure.VectorStore;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private const string ServerName = "mempalace";
    private const string ServerVersion = "3.0.0";
    private const string PalaceProtocol = """
IMPORTANT - MemPalace Memory Protocol:
1. ON WAKE-UP: Call mempalace_status to load palace overview + AAAK spec.
2. BEFORE RESPONDING about any person, project, or past event: call mempalace_kg_query or mempalace_search FIRST. Never guess - verify.
3. IF UNSURE about a fact (name, gender, age, relationship): say "let me check" and query the palace. Wrong is worse than slow.
4. AFTER EACH SESSION: call mempalace_diary_write to record what happened, what you learned, what matters.
5. WHEN FACTS CHANGE: call mempalace_kg_invalidate on the old fact, mempalace_kg_add for the new one.

This protocol ensures the AI KNOWS before it speaks. Storage is not memory - but storage + this protocol = memory.
""";
    private const string AaakSpec = """
AAAK is a compressed memory dialect that MemPalace uses for efficient storage.
It is designed to be readable by both humans and LLMs without decoding.

FORMAT:
  ENTITIES: 3-letter uppercase codes. ALC=Alice, JOR=Jordan, RIL=Riley, MAX=Max, BEN=Ben.
  EMOTIONS: *action markers* before/during text. *warm*=joy, *fierce*=determined, *raw*=vulnerable, *bloom*=tenderness.
  STRUCTURE: Pipe-separated fields. FAM: family | PROJ: projects | !: warnings/reminders.
  DATES: ISO format (2026-03-31). COUNTS: Nx = N mentions (e.g., 570x).
  IMPORTANCE: 1-5 scale.
  HALLS: hall_facts, hall_events, hall_discoveries, hall_preferences, hall_advice.
  WINGS: wing_user, wing_agent, wing_team, wing_code, wing_myproject, wing_hardware, wing_ue5, wing_ai_research.
  ROOMS: Hyphenated slugs representing named ideas (e.g., chromadb-setup, gpu-pricing).

EXAMPLE:
  FAM: ALC->JOR | 2D(kids): RIL(18,sports) MAX(11,chess+swimming) | BEN(contributor)

Read AAAK naturally - expand codes mentally, treat *markers* as emotional context.
When WRITING AAAK: use entity codes, mark emotions, keep structure tight.
""";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new();
    private static readonly string[] SupportedProtocolVersions =
    [
        "2025-11-25",
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    private static readonly Regex TokenPattern = new(@"\b[a-z0-9_]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly MempalaceConfigSnapshot _config;
    private readonly IPalaceGraphBuilder _graphBuilder;
    private readonly IKnowledgeGraphStore _knowledgeGraphStore;
    private readonly IReadOnlyDictionary<string, McpToolDefinition> _toolLookup;
    private readonly IReadOnlyList<McpToolDefinition> _tools;
    private readonly IVectorStore _vectorStore;

    public MemShackMcpServer(
        MempalaceConfigSnapshot config,
        IVectorStore vectorStore,
        IKnowledgeGraphStore knowledgeGraphStore,
        IPalaceGraphBuilder graphBuilder)
    {
        _config = config;
        _vectorStore = vectorStore;
        _knowledgeGraphStore = knowledgeGraphStore;
        _graphBuilder = graphBuilder;

        _tools = CreateTools();
        _toolLookup = _tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpToolDefinition> Tools => _tools;

    public static MemShackMcpServer CreateDefault(string? configDirectory = null, string? knowledgeGraphPath = null, string? palacePath = null)
    {
        var configStore = new FileConfigStore();
        var config = configStore.Load(configDirectory);
        if (!string.IsNullOrWhiteSpace(palacePath))
        {
            config = config with
            {
                PalacePath = Path.GetFullPath(PathUtilities.ExpandHome(palacePath)),
            };
        }

        var resolvedKnowledgeGraphPath = knowledgeGraphPath;
        if (string.IsNullOrWhiteSpace(resolvedKnowledgeGraphPath) && !string.IsNullOrWhiteSpace(palacePath))
        {
            resolvedKnowledgeGraphPath = Path.Combine(config.PalacePath, ConfigFileNames.KnowledgeGraphSqlite);
        }

        return new MemShackMcpServer(
            config,
            VectorStoreFactory.Create(config),
            new SqliteKnowledgeGraphStore(resolvedKnowledgeGraphPath),
            new PalaceGraphBuilder());
    }

    public async Task RunAsync(
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        await stderr.WriteLineAsync("MemPalace MCP Server starting...");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync();
            }
            catch (Exception exception)
            {
                await stderr.WriteLineAsync($"Server error: {exception.Message}");
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var request = JsonNode.Parse(line) as JsonObject;
                if (request is null)
                {
                    await stderr.WriteLineAsync("Server error: invalid JSON-RPC request");
                    continue;
                }

                var response = await HandleRequestAsync(request, cancellationToken);
                if (response is not null)
                {
                    await stdout.WriteLineAsync(response.ToJsonString(CompactJsonOptions));
                    await stdout.FlushAsync();
                }
            }
            catch (Exception exception)
            {
                await stderr.WriteLineAsync($"Server error: {exception.Message}");
            }
        }
    }

    public async Task<JsonObject?> HandleRequestAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var method = request["method"]?.GetValue<string>() ?? string.Empty;
        var requestId = request["id"]?.DeepClone();
        var parameters = request["params"] as JsonObject ?? new JsonObject();

        return method switch
        {
            "initialize" => CreateSuccessResponse(
                requestId,
                new JsonObject
                {
                    ["protocolVersion"] = NegotiateProtocolVersion(parameters),
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion,
                    },
                }),
            "notifications/initialized" => null,
            "tools/list" => CreateSuccessResponse(requestId, CreateToolsListResult()),
            "tools/call" => await HandleToolCallAsync(requestId, parameters, cancellationToken),
            _ => CreateErrorResponse(requestId, -32601, $"Unknown method: {method}"),
        };
    }

    public async Task<object?> InvokeToolAsync(
        string toolName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!_toolLookup.TryGetValue(toolName, out var tool))
        {
            throw new InvalidOperationException($"Unknown tool: {toolName}");
        }

        var coercedArguments = CoerceArguments(arguments ?? new JsonObject(), tool.InputSchema);
        return await tool.Handler(coercedArguments, cancellationToken);
    }

    private async Task<JsonObject> HandleToolCallAsync(
        JsonNode? requestId,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var toolName = parameters["name"]?.GetValue<string>() ?? string.Empty;
        if (!_toolLookup.ContainsKey(toolName))
        {
            return CreateErrorResponse(requestId, -32601, $"Unknown tool: {toolName}");
        }

        try
        {
            var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
            var result = await InvokeToolAsync(toolName, arguments, cancellationToken);

            return CreateSuccessResponse(
                requestId,
                new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = SerializeToolResult(result),
                        },
                    },
                });
        }
        catch (Exception)
        {
            return CreateErrorResponse(requestId, -32000, "Internal tool error");
        }
    }

    private JsonObject CreateToolsListResult()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools)
        {
            tools.Add(tool.ToJson());
        }

        return new JsonObject
        {
            ["tools"] = tools,
        };
    }

    private IReadOnlyList<McpToolDefinition> CreateTools()
    {
        return
        [
            new McpToolDefinition(McpToolNames.Status, "Palace overview - total drawers, wing and room counts", EmptySchema(), ToolStatusAsync),
            new McpToolDefinition(McpToolNames.ListWings, "List all wings with drawer counts", EmptySchema(), ToolListWingsAsync),
            new McpToolDefinition(McpToolNames.ListRooms, "List rooms within a wing (or all rooms if no wing given)", Schema(Array.Empty<string>(), new SchemaProperty("wing", "string", "Wing to list rooms for (optional)")), ToolListRoomsAsync),
            new McpToolDefinition(McpToolNames.GetTaxonomy, "Full taxonomy: wing -> room -> drawer count", EmptySchema(), ToolGetTaxonomyAsync),
            new McpToolDefinition(McpToolNames.GetAaakSpec, "Get the AAAK dialect specification - the compressed memory format MemPalace uses.", EmptySchema(), ToolGetAaakSpecAsync),
            new McpToolDefinition(McpToolNames.KgQuery, "Query the knowledge graph for an entity's relationships.", Schema(new[] { "entity" }, new SchemaProperty("entity", "string", "Entity to query"), new SchemaProperty("as_of", "string", "Date filter (YYYY-MM-DD, optional)"), new SchemaProperty("direction", "string", "outgoing, incoming, or both")), ToolKnowledgeGraphQueryAsync),
            new McpToolDefinition(McpToolNames.KgAdd, "Add a fact to the knowledge graph.", Schema(new[] { "subject", "predicate", "object" }, new SchemaProperty("subject", "string", "The entity doing or being something"), new SchemaProperty("predicate", "string", "Relationship type"), new SchemaProperty("object", "string", "Connected entity"), new SchemaProperty("valid_from", "string", "When this became true (optional)"), new SchemaProperty("source_closet", "string", "Closet id where this fact appears (optional)")), ToolKnowledgeGraphAddAsync),
            new McpToolDefinition(McpToolNames.KgInvalidate, "Mark a fact as no longer true.", Schema(new[] { "subject", "predicate", "object" }, new SchemaProperty("subject", "string", "Entity"), new SchemaProperty("predicate", "string", "Relationship"), new SchemaProperty("object", "string", "Connected entity"), new SchemaProperty("ended", "string", "When it stopped being true (optional)")), ToolKnowledgeGraphInvalidateAsync),
            new McpToolDefinition(McpToolNames.KgTimeline, "Chronological timeline of facts.", Schema(Array.Empty<string>(), new SchemaProperty("entity", "string", "Entity to get timeline for (optional)")), ToolKnowledgeGraphTimelineAsync),
            new McpToolDefinition(McpToolNames.KgStats, "Knowledge graph overview: entities, triples, current vs expired facts, relationship types.", EmptySchema(), ToolKnowledgeGraphStatsAsync),
            new McpToolDefinition(McpToolNames.Traverse, "Walk the palace graph from a room and show connected ideas across wings.", Schema(new[] { "start_room" }, new SchemaProperty("start_room", "string", "Room to start from"), new SchemaProperty("max_hops", "integer", "How many connections to follow (default: 2)")), ToolTraverseAsync),
            new McpToolDefinition(McpToolNames.FindTunnels, "Find rooms that bridge two wings.", Schema(Array.Empty<string>(), new SchemaProperty("wing_a", "string", "First wing (optional)"), new SchemaProperty("wing_b", "string", "Second wing (optional)")), ToolFindTunnelsAsync),
            new McpToolDefinition(McpToolNames.GraphStats, "Palace graph overview: total rooms, tunnel connections, edges between wings.", EmptySchema(), ToolGraphStatsAsync),
            new McpToolDefinition(McpToolNames.Search, "Semantic search. Returns verbatim drawer content with similarity scores.", Schema(new[] { "query" }, new SchemaProperty("query", "string", "What to search for"), new SchemaProperty("limit", "integer", "Max results (default 5)"), new SchemaProperty("wing", "string", "Filter by wing (optional)"), new SchemaProperty("room", "string", "Filter by room (optional)")), ToolSearchAsync),
            new McpToolDefinition(McpToolNames.CheckDuplicate, "Check if content already exists in the palace before filing.", Schema(new[] { "content" }, new SchemaProperty("content", "string", "Content to check"), new SchemaProperty("threshold", "number", "Similarity threshold 0-1 (default 0.9). Higher is stricter. MemShack reports similarity, not Chroma cosine distance.")), ToolCheckDuplicateAsync),
            new McpToolDefinition(McpToolNames.AddDrawer, "File verbatim content into the palace. Checks for duplicates first.", Schema(new[] { "wing", "room", "content" }, new SchemaProperty("wing", "string", "Wing (project name)"), new SchemaProperty("room", "string", "Room (aspect)"), new SchemaProperty("content", "string", "Verbatim content to store"), new SchemaProperty("source_file", "string", "Where this came from (optional)"), new SchemaProperty("added_by", "string", "Who is filing this (default: mcp)")), ToolAddDrawerAsync),
            new McpToolDefinition(McpToolNames.DeleteDrawer, "Delete a drawer by ID. Irreversible.", Schema(new[] { "drawer_id" }, new SchemaProperty("drawer_id", "string", "ID of the drawer to delete")), ToolDeleteDrawerAsync),
            new McpToolDefinition(McpToolNames.DiaryWrite, "Write to the agent diary in AAAK format.", Schema(new[] { "agent_name", "entry" }, new SchemaProperty("agent_name", "string", "Agent name"), new SchemaProperty("entry", "string", "Diary entry content"), new SchemaProperty("topic", "string", "Topic tag (optional)")), ToolDiaryWriteAsync),
            new McpToolDefinition(McpToolNames.DiaryRead, "Read recent diary entries for an agent.", Schema(new[] { "agent_name" }, new SchemaProperty("agent_name", "string", "Agent name"), new SchemaProperty("last_n", "integer", "Number of recent entries to read (default: 10)")), ToolDiaryReadAsync),
        ];
    }

    private readonly record struct SchemaProperty(string Name, string Type, string Description);

    private static string NegotiateProtocolVersion(JsonObject parameters)
    {
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(requested) &&
            SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal))
        {
            return requested;
        }

        return SupportedProtocolVersions[0];
    }
}
