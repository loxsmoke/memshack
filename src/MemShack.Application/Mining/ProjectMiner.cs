using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Mining;

public sealed class ProjectMiner
{
    private readonly IProjectPalaceConfigLoader _configLoader;
    private readonly IProjectScanner _projectScanner;
    private readonly ITextChunker _textChunker;
    private readonly IVectorStore _vectorStore;

    public ProjectMiner(
        IProjectPalaceConfigLoader configLoader,
        IProjectScanner projectScanner,
        ITextChunker textChunker,
        IVectorStore vectorStore)
    {
        _configLoader = configLoader;
        _projectScanner = projectScanner;
        _textChunker = textChunker;
        _vectorStore = vectorStore;
    }

    public async Task<MiningRunResult> MineAsync(
        string projectDirectory,
        string? wingOverride = null,
        string agent = "mempalace",
        int limit = 0,
        bool dryRun = false,
        bool respectGitignore = true,
        IEnumerable<string>? includeIgnored = null,
        string collectionName = CollectionNames.Drawers,
        CancellationToken cancellationToken = default)
    {
        var projectPath = Path.GetFullPath(projectDirectory);
        var config = _configLoader.Load(projectPath);
        var wing = string.IsNullOrWhiteSpace(wingOverride) ? config.Wing : wingOverride;
        var files = _projectScanner.ScanProject(projectPath, respectGitignore, includeIgnored);
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
            var result = await ProcessFileAsync(
                file,
                projectPath,
                wing,
                config.Rooms,
                agent,
                dryRun,
                collectionName,
                cancellationToken);

            if (result.DrawersAdded == 0 && !dryRun)
            {
                filesSkipped++;
                continue;
            }

            totalDrawers += result.DrawersAdded;
            if (result.DrawersAdded > 0)
            {
                roomCounts[result.Room] = roomCounts.TryGetValue(result.Room, out var count)
                    ? count + 1
                    : 1;
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

    private async Task<FileProcessingResult> ProcessFileAsync(
        string filePath,
        string projectPath,
        string wing,
        IReadOnlyList<RoomDefinition> rooms,
        string agent,
        bool dryRun,
        string collectionName,
        CancellationToken cancellationToken)
    {
        var sourceFile = Path.GetFullPath(filePath);
        if (!dryRun && await _vectorStore.HasSourceFileAsync(collectionName, sourceFile, cancellationToken))
        {
            return new FileProcessingResult(0, "general");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(sourceFile, cancellationToken);
        }
        catch (IOException)
        {
            return new FileProcessingResult(0, "general");
        }
        catch (UnauthorizedAccessException)
        {
            return new FileProcessingResult(0, "general");
        }

        content = content.Trim();
        if (content.Length < 50)
        {
            return new FileProcessingResult(0, "general");
        }

        var room = DetectRoom(sourceFile, content, rooms, projectPath);
        var chunks = _textChunker.ChunkText(content);
        if (dryRun)
        {
            return new FileProcessingResult(chunks.Count, room);
        }

        var drawersAdded = 0;
        foreach (var chunk in chunks)
        {
            var drawer = new DrawerRecord(
                MiningUtilities.CreateDrawerId(wing, room, sourceFile, chunk.ChunkIndex),
                chunk.Content,
                new DrawerMetadata
                {
                    Wing = wing,
                    Room = room,
                    SourceFile = sourceFile,
                    ChunkIndex = chunk.ChunkIndex,
                    AddedBy = agent,
                    FiledAt = MiningUtilities.NowIso(),
                });

            if (await _vectorStore.AddDrawerAsync(collectionName, drawer, cancellationToken))
            {
                drawersAdded++;
            }
        }

        return new FileProcessingResult(drawersAdded, room);
    }

    internal static string DetectRoom(
        string filePath,
        string content,
        IReadOnlyList<RoomDefinition> rooms,
        string projectPath)
    {
        var relative = Path.GetRelativePath(projectPath, filePath).Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        var contentLower = content.Length > 2000 ? content[..2000].ToLowerInvariant() : content.ToLowerInvariant();

        var pathParts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in pathParts.Take(Math.Max(pathParts.Length - 1, 0)))
        {
            foreach (var room in rooms)
            {
                var candidates = room.Keywords
                    .Concat([room.Name])
                    .Select(keyword => keyword.ToLowerInvariant())
                    .ToArray();

                if (candidates.Any(candidate => part == candidate || candidate.Contains(part, StringComparison.Ordinal) || part.Contains(candidate, StringComparison.Ordinal)))
                {
                    return room.Name;
                }
            }
        }

        foreach (var room in rooms)
        {
            if (room.Name.ToLowerInvariant().Contains(fileName, StringComparison.Ordinal) ||
                fileName.Contains(room.Name.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return room.Name;
            }
        }

        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            var score = 0;
            foreach (var keyword in room.Keywords.Concat([room.Name]))
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                score += CountOccurrences(contentLower, keyword.ToLowerInvariant());
            }

            scores[room.Name] = score;
        }

        if (scores.Count > 0)
        {
            var best = scores.OrderByDescending(pair => pair.Value).First();
            if (best.Value > 0)
            {
                return best.Key;
            }
        }

        return "general";
    }

    private static int CountOccurrences(string content, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed record FileProcessingResult(int DrawersAdded, string Room);
}
