using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Mining;

public sealed class ConversationMiner
{
    private static readonly HashSet<string> ConversationExtensions =
    [
        ".txt",
        ".md",
        ".json",
        ".jsonl",
    ];

    private static readonly HashSet<string> SkipDirectories =
    [
        ".git",
        "node_modules",
        "__pycache__",
        ".venv",
        "venv",
        "env",
        "dist",
        "build",
        ".next",
        ".mempalace",
        "tool-results",
        "memory",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TopicKeywords =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["technical"] = ["code", "python", "function", "bug", "error", "api", "database", "server", "deploy", "git", "test", "debug", "refactor"],
            ["architecture"] = ["architecture", "design", "pattern", "structure", "schema", "interface", "module", "component", "service", "layer"],
            ["planning"] = ["plan", "roadmap", "milestone", "deadline", "priority", "sprint", "backlog", "scope", "requirement", "spec"],
            ["decisions"] = ["decided", "chose", "picked", "switched", "migrated", "replaced", "trade-off", "alternative", "option", "approach"],
            ["problems"] = ["problem", "issue", "broken", "failed", "crash", "stuck", "workaround", "fix", "solved", "resolved"],
        };

    private readonly IConversationChunker _conversationChunker;
    private readonly IGeneralMemoryExtractor _generalMemoryExtractor;
    private readonly ITranscriptNormalizer _transcriptNormalizer;
    private readonly IVectorStore _vectorStore;

    public ConversationMiner(
        ITranscriptNormalizer transcriptNormalizer,
        IConversationChunker conversationChunker,
        IGeneralMemoryExtractor generalMemoryExtractor,
        IVectorStore vectorStore)
    {
        _transcriptNormalizer = transcriptNormalizer;
        _conversationChunker = conversationChunker;
        _generalMemoryExtractor = generalMemoryExtractor;
        _vectorStore = vectorStore;
    }

    public async Task<MiningRunResult> MineAsync(
        string conversationDirectory,
        string? wing = null,
        string agent = "mempalace",
        int limit = 0,
        bool dryRun = false,
        string extractMode = "exchange",
        string collectionName = CollectionNames.Drawers,
        CancellationToken cancellationToken = default)
    {
        var convoPath = Path.GetFullPath(conversationDirectory);
        var resolvedWing = string.IsNullOrWhiteSpace(wing)
            ? MiningUtilities.NormalizeWingName(Path.GetFileName(convoPath))
            : wing;

        var files = ScanConversationFiles(convoPath);
        if (limit > 0)
        {
            files = files.Take(limit).ToArray();
        }

        if (!dryRun)
        {
            await _vectorStore.EnsureCollectionAsync(collectionName, cancellationToken);
        }

        var totalDrawers = 0;
        var filesSkipped = 0;
        var roomCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var result = await ProcessConversationAsync(
                file,
                resolvedWing,
                agent,
                dryRun,
                extractMode,
                collectionName,
                cancellationToken);

            if (result.DrawersAdded == 0 && !dryRun)
            {
                filesSkipped++;
                continue;
            }

            totalDrawers += result.DrawersAdded;
            foreach (var entry in result.RoomCounts)
            {
                roomCounts[entry.Key] = roomCounts.TryGetValue(entry.Key, out var count)
                    ? count + entry.Value
                    : entry.Value;
            }
        }

