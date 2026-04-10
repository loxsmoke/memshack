using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemShack.Application.Chunking;
using MemShack.Application.Graphs;
using MemShack.Application.Mining;
using MemShack.Application.Normalization;
using MemShack.Application.Scanning;
using MemShack.Application.Search;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.Config.Projects;
using MemShack.Infrastructure.Sqlite.KnowledgeGraph;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.McpServer;
using MemShack.Tests.KnowledgeGraph;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Parity;

[TestClass]
public sealed class Palace2ShackParityTests
{
    public static IEnumerable<object[]> TranscriptFixtures =>
    [
        [
            FixturePaths.GetPhase0Path("transcripts", "plain-text-transcript.txt"),
            """
            > What is memory?
            Memory is persistence.

            > Why does it matter?
            It enables continuity.

            > How do we build it?
            With structured storage.
            """
        ],
        [
            FixturePaths.GetPhase0Path("transcripts", "claude-code-session.jsonl"),
            """
            > How should we store memory?
            Use structured local storage.
            """
        ],
        [
            FixturePaths.GetPhase0Path("transcripts", "codex-session.jsonl"),
            """
            > What did we decide about auth?
            We chose JWT tokens with refresh cookies.
            """
        ],
        [
            FixturePaths.GetPhase0Path("transcripts", "slack-export.json"),
            """
            > We should migrate the auth flow.
            Passkeys are a good candidate.

            > Lets document the tradeoffs.
            """
        ],
        [
            FixturePaths.GetPhase0Path("transcripts", "chatgpt-conversation.json"),
            """
            > Why did we switch to passkeys?
            We wanted phishing-resistant login with better UX.
            """
        ],
        [
            FixturePaths.GetPhase0Path("transcripts", "claude-flat-messages.json"),
            """
            > Hi
            Hello
            """
        ],
    ];

    [TestMethod]
    [Microsoft.VisualStudio.TestTools.UnitTesting.DynamicData(nameof(TranscriptFixtures))]
    public void TranscriptFixtures_NormalizeToFrozenOutputs(string fixturePath, string expected)
    {
        var normalizer = new TranscriptNormalizer();
        var content = File.ReadAllText(fixturePath);

        var actual = normalizer.NormalizeContent(content, Path.GetExtension(fixturePath));

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [TestMethod]
    public void ConversationChunker_ProducesExpectedExchangeChunks()
    {
        var chunker = new ConversationChunker();
        var transcript = File.ReadAllText(FixturePaths.GetPhase0Path("transcripts", "plain-text-transcript.txt"));

        var chunks = chunker.ChunkExchanges(transcript);

        Assert.Equal(
            [
                "> What is memory?\nMemory is persistence.",
                "> Why does it matter?\nIt enables continuity.",
                "> How do we build it?\nWith structured storage.",
            ],
            chunks.Select(chunk => Normalize(chunk.Content)).ToArray());
    }

    [TestMethod]
    public async Task ProjectCorpus_ScanningMiningRoomAssignmentAndDrawerMetadataStayStable()
    {
        using var temp = new TemporaryDirectory();
        var corpusPath = FixturePaths.GetPalace2ShackPath("project-corpus");
        var scanner = new ProjectScanner();

        var scannedFiles = scanner.ScanProject(corpusPath, respectGitignore: true)
            .Select(path => Path.GetRelativePath(corpusPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "backend/auth.py",
                "docs/architecture.md",
                "frontend/app.tsx",
                "notes/roadmap.md",
            ],
            scannedFiles);

        var run1Store = new ChromaCompatibilityVectorStore(temp.GetPath("palace-1"));
        var run2Store = new ChromaCompatibilityVectorStore(temp.GetPath("palace-2"));
        var miner1 = new ProjectMiner(new YamlProjectPalaceConfigLoader(), new ProjectScanner(), new TextChunker(), run1Store);
        var miner2 = new ProjectMiner(new YamlProjectPalaceConfigLoader(), new ProjectScanner(), new TextChunker(), run2Store);

        var run1 = await miner1.MineAsync(corpusPath, agent: "palace2shack");
        var run2 = await miner2.MineAsync(corpusPath, agent: "palace2shack");
        var drawers1 = await run1Store.GetDrawersAsync(CollectionNames.Drawers);
        var drawers2 = await run2Store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.Equal(4, run1.DrawersFiled);
        Assert.Equal(4, run2.DrawersFiled);
        Assert.Equal(4, drawers1.Count);
        Assert.Equal(4, drawers2.Count);
        Assert.Equal(Snapshot(drawers1, corpusPath), Snapshot(drawers2, corpusPath));
        Assert.All(drawers1, drawer => Assert.Matches(@"^drawer_product_[a-z]+_[a-f0-9]{16}$", drawer.Id));

        var bySource = drawers1.ToDictionary(
            drawer => Path.GetRelativePath(corpusPath, drawer.Metadata.SourceFile).Replace('\\', '/'),
            drawer => drawer,
            StringComparer.Ordinal);

        Assert.Equal("backend", bySource["backend/auth.py"].Metadata.Room);
        Assert.Equal("documentation", bySource["docs/architecture.md"].Metadata.Room);
        Assert.Equal("frontend", bySource["frontend/app.tsx"].Metadata.Room);
        Assert.Equal("planning", bySource["notes/roadmap.md"].Metadata.Room);
        Assert.All(drawers1, drawer => Assert.Equal("palace2shack", drawer.Metadata.AddedBy));
    }

    [TestMethod]
    public async Task KnowledgeGraphFixture_QueryResultsMatchFrozenExpectations()
    {
        using var temp = new TemporaryDirectory();
        var store = new SqliteKnowledgeGraphStore(temp.GetPath("kg.sqlite3"));
        var fixture = JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("kg", "seeded-kg.json")))!.AsObject();

