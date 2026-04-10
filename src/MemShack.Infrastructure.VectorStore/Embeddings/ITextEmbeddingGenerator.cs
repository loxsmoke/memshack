namespace MemShack.Infrastructure.VectorStore.Embeddings;

public interface ITextEmbeddingGenerator
{
    IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
