namespace MemShack.Application.Splitting;

public sealed record MegaFileSplitItem(
    string SourceFile,
    int SessionCount,
    IReadOnlyList<SplitSessionResult> Sessions,
    string? BackupPath = null);
