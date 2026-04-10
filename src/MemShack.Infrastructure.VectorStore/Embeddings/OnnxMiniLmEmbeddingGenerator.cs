using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MemShack.Core.Utilities;
using Tokenizers.HuggingFace.Tokenizer;

namespace MemShack.Infrastructure.VectorStore.Embeddings;

public sealed class OnnxMiniLmEmbeddingGenerator : ITextEmbeddingGenerator, IDisposable
{
    private const int BatchSize = 32;
    private const int MaxTokens = 256;
    private const string ArchiveFileName = "onnx.tar.gz";
    private const string ExtractedFolderName = "onnx";
    private const string ModelName = "all-MiniLM-L6-v2";
    private const string ModelUrl = "https://chroma-onnx-models.s3.amazonaws.com/all-MiniLM-L6-v2/onnx.tar.gz";
    private const string ModelSha256 = "913d7300ceae3b2dbc2c50d1de4baacab4be7b9380491c27fab7418616a16ec3";

    private static readonly string[] RequiredFiles =
    [
        "config.json",
        "model.onnx",
        "special_tokens_map.json",
        "tokenizer_config.json",
        "tokenizer.json",
        "vocab.txt",
    ];

    private readonly string _archivePath;
    private readonly string _downloadDirectory;
    private readonly string _extractedDirectory;
    private readonly HttpClient _httpClient;
    private readonly object _sync = new();
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;

    public OnnxMiniLmEmbeddingGenerator(string? downloadDirectory = null, HttpClient? httpClient = null)
    {
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            PathUtilities.GetHomeDirectory(),
            ".cache",
            "chroma",
            "onnx_models",
            ModelName);
        _archivePath = Path.Combine(_downloadDirectory, ArchiveFileName);
        _extractedDirectory = Path.Combine(_downloadDirectory, ExtractedFolderName);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return [];
        }

        EnsureAssetsReady(cancellationToken);
        EnsureLoaded();

        var embeddings = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(BatchSize).ToArray();
            embeddings.AddRange(EmbedBatch(batch));
        }

        return embeddings;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
            _tokenizer?.Dispose();
            _tokenizer = null;
        }
    }

    private void EnsureAssetsReady(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (HasExtractedFiles())
            {
                return;
            }

            Directory.CreateDirectory(_downloadDirectory);
            if (!File.Exists(_archivePath) || !VerifySha256(_archivePath, ModelSha256))
            {
                DownloadArchive(cancellationToken);
            }

            if (Directory.Exists(_extractedDirectory))
            {
                Directory.Delete(_extractedDirectory, recursive: true);
            }

            using var archiveStream = File.OpenRead(_archivePath);
            using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, _downloadDirectory, overwriteFiles: true);

            if (!HasExtractedFiles())
            {
                throw new InvalidOperationException($"The extracted MiniLM model is incomplete under '{_extractedDirectory}'.");
            }
        }
    }

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_tokenizer is not null && _session is not null)
            {
                return;
            }

            _tokenizer = Tokenizer.FromFile(Path.Combine(_extractedDirectory, "tokenizer.json"));

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session = new InferenceSession(
                Path.Combine(_extractedDirectory, "model.onnx"),
                sessionOptions);
        }
    }

    private IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> batch)
    {
        var tokenizer = _tokenizer ?? throw new InvalidOperationException("Tokenizer is not initialized.");
        var session = _session ?? throw new InvalidOperationException("ONNX session is not initialized.");

        var inputIds = new DenseTensor<long>(new[] { batch.Count, MaxTokens });
        var attentionMask = new DenseTensor<long>(new[] { batch.Count, MaxTokens });
        var tokenTypeIds = new DenseTensor<long>(new[] { batch.Count, MaxTokens });

        for (var row = 0; row < batch.Count; row++)
        {
            var encoding = tokenizer.Encode(
                batch[row],
                addSpecialTokens: true,
                includeTypeIds: true,
                includeAttentionMask: true).First();

            var ids = encoding.Ids.Take(MaxTokens).ToArray();
            var mask = encoding.AttentionMask.Count > 0
                ? encoding.AttentionMask.Take(MaxTokens).ToArray()
                : Enumerable.Repeat<uint>(1, ids.Length).ToArray();
            var length = ids.Length;
            for (var column = 0; column < length; column++)
            {
                inputIds[row, column] = ids[column];
                attentionMask[row, column] = mask[column];
                // Chroma's Python ONNX embedder always sends zero token_type_ids.
                tokenTypeIds[row, column] = 0;
            }
        }

        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        ]);

        var lastHiddenState = results.First().AsTensor<float>();
        var hiddenSize = lastHiddenState.Dimensions[^1];
        var output = new List<float[]>(batch.Count);

        for (var row = 0; row < batch.Count; row++)
        {
            var embedding = new float[hiddenSize];
            long tokenCount = 0;
            for (var column = 0; column < MaxTokens; column++)
            {
                if (attentionMask[row, column] == 0)
                {
                    continue;
                }

                tokenCount++;
                for (var hidden = 0; hidden < hiddenSize; hidden++)
                {
                    embedding[hidden] += lastHiddenState[row, column, hidden];
                }
            }

            if (tokenCount > 0)
            {
                for (var hidden = 0; hidden < hiddenSize; hidden++)
                {
                    embedding[hidden] /= tokenCount;
                }
            }

            Normalize(embedding);
            output.Add(embedding);
        }

        return output;
    }

    private void DownloadArchive(CancellationToken cancellationToken)
    {
        using var response = _httpClient.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var tempPath = Path.Combine(_downloadDirectory, $"{ArchiveFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = response.Content.ReadAsStream(cancellationToken))
            using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
                target.Flush();
            }

            if (!VerifySha256(tempPath, ModelSha256))
            {
                throw new InvalidOperationException($"Downloaded MiniLM archive at '{tempPath}' did not match the expected SHA256.");
            }

            File.Move(tempPath, _archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private bool HasExtractedFiles() =>
        RequiredFiles.All(fileName => File.Exists(Path.Combine(_extractedDirectory, fileName)));

    private static bool VerifySha256(string filePath, string expectedSha256)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
    }

    private static void Normalize(float[] vector)
    {
        double magnitudeSquared = 0d;
        foreach (var value in vector)
        {
            magnitudeSquared += value * value;
        }

        if (magnitudeSquared <= double.Epsilon)
        {
            return;
        }

        var scale = (float)(1d / Math.Sqrt(magnitudeSquared));
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
        }
    }
}
