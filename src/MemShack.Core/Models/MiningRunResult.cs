namespace MemShack.Core.Models;

public sealed record MiningRunResult(
    int FilesDiscovered,
    int FilesProcessed,
    int FilesSkipped,
    int DrawersFiled,
    IReadOnlyDictionary<string, int> RoomCounts,
    bool DryRun);
