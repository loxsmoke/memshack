using System.Text.Json;
using System.Text.Json.Nodes;
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

var outputPath = ParseOutputPath(args);
var snapshot = await BuildSnapshotAsync();
var json = snapshot.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
});

if (string.IsNullOrWhiteSpace(outputPath))
{
    Console.WriteLine(json);
    return;
}

var fullOutputPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
await File.WriteAllTextAsync(fullOutputPath, json);

static async Task<JsonObject> BuildSnapshotAsync()
{
    var repoRoot = FindRepoRoot();
    var phase0Root = Path.Combine(repoRoot, "fixtures", "phase0");
    var palace2ShackRoot = Path.Combine(repoRoot, "fixtures", "palace2shack");

    return new JsonObject
    {
        ["transcripts"] = BuildTranscriptSnapshot(phase0Root),
        ["conversation_chunks"] = BuildConversationChunkSnapshot(phase0Root),
        ["project_scan"] = BuildProjectScanSnapshot(palace2ShackRoot),
        ["project_mining"] = await BuildProjectMiningSnapshotAsync(palace2ShackRoot),
        ["knowledge_graph"] = await BuildKnowledgeGraphSnapshotAsync(phase0Root),
        ["search"] = await BuildSearchSnapshotAsync(phase0Root),
        ["mcp_status"] = await BuildMcpStatusSnapshotAsync(phase0Root),
    };
}

static JsonObject BuildTranscriptSnapshot(string phase0Root)
{
    var normalizer = new TranscriptNormalizer();
    var transcriptDirectory = Path.Combine(phase0Root, "transcripts");
    var fileNames = new[]
    {
        "plain-text-transcript.txt",
        "claude-code-session.jsonl",
        "codex-session.jsonl",
        "slack-export.json",
        "chatgpt-conversation.json",
        "claude-flat-messages.json",
    };

    var snapshot = new JsonObject();
    foreach (var fileName in fileNames)
    {
        var path = Path.Combine(transcriptDirectory, fileName);
        var content = File.ReadAllText(path);
        snapshot[fileName] = NormalizeLineEndings(normalizer.NormalizeContent(content, Path.GetExtension(path)));
    }

    return snapshot;
}

static JsonArray BuildConversationChunkSnapshot(string phase0Root)
{
    var chunker = new ConversationChunker();
    var transcriptPath = Path.Combine(phase0Root, "transcripts", "plain-text-transcript.txt");
    var transcript = File.ReadAllText(transcriptPath);
    var snapshot = new JsonArray();

    foreach (var chunk in chunker.ChunkExchanges(transcript))
    {
        snapshot.Add(NormalizeLineEndings(chunk.Content));
    }

    return snapshot;
}

static JsonArray BuildProjectScanSnapshot(string palace2ShackRoot)
{
    var corpusPath = Path.Combine(palace2ShackRoot, "project-corpus");
    var scanner = new ProjectScanner();
    var files = scanner.ScanProject(corpusPath, respectGitignore: true)
        .Select(path => Path.GetRelativePath(corpusPath, path).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.Ordinal);

    var snapshot = new JsonArray();
    foreach (var file in files)
    {
        snapshot.Add(file);
    }

    return snapshot;
}

static async Task<JsonObject> BuildProjectMiningSnapshotAsync(string palace2ShackRoot)
{
    using var temp = new TemporaryDirectory();
    var corpusPath = Path.Combine(palace2ShackRoot, "project-corpus");
    var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
    var miner = new ProjectMiner(new YamlProjectPalaceConfigLoader(), new ProjectScanner(), new TextChunker(), store);
    var result = await miner.MineAsync(corpusPath, agent: "palace2shack-live");
    var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

    var drawerSnapshots = new JsonArray();
    foreach (var drawer in drawers.OrderBy(item => item.Id, StringComparer.Ordinal))
    {
        drawerSnapshots.Add(
            $"{drawer.Id}|{drawer.Metadata.Wing}|{drawer.Metadata.Room}|{Path.GetRelativePath(corpusPath, drawer.Metadata.SourceFile).Replace('\\', '/')}|{drawer.Metadata.ChunkIndex}|{drawer.Metadata.AddedBy}");
    }

    return new JsonObject
    {
        ["drawers_filed"] = result.DrawersFiled,
        ["drawer_snapshots"] = drawerSnapshots,
    };
}

