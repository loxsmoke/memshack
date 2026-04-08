namespace MemShack.Application.Splitting;

public sealed record SplitSessionResult(
    string OutputPath,
    int LineCount,
    bool Written);