        foreach (var entity in fixture["entities"]!.AsArray())
        {
            await store.AddEntityAsync(
                new EntityRecord(
                    entity!["name"]!.GetValue<string>(),
                    entity["type"]!.GetValue<string>()));
        }

        foreach (var triple in fixture["triples"]!.AsArray())
        {
            await store.AddTripleAsync(
                new TripleRecord(
                    triple!["subject"]!.GetValue<string>(),
                    triple["predicate"]!.GetValue<string>(),
                    triple["object"]!.GetValue<string>(),
                    triple["valid_from"]?.GetValue<string>(),
                    triple["valid_to"]?.GetValue<string>()));
        }

        var aliceOutgoing = (await store.QueryEntityAsync("Alice"))
            .Select(triple => $"{triple.Predicate}|{triple.Object}|{triple.ValidFrom}|{triple.ValidTo ?? string.Empty}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var worksAt2024 = await store.QueryRelationshipAsync("works_at", "2024-06-01");
        var worksAt2025 = await store.QueryRelationshipAsync("works_at", "2025-06-01");
        var maxTimeline = await store.TimelineAsync("Max");
        var stats = await store.StatsAsync();

        Assert.Equal(
            [
                "parent_of|Max|2015-04-01|",
                "works_at|Acme Corp|2020-01-01|2024-12-31",
                "works_at|NewCo|2025-01-01|",
            ],
            aliceOutgoing);
        Assert.Equal("Acme Corp", Assert.Single(worksAt2024).Object);
        Assert.Equal("NewCo", Assert.Single(worksAt2025).Object);
        Assert.Equal(3, maxTimeline.Count);
        Assert.Equal(6, stats.Entities);
        Assert.Equal(5, stats.Triples);
    }