static async Task<JsonObject> BuildKnowledgeGraphSnapshotAsync(string phase0Root)
{
    using var temp = new TemporaryDirectory();
    var store = new SqliteKnowledgeGraphStore(temp.GetPath("knowledge_graph.sqlite3"));
    var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(phase0Root, "kg", "seeded-kg.json")))!.AsObject();

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
        .Select(FormatTriple)
        .OrderBy(value => value, StringComparer.Ordinal);
    var worksAt2024 = (await store.QueryRelationshipAsync("works_at", "2024-06-01"))
        .Select(FormatTriple)
        .OrderBy(value => value, StringComparer.Ordinal);
    var worksAt2025 = (await store.QueryRelationshipAsync("works_at", "2025-06-01"))
        .Select(FormatTriple)
        .OrderBy(value => value, StringComparer.Ordinal);
    var maxTimeline = (await store.TimelineAsync("Max"))
        .Select(FormatTriple)
        .OrderBy(value => value, StringComparer.Ordinal);
    var stats = await store.StatsAsync();

    return new JsonObject
    {
        ["alice_outgoing"] = ToJsonArray(aliceOutgoing),
        ["works_at_2024"] = ToJsonArray(worksAt2024),
        ["works_at_2025"] = ToJsonArray(worksAt2025),
        ["max_timeline"] = ToJsonArray(maxTimeline),
        ["stats"] = new JsonObject
        {
            ["entities"] = stats.Entities,
            ["triples"] = stats.Triples,
            ["current_facts"] = stats.CurrentFacts,
            ["expired_facts"] = stats.ExpiredFacts,
            ["relationship_types"] = ToJsonArray(stats.RelationshipTypes),
        },
    };
}

static async Task<JsonObject> BuildSearchSnapshotAsync(string phase0Root)
{
    using var temp = new TemporaryDirectory();
    var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
    var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(phase0Root, "drawers", "seeded-drawers.json")))!.AsArray();

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
    var auth = await service.SearchMemoriesAsync("JWT authentication");
    var notes = await service.SearchMemoriesAsync("ChromaDB", wing: "notes");
    var backend = await service.SearchMemoriesAsync("authentication database", room: "backend", nResults: 5);

    return new JsonObject
    {
        ["auth"] = BuildSearchHitArray(auth.Results),
        ["notes"] = BuildSearchHitArray(notes.Results),
        ["backend"] = BuildSearchHitArray(backend.Results),
    };
}

static async Task<JsonObject> BuildMcpStatusSnapshotAsync(string phase0Root)
{
    using var temp = new TemporaryDirectory();
    var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
    var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(phase0Root, "drawers", "seeded-drawers.json")))!.AsArray();

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

    var server = new MemShackMcpServer(CreateConfig(temp.GetPath("palace")), store, new SqliteKnowledgeGraphStore(temp.GetPath("kg.sqlite3")), new PalaceGraphBuilder());
    var request = JsonNode.Parse(File.ReadAllText(Path.Combine(phase0Root, "mcp", "tools-call-status-request.json")))!.AsObject();
    var response = await server.HandleRequestAsync(request);
    var payload = JsonNode.Parse(response!["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    return new JsonObject
    {
        ["total_drawers"] = payload["total_drawers"]!.GetValue<int>(),
        ["wings"] = payload["wings"]!.DeepClone(),
        ["rooms"] = payload["rooms"]!.DeepClone(),
        ["has_protocol"] = !string.IsNullOrWhiteSpace(payload["protocol"]?.GetValue<string>()),
        ["has_aaak_dialect"] = !string.IsNullOrWhiteSpace(payload["aaak_dialect"]?.GetValue<string>()),
    };
}

static JsonArray BuildSearchHitArray(IReadOnlyList<SearchHit> hits)
{
    var array = new JsonArray();
    foreach (var hit in hits)
    {
        array.Add(new JsonObject
        {
            ["wing"] = hit.Wing,
            ["room"] = hit.Room,
            ["source_file"] = hit.SourceFile,
            ["similarity"] = Math.Round(hit.Similarity, 3),
            ["text"] = hit.Text,
        });
    }

    return array;
}

static JsonArray ToJsonArray(IEnumerable<string> values)
{
    var array = new JsonArray();
    foreach (var value in values)
    {
        array.Add(value);
    }

    return array;
}

static string FormatTriple(TripleRecord triple) =>
    $"{triple.Subject}|{triple.Predicate}|{triple.Object}|{triple.ValidFrom ?? string.Empty}|{triple.ValidTo ?? string.Empty}";

static MempalaceConfigSnapshot CreateConfig(string palacePath)
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

static string NormalizeLineEndings(string value) =>
    value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

static string FindRepoRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "fixtures")) &&
            Directory.Exists(Path.Combine(current.FullName, "src")) &&
            File.Exists(Path.Combine(current.FullName, "MemShack.slnx")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the MemShack repo root from the parity runner output directory.");
}

static string? ParseOutputPath(IReadOnlyList<string> arguments)
{
    if (arguments.Count == 0)
    {
        return null;
    }

    if (arguments.Count == 2 && string.Equals(arguments[0], "--output", StringComparison.Ordinal))
    {
        return arguments[1];
    }

    throw new ArgumentException("Usage: MemShack.ParityRunner [--output <path>]");
}

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "memshack-palace2shack-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetPath(params string[] segments)
    {
        var parts = new[] { RootPath }.Concat(segments).ToArray();
        return Path.Combine(parts);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
