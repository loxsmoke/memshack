using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Rooms;

public sealed class LocalRoomDetector : IRoomDetector
{
    private static readonly Dictionary<string, string> FolderRoomMap = new(StringComparer.Ordinal)
    {
        ["frontend"] = "frontend",
        ["front_end"] = "frontend",
        ["front-end"] = "frontend",
        ["client"] = "frontend",
        ["ui"] = "frontend",
        ["views"] = "frontend",
        ["components"] = "frontend",
        ["pages"] = "frontend",
        ["backend"] = "backend",
        ["back_end"] = "backend",
        ["back-end"] = "backend",
        ["server"] = "backend",
        ["api"] = "backend",
        ["routes"] = "backend",
        ["services"] = "backend",
        ["controllers"] = "backend",
        ["models"] = "backend",
        ["database"] = "backend",
        ["db"] = "backend",
        ["docs"] = "documentation",
        ["doc"] = "documentation",
        ["documentation"] = "documentation",
        ["wiki"] = "documentation",
        ["readme"] = "documentation",
        ["notes"] = "documentation",
        ["design"] = "design",
        ["designs"] = "design",
        ["mockups"] = "design",
        ["wireframes"] = "design",
        ["assets"] = "design",
        ["storyboard"] = "design",
        ["costs"] = "costs",
        ["cost"] = "costs",
        ["budget"] = "costs",
        ["finance"] = "costs",
        ["financial"] = "costs",
        ["pricing"] = "costs",
        ["invoices"] = "costs",
        ["accounting"] = "costs",
        ["meetings"] = "meetings",
        ["meeting"] = "meetings",
        ["calls"] = "meetings",
        ["meeting_notes"] = "meetings",
        ["standup"] = "meetings",
        ["minutes"] = "meetings",
        ["team"] = "team",
        ["staff"] = "team",
        ["hr"] = "team",
        ["hiring"] = "team",
        ["employees"] = "team",
        ["people"] = "team",
        ["research"] = "research",
        ["references"] = "research",
        ["reading"] = "research",
        ["papers"] = "research",
        ["planning"] = "planning",
        ["roadmap"] = "planning",
        ["strategy"] = "planning",
        ["specs"] = "planning",
        ["requirements"] = "planning",
        ["tests"] = "testing",
        ["test"] = "testing",
        ["testing"] = "testing",
        ["qa"] = "testing",
        ["scripts"] = "scripts",
        ["tools"] = "scripts",
        ["utils"] = "scripts",
        ["config"] = "configuration",
        ["configs"] = "configuration",
        ["settings"] = "configuration",
        ["infrastructure"] = "configuration",
        ["infra"] = "configuration",
        ["deploy"] = "configuration",
    };

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
        "coverage",
        ".dotnet",
        ".nuget",
        "bin",
        "obj",
    ];

    private static readonly IReadOnlyDictionary<string, int> PreferredRoomOrder =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src"] = 0,
            ["backend"] = 1,
            ["frontend"] = 2,
            ["design"] = 3,
            ["scripts"] = 4,
            ["fixtures"] = 5,
            ["testing"] = 6,
            ["documentation"] = 7,
            ["configuration"] = 8,
            ["planning"] = 9,
            ["research"] = 10,
            ["team"] = 11,
            ["meetings"] = 12,
            ["costs"] = 13,
        };

    public IReadOnlyList<RoomDefinition> DetectRoomsFromFolders(string projectDirectory)
    {
        var projectPath = new DirectoryInfo(Path.GetFullPath(projectDirectory));
        var foundRooms = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!projectPath.Exists)
        {
            return [GeneralRoom("All project files")];
        }

        foreach (var directory in projectPath.EnumerateDirectories())
        {
            if (SkipDirectories.Contains(directory.Name))
            {
                continue;
            }

            AddRoomCandidate(foundRooms, directory.Name);
        }

        foreach (var directory in projectPath.EnumerateDirectories())
        {
            if (SkipDirectories.Contains(directory.Name))
            {
                continue;
            }

            foreach (var subDirectory in directory.EnumerateDirectories())
            {
                if (SkipDirectories.Contains(subDirectory.Name))
                {
                    continue;
                }

                AddNestedRoomCandidate(foundRooms, subDirectory.Name);
            }
        }

        var rooms = foundRooms
            .Select((pair, index) => new
            {
                Room = new RoomDefinition(
                    pair.Key,
                    $"Files from {pair.Value}/",
                    [pair.Key, pair.Value.ToLowerInvariant()]),
                Index = index,
            })
            .OrderBy(entry => GetPreferredRoomRank(entry.Room.Name))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Room)
            .ToList();

        if (rooms.All(room => room.Name != "general"))
        {
            rooms.Add(GeneralRoom("Files that don't fit other rooms"));
        }

        return rooms;
    }

    public IReadOnlyList<RoomDefinition> DetectRoomsFromFiles(string projectDirectory)
    {
        var projectPath = new DirectoryInfo(Path.GetFullPath(projectDirectory));
        if (!projectPath.Exists)
        {
            return [GeneralRoom("All project files")];
        }

        var keywordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        CountRooms(projectPath.FullName, keywordCounts);

        var rooms = keywordCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => pair.Value >= 2)
            .Take(6)
            .Select(pair => new RoomDefinition(pair.Key, $"Files related to {pair.Key}", [pair.Key]))
            .ToList();

        return rooms.Count > 0 ? rooms : [GeneralRoom("All project files")];
    }

    private static void CountRooms(string directory, IDictionary<string, int> keywordCounts)
    {
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(childDirectory);
            if (SkipDirectories.Contains(name))
            {
                continue;
            }

            CountRooms(childDirectory, keywordCounts);
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(file)
                .ToLowerInvariant()
                .Replace('-', '_')
                .Replace(' ', '_');

            foreach (var mapping in FolderRoomMap)
            {
                if (!fileName.Contains(mapping.Key, StringComparison.Ordinal))
                {
                    continue;
                }

                keywordCounts[mapping.Value] = keywordCounts.TryGetValue(mapping.Value, out var count)
                    ? count + 1
                    : 1;
            }
        }
    }

    private static void AddRoomCandidate(IDictionary<string, string> foundRooms, string originalName)
    {
        var normalized = originalName.ToLowerInvariant().Replace('-', '_');
        if (FolderRoomMap.TryGetValue(normalized, out var mappedRoom))
        {
            foundRooms.TryAdd(mappedRoom, originalName);
            return;
        }

        if (originalName.Length > 2 && char.IsLetter(originalName[0]))
        {
            var clean = originalName
                .ToLowerInvariant()
                .Replace('-', '_')
                .Replace(' ', '_');

            foundRooms.TryAdd(clean, originalName);
        }
    }

    private static void AddNestedRoomCandidate(IDictionary<string, string> foundRooms, string originalName)
    {
        var normalized = originalName.ToLowerInvariant().Replace('-', '_');
        if (FolderRoomMap.TryGetValue(normalized, out var mappedRoom))
        {
            foundRooms.TryAdd(mappedRoom, originalName);
        }
    }

    private static int GetPreferredRoomRank(string roomName) =>
        PreferredRoomOrder.TryGetValue(roomName, out var rank)
            ? rank
            : int.MaxValue;

    private static RoomDefinition GeneralRoom(string description) => new("general", description, []);
}
