using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MemShack.Core.Utilities;
using MemShack.Infrastructure.VectorStore.Embeddings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Parity;

[TestClass]
public sealed class EmbeddingParityTests
{
    private const string PythonProbeScript = """
import json
import sys

import chromadb

print(chromadb.__version__)
""";

    private const string PythonEmbeddingScript = """
import contextlib
import io
import json
import sys

try:
    from chromadb.utils.embedding_functions import DefaultEmbeddingFunction
except Exception:
    from chromadb.utils import embedding_functions
    DefaultEmbeddingFunction = embedding_functions.DefaultEmbeddingFunction

texts = json.loads(sys.argv[1])
buffer = io.StringIO()
with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
    embeddings = DefaultEmbeddingFunction()(texts)

if hasattr(embeddings, "tolist"):
    embeddings = embeddings.tolist()
else:
    embeddings = [list(item) for item in embeddings]

print(json.dumps(embeddings))
""";

    private static readonly string[] RequiredModelFiles =
    [
        "config.json",
        "model.onnx",
        "special_tokens_map.json",
        "tokenizer_config.json",
        "tokenizer.json",
        "vocab.txt",
    ];

    private static readonly string[] SampleTexts =
    [
        "JWT authentication tokens protect the backend API with refresh cookies.",
        "Authentication middleware validates JWT refresh tokens for the backend API.",
        "The migration guide explains how to upgrade Tool v2 safely.",
        "Tool v2 migration steps cover compatibility and validation checks.",
        "Chroma stores metadata in SQLite and vector indexes in HNSW files.",
        "Fresh sourdough bread sold out before noon at the bakery.",
    ];

    [TestMethod]
    [TestCategory("Parity")]
    public void CSharpMiniLmEmbeddings_DistinguishRelatedAndUnrelatedTexts()
    {
        var modelDirectory = GetModelDirectory();
        if (!HasExtractedModelAssets(modelDirectory))
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive($"The shared MiniLM model cache is missing under '{modelDirectory}'. Run a Python or C# embedding flow once to populate it before running this parity test.");
        }

        using var generator = new OnnxMiniLmEmbeddingGenerator(downloadDirectory: modelDirectory);
        var embeddings = generator.Embed(SampleTexts);

        var authToAuth = CosineSimilarity(embeddings[0], embeddings[1]);
        var authToBakery = CosineSimilarity(embeddings[0], embeddings[5]);
        var migrationToMigration = CosineSimilarity(embeddings[2], embeddings[3]);
        var migrationToBakery = CosineSimilarity(embeddings[2], embeddings[5]);

