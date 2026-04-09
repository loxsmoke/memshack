using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;

namespace MemShack.Infrastructure.VectorStore;

public static class VectorStoreFactory
{
    public static IVectorStore Create(MempalaceConfigSnapshot config) =>
        !string.IsNullOrWhiteSpace(config.ChromaUrl)
            ? new ChromaHttpVectorStore(config.ChromaUrl!, config.ChromaTenant, config.ChromaDatabase)
            : new ChromaCompatibilityVectorStore(config.PalacePath);
}
