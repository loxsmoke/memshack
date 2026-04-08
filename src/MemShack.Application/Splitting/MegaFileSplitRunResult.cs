namespace MemShack.Application.Splitting;

public sealed record MegaFileSplitRunResult(
    string SourceDirectory,
    string? OutputDirectory,
    int MegaFileCount,
    int SessionsCreated,
    bool DryRun,
    IReadOnlyList<MegaFileSplitItem> Files);