        Assert.True(authToAuth > authToBakery, $"Expected auth texts to be closer than auth vs bakery, but got {authToAuth:F4} <= {authToBakery:F4}.");
        Assert.True(migrationToMigration > migrationToBakery, $"Expected migration texts to be closer than migration vs bakery, but got {migrationToMigration:F4} <= {migrationToBakery:F4}.");
        Assert.True(authToAuth >= 0.3d, $"Expected auth-related cosine >= 0.3, but got {authToAuth:F4}.");
        Assert.True(migrationToMigration >= 0.3d, $"Expected migration-related cosine >= 0.3, but got {migrationToMigration:F4}.");
    }

    [TestMethod]
    [TestCategory("Parity")]
    public void CSharpMiniLmQueries_PreferExpectedDocuments()
    {
        var modelDirectory = GetModelDirectory();
        if (!HasExtractedModelAssets(modelDirectory))
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive($"The shared MiniLM model cache is missing under '{modelDirectory}'. Run a Python or C# embedding flow once to populate it before running this parity test.");
        }

        using var generator = new OnnxMiniLmEmbeddingGenerator(downloadDirectory: modelDirectory);
        var docEmbeddings = generator.Embed(SampleTexts);

        var authQuery = generator.Embed(["JWT refresh token authentication for the backend API"])[0];
        var migrationQuery = generator.Embed(["Tool v2 migration and validation steps"])[0];
        var bakeryQuery = generator.Embed(["fresh bakery bread and sourdough"])[0];

        var authTop = FindNearestDocumentIndex(authQuery, docEmbeddings);
        var migrationTop = FindNearestDocumentIndex(migrationQuery, docEmbeddings);
        var bakeryTop = FindNearestDocumentIndex(bakeryQuery, docEmbeddings);

        Assert.Contains(authTop, new[] { 0, 1 });
        Assert.Contains(migrationTop, new[] { 2, 3 });
        Assert.Equal(5, bakeryTop);
    }

    [TestMethod]
    [TestCategory("Parity")]
    public void CSharpMiniLmEmbeddings_StayCloseToPythonChromaEmbeddings()
    {
        var python = ResolvePythonCommand();
        if (python is null)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive("No Python interpreter with chromadb was found. Set MEMSHACK_PARITY_PYTHON or install Python/Chroma to run embedding parity checks.");
        }

        var modelDirectory = GetModelDirectory();
        if (!HasExtractedModelAssets(modelDirectory))
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive($"The shared MiniLM model cache is missing under '{modelDirectory}'. Run a Python or C# embedding flow once to populate it before running this parity test.");
        }

        IReadOnlyList<float[]> pythonEmbeddings;
        try
        {
            pythonEmbeddings = GetPythonEmbeddings(python, SampleTexts);
        }
        catch (Exception exception)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive($"Could not compute Python Chroma embeddings: {exception.Message}");
            return;
        }

        IReadOnlyList<float[]> csharpEmbeddings;
        try
        {
            using var generator = new OnnxMiniLmEmbeddingGenerator(downloadDirectory: modelDirectory);
            csharpEmbeddings = generator.Embed(SampleTexts);
        }
        catch (Exception exception)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive($"Could not compute C# MiniLM embeddings: {exception.Message}");
            return;
        }

        Assert.Equal(SampleTexts.Length, pythonEmbeddings.Count, "Python returned an unexpected number of embeddings.");
        Assert.Equal(SampleTexts.Length, csharpEmbeddings.Count, "C# returned an unexpected number of embeddings.");

        var selfCosines = new List<double>(SampleTexts.Length);
        for (var index = 0; index < SampleTexts.Length; index++)
        {
            Assert.Equal(
                pythonEmbeddings[index].Length,
                csharpEmbeddings[index].Length,
                $"Embedding dimension mismatch for sample {index + 1}.");

            var cosine = CosineSimilarity(pythonEmbeddings[index], csharpEmbeddings[index]);
            selfCosines.Add(cosine);
            Assert.True(
                cosine >= 0.95d,
                $"Expected Python/C# cosine >= 0.95 for sample {index + 1}, but got {cosine:F4}. Text: {SampleTexts[index]}");
        }

        var averageSelfCosine = selfCosines.Average();
        Assert.True(
            averageSelfCosine >= 0.98d,
            $"Expected average Python/C# cosine >= 0.98 across the sample corpus, but got {averageSelfCosine:F4}.");

        var pairwiseDrifts = new List<double>();
        for (var left = 0; left < SampleTexts.Length; left++)
        {
            for (var right = left + 1; right < SampleTexts.Length; right++)
            {
                var pythonCosine = CosineSimilarity(pythonEmbeddings[left], pythonEmbeddings[right]);
                var csharpCosine = CosineSimilarity(csharpEmbeddings[left], csharpEmbeddings[right]);
                pairwiseDrifts.Add(Math.Abs(pythonCosine - csharpCosine));
            }
        }

        var meanPairwiseDrift = pairwiseDrifts.Average();
        Assert.True(
            meanPairwiseDrift <= 0.05d,
            $"Expected mean pairwise similarity drift <= 0.05, but got {meanPairwiseDrift:F4}.");

        var matchingNearestNeighbors = 0;
        for (var index = 0; index < SampleTexts.Length; index++)
        {
            var pythonNeighbor = FindNearestNeighborIndex(pythonEmbeddings, index);
            var csharpNeighbor = FindNearestNeighborIndex(csharpEmbeddings, index);
            if (pythonNeighbor == csharpNeighbor)
            {
                matchingNearestNeighbors++;
            }
        }

        Assert.True(
            matchingNearestNeighbors >= 4,
            $"Expected at least 4 of {SampleTexts.Length} nearest-neighbor relationships to match between Python and C#, but only {matchingNearestNeighbors} matched.");
    }

    private static IReadOnlyList<float[]> GetPythonEmbeddings(PythonCommand python, IReadOnlyList<string> texts)
    {
        var json = JsonSerializer.Serialize(texts);
        var result = RunProcess(python, ["-c", PythonEmbeddingScript, json]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Stderr.Trim());
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<float[][]>(result.Stdout.Trim());
            return Assert.NotNull(parsed, "Python embedding output was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Python embedding output was not valid JSON. Stdout: {result.Stdout.Trim()} Stderr: {result.Stderr.Trim()}",
                exception);
        }
    }

    private static PythonCommand? ResolvePythonCommand()
    {
        foreach (var candidate in GetPythonCandidates())
        {
            var result = RunProcess(candidate, ["-c", PythonProbeScript]);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<PythonCommand> GetPythonCandidates()
    {
        var mempalaceRepoPath = GetSiblingMempalaceRepoPath();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEMSHACK_PARITY_PYTHON")))
        {
            var explicitPath = Environment.GetEnvironmentVariable("MEMSHACK_PARITY_PYTHON")!;
            if (seen.Add($"file:{explicitPath}"))
            {
                yield return new PythonCommand(explicitPath, [], Environment.CurrentDirectory);
            }
        }

        if (!string.IsNullOrWhiteSpace(mempalaceRepoPath) && seen.Add($"uv:{mempalaceRepoPath}"))
        {
            yield return new PythonCommand("uv", ["run", "python"], mempalaceRepoPath);
        }

        if (seen.Add("python"))
        {
            yield return new PythonCommand("python", [], Environment.CurrentDirectory);
        }

        if (seen.Add("python3"))
        {
            yield return new PythonCommand("python3", [], Environment.CurrentDirectory);
        }

        if (OperatingSystem.IsWindows() && seen.Add("py-3"))
        {
            yield return new PythonCommand("py", ["-3"], Environment.CurrentDirectory);
        }
    }

    private static ProcessResult RunProcess(PythonCommand command, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = command.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var prefixArgument in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefixArgument);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, $"Failed to start '{command.FileName}'.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                return new ProcessResult(-1, stdout, $"Timed out waiting for '{command.FileName}' to finish. {stderr}".Trim());
            }

            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception exception)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    private static string? GetSiblingMempalaceRepoPath()
    {
        var parent = Directory.GetParent(FixturePaths.RepoRootPath);
        if (parent is null)
        {
            return null;
        }

        var candidate = Path.Combine(parent.FullName, "mempalace");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string GetModelDirectory() =>
        Path.Combine(
            PathUtilities.GetHomeDirectory(),
            ".cache",
            "chroma",
            "onnx_models",
            "all-MiniLM-L6-v2");

    private static bool HasExtractedModelAssets(string modelDirectory)
    {
        var extractedDirectory = Path.Combine(modelDirectory, "onnx");
        return RequiredModelFiles.All(fileName => File.Exists(Path.Combine(extractedDirectory, fileName)));
    }

    private static int FindNearestNeighborIndex(IReadOnlyList<float[]> embeddings, int anchorIndex)
    {
        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;

        for (var index = 0; index < embeddings.Count; index++)
        {
            if (index == anchorIndex)
            {
                continue;
            }

            var score = CosineSimilarity(embeddings[anchorIndex], embeddings[index]);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static int FindNearestDocumentIndex(IReadOnlyList<float> queryEmbedding, IReadOnlyList<float[]> documentEmbeddings)
    {
        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;

        for (var index = 0; index < documentEmbeddings.Count; index++)
        {
            var score = CosineSimilarity(queryEmbedding, documentEmbeddings[index]);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0d;
        double leftMagnitude = 0d;
        double rightMagnitude = 0d;

        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private sealed record PythonCommand(string FileName, IReadOnlyList<string> PrefixArguments, string WorkingDirectory);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
