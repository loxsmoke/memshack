using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IKnowledgeGraphStore
{
    Task<string> AddEntityAsync(EntityRecord entity, CancellationToken cancellationToken = default);

    Task<string> AddTripleAsync(TripleRecord triple, CancellationToken cancellationToken = default);

    Task InvalidateAsync(
        string subject,
        string predicate,
        string @object,
        string? ended = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TripleRecord>> QueryEntityAsync(
        string entity,
        string? asOf = null,
        string direction = "outgoing",
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TripleRecord>> QueryRelationshipAsync(
        string predicate,
        string? asOf = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TripleRecord>> TimelineAsync(
        string? entityName = null,
        CancellationToken cancellationToken = default);

    Task<KnowledgeGraphStats> StatsAsync(CancellationToken cancellationToken = default);
}
