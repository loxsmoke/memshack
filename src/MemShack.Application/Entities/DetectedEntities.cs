namespace MemShack.Application.Entities;

public sealed record DetectedEntities(
    IReadOnlyList<DetectedEntity> People,
    IReadOnlyList<DetectedEntity> Projects,
    IReadOnlyList<DetectedEntity> Uncertain);
