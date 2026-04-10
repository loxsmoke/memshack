using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;

namespace MemShack.Infrastructure.VectorStore;

public static class VectorStoreFactory
{
    private const string ChromaBackend = "chroma";
    private const string CompatibilityBackend = "compatibility";

    public static IVectorStore Create(MempalaceConfigSnapshot config, ChromaSidecarManager? sidecarManager = null)
    {
        var backend = NormalizeBackend(config.VectorStoreBackend);
        if (string.Equals(backend, CompatibilityBackend, StringComparison.Ordinal))
        {
            return new ChromaCompatibilityVectorStore(config.PalacePath);
        }

        if (!string.Equals(backend, ChromaBackend, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unknown vector store backend '{config.VectorStoreBackend}'. Supported values are '{ChromaBackend}' and '{CompatibilityBackend}'.");
        }

        if (!string.IsNullOrWhiteSpace(config.ChromaUrl))
        {
            return new ChromaHttpVectorStore(config.ChromaUrl!, config.ChromaTenant, config.ChromaDatabase);
        }

        sidecarManager ??= new ChromaSidecarManager();
        var sidecarUrl = sidecarManager.TryGetOrStart(config);
        if (!string.IsNullOrWhiteSpace(sidecarUrl))
        {
            return new ChromaHttpVectorStore(sidecarUrl, config.ChromaTenant, config.ChromaDatabase);
        }

        throw new InvalidOperationException(
            "MemShack is configured to use its managed Chroma database, but no Chroma server or binary is available. " +
            "Set 'chroma_url', set 'chroma_binary_path', install 'chroma' on PATH, let MemShack auto-install it to the default config location, or bundle the Chroma sidecar with the tool. " +
            "To force the legacy JSON compatibility store instead, set 'vector_store_backend' to 'compatibility'.");
    }

    private static string NormalizeBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return ChromaBackend;
        }

        return backend.Trim().ToLowerInvariant() switch
        {
            "chroma" or "managed-chroma" or "managed_chroma" => ChromaBackend,
            "compatibility" or "compat" or "json" or "legacy-json" or "legacy_json" => CompatibilityBackend,
            _ => backend.Trim(),
        };
    }
}
