namespace MemShack.Application.Mining;

public sealed record MiningProgressUpdate(
    int FilesProcessed,
    int FilesDiscovered,
    int FilesSkipped,
    int DrawersFiled,
    bool DryRun);
