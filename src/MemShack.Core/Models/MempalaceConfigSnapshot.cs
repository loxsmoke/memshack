namespace MemShack.Core.Models;

public sealed record MempalaceConfigSnapshot(
    string PalacePath,
    string CollectionName,
    IReadOnlyDictionary<string, string> PeopleMap,
    IReadOnlyList<string> TopicWings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> HallKeywords,
    string VectorStoreBackend = "chroma",
    string? ChromaUrl = null,
    string ChromaTenant = "default_tenant",
    string ChromaDatabase = "default_database",
    string? ChromaBinaryPath = null,
    bool ChromaAutoInstall = true,
    string? ConfigDirectory = null);
