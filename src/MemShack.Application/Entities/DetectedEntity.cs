namespace MemShack.Application.Entities;

public sealed record DetectedEntity(
    string Name,
    string Type,
    double Confidence,
    int Frequency,
    IReadOnlyList<string> Signals);
