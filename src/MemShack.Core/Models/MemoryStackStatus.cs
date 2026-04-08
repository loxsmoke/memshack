namespace MemShack.Core.Models;

public sealed record MemoryStackStatus(
    string PalacePath,
    MemoryLayerStatus L0Identity,
    MemoryLayerStatus L1Essential,
    MemoryLayerStatus L2OnDemand,
    MemoryLayerStatus L3DeepSearch,
    int TotalDrawers);