    [TestMethod]
    public async Task SearchFixture_PreservesResultShapesAndFilters()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var fixture = JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("drawers", "seeded-drawers.json")))!.AsArray();

        foreach (var item in fixture)
        {
            var metadata = item!["metadata"]!.AsObject();
            await store.AddDrawerAsync(
                CollectionNames.Drawers,
                new DrawerRecord(
                    item["id"]!.GetValue<string>(),
                    item["text"]!.GetValue<string>(),
                    new DrawerMetadata
                    {
                        Wing = metadata["wing"]!.GetValue<string>(),
                        Room = metadata["room"]!.GetValue<string>(),
                        SourceFile = metadata["source_file"]!.GetValue<string>(),
                        ChunkIndex = metadata["chunk_index"]!.GetValue<int>(),
                        AddedBy = metadata["added_by"]!.GetValue<string>(),
                        FiledAt = metadata["filed_at"]!.GetValue<string>(),
                    }));
        }

        var service = new MemorySearchService(store, temp.GetPath("palace"));
        var authResults = await service.SearchMemoriesAsync("JWT authentication");
        var notesResults = await service.SearchMemoriesAsync("ChromaDB", wing: "notes");
        var backendResults = await service.SearchMemoriesAsync("authentication database", room: "backend", nResults: 5);

        var firstHit = Assert.Single(authResults.Results.Take(1));
        Assert.Equal("project", firstHit.Wing);
        Assert.Equal("backend", firstHit.Room);
        Assert.Equal("auth.py", firstHit.SourceFile);
        Assert.Equal("notes", Assert.Single(notesResults.Results).Wing);
        Assert.All(backendResults.Results, hit => Assert.Equal("backend", hit.Room));
        Assert.Equal(2, backendResults.Results.Count);
    }

    [TestMethod]
    public async Task McpStatusFixture_ResponseEnvelopeMatchesFrozenPayload()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var fixture = JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("drawers", "seeded-drawers.json")))!.AsArray();

        foreach (var item in fixture)
        {
            var metadata = item!["metadata"]!.AsObject();
            await store.AddDrawerAsync(
                CollectionNames.Drawers,
                new DrawerRecord(
                    item["id"]!.GetValue<string>(),
                    item["text"]!.GetValue<string>(),
                    new DrawerMetadata
                    {
                        Wing = metadata["wing"]!.GetValue<string>(),
                        Room = metadata["room"]!.GetValue<string>(),
                        SourceFile = metadata["source_file"]!.GetValue<string>(),
                        ChunkIndex = metadata["chunk_index"]!.GetValue<int>(),
                        AddedBy = metadata["added_by"]!.GetValue<string>(),
                        FiledAt = metadata["filed_at"]!.GetValue<string>(),
                    }));
        }

        var server = new MemShackMcpServer(CreateConfig(temp.GetPath("palace")), store, KnowledgeGraphTestFactory.CreateStore(temp), new PalaceGraphBuilder());
        var request = JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("mcp", "tools-call-status-request.json")))!.AsObject();
        var expected = JsonNode.Parse(File.ReadAllText(FixturePaths.GetPhase0Path("mcp", "tools-call-response-envelope.json")))!.AsObject();

        var response = await server.HandleRequestAsync(request);

        Assert.NotNull(response);
        Assert.Equal(expected["jsonrpc"]!.GetValue<string>(), response!["jsonrpc"]!.GetValue<string>());
        Assert.Equal(expected["id"]!.GetValue<int>(), response["id"]!.GetValue<int>());
        var expectedPayload = JsonNode.Parse(expected["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var actualPayload = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

        Assert.Equal(expectedPayload["total_drawers"]!.GetValue<int>(), actualPayload["total_drawers"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expectedPayload["wings"], actualPayload["wings"]));
        Assert.NotNull(actualPayload["rooms"]);
        Assert.NotNull(actualPayload["palace_path"]);
        Assert.NotNull(actualPayload["protocol"]);
        Assert.NotNull(actualPayload["aaak_dialect"]);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static IReadOnlyList<string> Snapshot(IEnumerable<DrawerRecord> drawers, string projectPath) =>
        drawers
            .OrderBy(drawer => drawer.Id, StringComparer.Ordinal)
            .Select(drawer =>
                $"{drawer.Id}|{drawer.Metadata.Wing}|{drawer.Metadata.Room}|{Path.GetRelativePath(projectPath, drawer.Metadata.SourceFile).Replace('\\', '/')}|{drawer.Metadata.ChunkIndex}|{drawer.Metadata.AddedBy}")
            .ToArray();

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
}
