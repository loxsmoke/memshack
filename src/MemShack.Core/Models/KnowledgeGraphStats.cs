namespace MemShack.Core.Models;

public sealed record KnowledgeGraphStats(
    int Entities,
    int Triples,
    int CurrentFacts,
    int ExpiredFacts,
    IReadOnlyList<string> RelationshipTypes);