        return new MiningRunResult(
            files.Count,
            files.Count - filesSkipped,
            filesSkipped,
            totalDrawers,
            roomCounts,
            dryRun);
    }

    public static IReadOnlyList<string> ScanConversationFiles(string conversationDirectory)
    {
        var files = new List<string>();
        Walk(conversationDirectory);
        return files.OrderBy(path => path, StringComparer.Ordinal).ToArray();

        void Walk(string directory)
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(childDirectory);
                if (SkipDirectories.Contains(name))
                {
                    continue;
                }

                Walk(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ConversationExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
        }
    }

    internal static string DetectConversationRoom(string content)
    {
        var lowered = content.Length > 3000 ? content[..3000].ToLowerInvariant() : content.ToLowerInvariant();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in TopicKeywords)
        {
            var score = entry.Value.Count(keyword => lowered.Contains(keyword, StringComparison.Ordinal));
            if (score > 0)
            {
                scores[entry.Key] = score;
            }
        }

        return scores.Count > 0
            ? scores.OrderByDescending(pair => pair.Value).First().Key
            : "general";
    }

    private async Task<ConversationProcessingResult> ProcessConversationAsync(
        string filePath,
        string wing,
        string agent,
        bool dryRun,
        string extractMode,
        string collectionName,
        CancellationToken cancellationToken)
    {
        var sourceFile = Path.GetFullPath(filePath);
        if (!dryRun && await _vectorStore.HasSourceFileAsync(collectionName, sourceFile, cancellationToken))
        {
            return new ConversationProcessingResult(0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        string normalizedContent;
        try
        {
            normalizedContent = _transcriptNormalizer.NormalizeFromFile(sourceFile);
        }
        catch (InvalidOperationException)
        {
            return new ConversationProcessingResult(0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(normalizedContent) || normalizedContent.Trim().Length < 30)
        {
            return new ConversationProcessingResult(0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        var roomCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.Equals(extractMode, "general", StringComparison.Ordinal))
        {
            var memories = _generalMemoryExtractor.ExtractMemories(normalizedContent);
            if (dryRun)
            {
                foreach (var memory in memories)
                {
                    roomCounts[memory.MemoryType] = roomCounts.TryGetValue(memory.MemoryType, out var count)
                        ? count + 1
                        : 1;
                }

                return new ConversationProcessingResult(memories.Count, roomCounts);
            }

            var added = 0;
            foreach (var memory in memories)
            {
                var room = memory.MemoryType;
                roomCounts[room] = roomCounts.TryGetValue(room, out var count) ? count + 1 : 1;

                var drawer = new DrawerRecord(
                    MiningUtilities.CreateDrawerId(wing, room, sourceFile, memory.ChunkIndex),
                    memory.Content,
                    new DrawerMetadata
                    {
                        Wing = wing,
                        Room = room,
                        SourceFile = sourceFile,
                        ChunkIndex = memory.ChunkIndex,
                        AddedBy = agent,
                        FiledAt = MiningUtilities.NowIso(),
                        IngestMode = "convos",
                        ExtractMode = extractMode,
                    });

                if (await _vectorStore.AddDrawerAsync(collectionName, drawer, cancellationToken))
                {
                    added++;
                }
            }

            return new ConversationProcessingResult(added, roomCounts);
        }

        var roomName = DetectConversationRoom(normalizedContent);
        var chunks = _conversationChunker.ChunkExchanges(normalizedContent);

        if (dryRun)
        {
            roomCounts[roomName] = 1;
            return new ConversationProcessingResult(chunks.Count, roomCounts);
        }

        var drawersAdded = 0;
        foreach (var chunk in chunks)
        {
            var drawer = new DrawerRecord(
                MiningUtilities.CreateDrawerId(wing, roomName, sourceFile, chunk.ChunkIndex),
                chunk.Content,
                new DrawerMetadata
                {
                    Wing = wing,
                    Room = roomName,
                    SourceFile = sourceFile,
                    ChunkIndex = chunk.ChunkIndex,
                    AddedBy = agent,
                    FiledAt = MiningUtilities.NowIso(),
                    IngestMode = "convos",
                    ExtractMode = extractMode,
                });

            if (await _vectorStore.AddDrawerAsync(collectionName, drawer, cancellationToken))
            {
                drawersAdded++;
            }
        }

        roomCounts[roomName] = 1;
        return new ConversationProcessingResult(drawersAdded, roomCounts);
    }

    private sealed record ConversationProcessingResult(int DrawersAdded, IReadOnlyDictionary<string, int> RoomCounts);
}
