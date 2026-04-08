namespace MemShack.Core.Models;

public sealed record MempalaceConfigSnapshot(
    string PalacePath,
    string CollectionName,
    IReadOnlyDictionary<string, string> PeopleMap,
    IReadOnlyList<string> TopicWings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> HallKeywords);
