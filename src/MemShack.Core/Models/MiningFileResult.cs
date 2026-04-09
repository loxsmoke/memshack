namespace MemShack.Core.Models;

public sealed record MiningFileResult(
    int FileIndex,
    string SourceFile,
    string Room,
    int DrawersFiled);
