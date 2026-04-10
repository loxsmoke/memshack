using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Infrastructure.VectorStore.Embeddings;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.VectorStore;

[TestClass]
public sealed class ChromaHttpVectorStoreTests
{
    [TestMethod]
    public async Task SupportsCollectionLifecycleAndSearchOverHttp()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new FakeChromaV2Handler();
        using var httpClient = new HttpClient(handler);
        var store = new ChromaHttpVectorStore("http://localhost:8000", httpClient: httpClient, embeddingGenerator: new TestTextEmbeddingGenerator());

        await store.EnsureCollectionAsync(CollectionNames.Drawers);
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_1",
                "JWT authentication uses refresh cookies",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = temp.GetPath("src", "auth.py"),
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-09T10:00:00",
                }));
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_docs_1",
                "Architecture decisions explain token refresh flow",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "documentation",
                    SourceFile = temp.GetPath("docs", "architecture.md"),
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-09T10:05:00",
                }));

        var collections = await store.ListCollectionsAsync();
        var hasSourceFile = await store.HasSourceFileAsync(CollectionNames.Drawers, temp.GetPath("src", "auth.py"));
        var search = await store.SearchAsync(CollectionNames.Drawers, "jwt authentication", 3, wing: "project");
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers, wing: "project");
        var deleted = await store.DeleteDrawerAsync(CollectionNames.Drawers, "drawer_project_docs_1");
        var remaining = await store.GetDrawersAsync(CollectionNames.Drawers, wing: "project");

        Assert.Contains(CollectionNames.Drawers, collections);
        Assert.True(handler.CreatedCollectionsUsePythonDefaultSpace);
        Assert.True(hasSourceFile);
        Assert.Equal(2, drawers.Count);
        Assert.Single(search);
        Assert.Equal("backend", search[0].Room);
        Assert.True(deleted);
        Assert.Single(remaining);
        Assert.Equal("drawer_project_backend_1", remaining[0].Id);
    }

    [TestMethod]
    public async Task SourceFileChecksRespectEmbeddingSignatureAndDeleteBySourceFileUsesWhere()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new FakeChromaV2Handler();
        using var httpClient = new HttpClient(handler);
        var store = new ChromaHttpVectorStore("http://localhost:8000", httpClient: httpClient, embeddingGenerator: new TestTextEmbeddingGenerator());
        var sourceFile = temp.GetPath("src", "auth.py");

        await store.EnsureCollectionAsync(CollectionNames.Drawers);
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_1",
                "JWT authentication uses refresh cookies",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = sourceFile,
                    ChunkIndex = 0,
                    AddedBy = "test",
                    FiledAt = "2026-04-09T10:00:00",
                    EmbeddingSignature = "legacy-signature",
                }));

        var hasLegacy = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, "legacy-signature");
        var hasCurrent = await store.HasSourceFileAsync(CollectionNames.Drawers, sourceFile, EmbeddingSignatures.Current);
        var deleted = await store.DeleteSourceFileAsync(CollectionNames.Drawers, sourceFile);

        Assert.True(hasLegacy);
        Assert.False(hasCurrent);
        Assert.True(deleted);
        Assert.Empty(await store.GetDrawersAsync(CollectionNames.Drawers));
    }

    [TestMethod]
    public async Task Search_UsesChromaDistanceForReportedSimilarity()
    {
        using var handler = new FixedDistanceQueryHandler(-0.75);
        using var httpClient = new HttpClient(handler);
        var store = new ChromaHttpVectorStore("http://localhost:8000", httpClient: httpClient, embeddingGenerator: new TestTextEmbeddingGenerator());

        var results = await store.SearchAsync(CollectionNames.Drawers, "authentication", 5);

        Assert.Single(results);
        Assert.Equal(-0.75, results[0].Similarity);
    }

    [TestMethod]
    public async Task EnsureCollectionAsync_RecreatesLegacyCosineCollectionAsPythonDefaultL2BeforeWriting()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new FakeChromaV2Handler();
        handler.SeedCollection(
            CollectionNames.Drawers,
            "cosine",
            new[]
            {
                new SeedRecord(
                    "drawer-legacy",
                    "legacy text",
                    new JsonObject
                    {
                        ["wing"] = "project",
                        ["room"] = "backend",
                        ["source_file"] = temp.GetPath("src", "auth.py"),
                    },
                    new[] { 1f, 0f }),
            });
        using var httpClient = new HttpClient(handler);
        var store = new ChromaHttpVectorStore("http://localhost:8000", httpClient: httpClient, embeddingGenerator: new TestTextEmbeddingGenerator());

        await store.EnsureCollectionAsync(CollectionNames.Drawers);
        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers);

        Assert.True(handler.DeletedCollections.Count > 0);
        Assert.True(handler.CreatedCollectionsUsePythonDefaultSpace);
        Assert.Empty(drawers);
    }

    [TestMethod]
    public void VectorStoreFactory_UsesHttpStoreWhenChromaUrlIsConfigured()
    {
        var config = new MempalaceConfigSnapshot(
            PalacePath: "C:\\palace",
            CollectionName: CollectionNames.Drawers,
            PeopleMap: new Dictionary<string, string>(StringComparer.Ordinal),
            TopicWings: [],
            HallKeywords: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            VectorStoreBackend: "chroma",
            ChromaUrl: "http://localhost:8000");

        var store = VectorStoreFactory.Create(config);

        Assert.True(store is ChromaHttpVectorStore);
    }

    private sealed class FakeChromaV2Handler : HttpMessageHandler
    {
        private static readonly Regex TokenPattern = new(@"\b[a-z0-9_]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Dictionary<string, FakeCollection> _collectionsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeCollection> _collectionsByName = new(StringComparer.Ordinal);

        public bool CreatedCollectionsUsePythonDefaultSpace { get; private set; } = true;
        public List<string> DeletedCollections { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method.Method;

            if (method == HttpMethod.Get.Method && path == "/api/v2/tenants/default_tenant")
            {
                return Json(new JsonObject { ["name"] = "default_tenant" });
            }

            if (path == "/api/v2/tenants/default_tenant/databases/default_database/collections")
            {
                if (method == HttpMethod.Get.Method)
                {
                    return Json(new JsonArray(_collectionsByName.Values.Select(ToCollectionJson).ToArray()));
                }

                if (method == HttpMethod.Post.Method)
                {
                    var body = await ReadBodyAsync(request, cancellationToken);
                    CreatedCollectionsUsePythonDefaultSpace &= string.Equals(
                        body?["configuration"]?["hnsw"]?["space"]?.GetValue<string>(),
                        "l2",
                        StringComparison.Ordinal);
                    var name = body?["name"]?.GetValue<string>() ?? string.Empty;
                    if (!_collectionsByName.TryGetValue(name, out var collection))
                    {
                        collection = new FakeCollection(
                            Guid.NewGuid().ToString("D"),
                            name,
                            body?["configuration"]?["hnsw"]?["space"]?.GetValue<string>() ?? "l2");
                        _collectionsByName[name] = collection;
                        _collectionsById[collection.Id] = collection;
                    }

                    return Json(ToCollectionJson(collection));
                }
            }

            if (method == HttpMethod.Get.Method &&
                path.StartsWith("/api/v2/tenants/default_tenant/databases/default_database/collections/", StringComparison.Ordinal))
            {
                var collectionName = Uri.UnescapeDataString(path.Split('/').Last());
                return _collectionsByName.TryGetValue(collectionName, out var collection)
                    ? Json(ToCollectionJson(collection))
                    : NotFound();
            }

            if (method == HttpMethod.Delete.Method &&
                path.StartsWith("/api/v2/tenants/default_tenant/databases/default_database/collections/", StringComparison.Ordinal))
            {
                var collectionId = path.Split('/').Last();
                if (_collectionsById.TryGetValue(collectionId, out var collection))
                {
                    _collectionsById.Remove(collection.Id);
                    _collectionsByName.Remove(collection.Name);
                    DeletedCollections.Add(collectionId);
                }

                return Json(new JsonObject());
            }

            if (path.Contains("/add", StringComparison.Ordinal))
            {
                var collection = GetCollectionFromActionPath(path);
                var body = await ReadBodyAsync(request, cancellationToken);
                var embeddings = ReadEmbeddings(body, "embeddings");
                if (embeddings.Count == 0)
                {
                    return Unprocessable("missing field `embeddings`");
                }

                var ids = body?["ids"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
                var documents = body?["documents"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
                var metadatas = body?["metadatas"]?.AsArray().Select(node => node as JsonObject ?? new JsonObject()).ToArray() ?? [];
                for (var index = 0; index < ids.Length; index++)
                {
                    if (collection.Records.Any(record => record.Id == ids[index]))
                    {
                        continue;
                    }

                    collection.Records.Add(new FakeRecord(
                        ids[index],
                        index < documents.Length ? documents[index] : string.Empty,
                        index < metadatas.Length ? metadatas[index] : new JsonObject(),
                        index < embeddings.Count ? embeddings[index] : []));
                }

                return Json(new JsonObject { ["ok"] = true });
            }

            if (path.Contains("/get", StringComparison.Ordinal))
            {
                var collection = GetCollectionFromActionPath(path);
                var body = await ReadBodyAsync(request, cancellationToken);
                var filtered = FilterRecords(collection, body);
                var limit = body?["limit"]?.GetValue<int>() ?? filtered.Count;
                var offset = body?["offset"]?.GetValue<int>() ?? 0;
                filtered = filtered.Skip(offset).Take(limit).ToList();
                return Json(ToGetResponse(filtered));
            }

            if (path.Contains("/query", StringComparison.Ordinal))
            {
                var collection = GetCollectionFromActionPath(path);
                var body = await ReadBodyAsync(request, cancellationToken);
                var queryEmbeddings = ReadEmbeddings(body, "query_embeddings");
                if (queryEmbeddings.Count == 0)
                {
                    return Unprocessable("missing field `query_embeddings`");
                }

                var queryEmbedding = queryEmbeddings[0];
                var limit = body?["n_results"]?.GetValue<int>() ?? 5;
                var filtered = FilterRecords(collection, body)
                    .Select(record => new
                    {
                        Record = record,
                        Distance = 1d - CalculateSimilarity(queryEmbedding, record.Embedding),
                    })
                    .Where(entry => entry.Distance < 1d)
                    .OrderBy(entry => entry.Distance)
                    .Take(limit)
                    .ToArray();

                return Json(
                    new JsonObject
                    {
                        ["ids"] = new JsonArray(new JsonArray(filtered.Select(item => JsonValue.Create(item.Record.Id)!).ToArray())),
                        ["documents"] = new JsonArray(new JsonArray(filtered.Select(item => JsonValue.Create(item.Record.Document)!).ToArray())),
                        ["metadatas"] = new JsonArray(new JsonArray(filtered.Select(item => (JsonNode)item.Record.Metadata.DeepClone()).ToArray())),
                        ["distances"] = new JsonArray(new JsonArray(filtered.Select(item => JsonValue.Create(item.Distance)!).ToArray())),
                    });
            }

            if (path.Contains("/delete", StringComparison.Ordinal))
            {
                var collection = GetCollectionFromActionPath(path);
                var body = await ReadBodyAsync(request, cancellationToken);
                var ids = body?["ids"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal) ?? [];
                var where = body?["where"];
                collection.Records.RemoveAll(record =>
                    ids.Contains(record.Id) ||
                    (where is not null && MatchesWhere(record.Metadata, where)));
                return Json(new JsonObject { ["ok"] = true });
            }

            throw new InvalidOperationException($"Unhandled fake Chroma request: {method} {path}");
        }

        private FakeCollection GetCollectionFromActionPath(string path)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var collectionId = segments[^2];
            return _collectionsById[collectionId];
        }

        private static List<FakeRecord> FilterRecords(FakeCollection collection, JsonObject? body)
        {
            var records = collection.Records.ToList();
            var ids = body?["ids"] as JsonArray;
            if (ids is not null && ids.Count > 0)
            {
                var idSet = ids.Select(node => node?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
                records = records.Where(record => idSet.Contains(record.Id)).ToList();
            }

            var where = body?["where"];
            return where is null ? records : records.Where(record => MatchesWhere(record.Metadata, where)).ToList();
        }

        private static bool MatchesWhere(JsonObject metadata, JsonNode where)
        {
            if (where is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("$and", out var andNode) && andNode is JsonArray andArray)
                {
                    return andArray.All(item => item is not null && MatchesWhere(metadata, item));
                }

                foreach (var property in obj)
                {
                    var actual = metadata[property.Key]?.GetValue<string>();
                    var expected = property.Value?.GetValue<string>();
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            return true;
        }

        private static List<float[]> ReadEmbeddings(JsonObject? body, string propertyName)
        {
            if (body?[propertyName] is not JsonArray outerArray)
            {
                return [];
            }

            var embeddings = new List<float[]>();
            foreach (var item in outerArray)
            {
                if (item is not JsonArray embeddingArray)
                {
                    continue;
                }

                embeddings.Add(
                    embeddingArray.Select(node => node?.GetValue<float>() ?? 0f).ToArray());
            }

            return embeddings;
        }

        private static double CalculateSimilarity(IReadOnlyList<float> queryEmbedding, IReadOnlyList<float> recordEmbedding)
        {
            if (queryEmbedding.Count == 0 || recordEmbedding.Count == 0)
            {
                return 0d;
            }

            var length = Math.Min(queryEmbedding.Count, recordEmbedding.Count);
            double dotProduct = 0d;
            for (var index = 0; index < length; index++)
            {
                dotProduct += queryEmbedding[index] * recordEmbedding[index];
            }

            return Math.Max(0d, Math.Min(1d, dotProduct));
        }

        private static JsonObject ToGetResponse(IReadOnlyList<FakeRecord> records) =>
            new()
            {
                ["ids"] = new JsonArray(records.Select(record => JsonValue.Create(record.Id)!).ToArray()),
                ["documents"] = new JsonArray(records.Select(record => JsonValue.Create(record.Document)!).ToArray()),
                ["metadatas"] = new JsonArray(records.Select(record => (JsonNode)record.Metadata.DeepClone()).ToArray()),
            };

        private static JsonObject ToCollectionJson(FakeCollection collection) =>
            new()
            {
                ["id"] = collection.Id,
                ["name"] = collection.Name,
                ["configuration_json"] = new JsonObject
                {
                    ["hnsw"] = new JsonObject
                    {
                        ["space"] = collection.Space,
                    },
                },
            };

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(node.ToJsonString()),
            };

        private static HttpResponseMessage NotFound() =>
            new(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}"),
            };

        private static HttpResponseMessage Unprocessable(string message) =>
            new(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent($$"""{"error":"ChromaError","message":"{{message}}"}"""),
            };

        private static async Task<JsonObject?> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                return null;
            }

            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content) as JsonObject;
        }

        private sealed record FakeRecord(string Id, string Document, JsonObject Metadata, float[] Embedding);

        public void SeedCollection(string name, string space, IEnumerable<SeedRecord> records)
        {
            var collection = new FakeCollection(Guid.NewGuid().ToString("D"), name, space);
            collection.Records.AddRange(records.Select(record => new FakeRecord(record.Id, record.Document, record.Metadata, record.Embedding)));
            _collectionsById[collection.Id] = collection;
            _collectionsByName[collection.Name] = collection;
        }

        private sealed class FakeCollection
        {
            public FakeCollection(string id, string name, string space)
            {
                Id = id;
                Name = name;
                Space = space;
            }

            public string Id { get; }

            public string Name { get; }

            public string Space { get; }

            public List<FakeRecord> Records { get; } = [];
        }
    }

    private sealed class FixedDistanceQueryHandler(double similarity) : HttpMessageHandler
    {
        private readonly double _distance = 1d - similarity;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method.Method;

            if (method == HttpMethod.Get.Method && path == "/api/v2/tenants/default_tenant")
            {
                return Json(new JsonObject { ["name"] = "default_tenant" });
            }

            if (method == HttpMethod.Get.Method &&
                path == "/api/v2/tenants/default_tenant/databases/default_database/collections/mempalace_drawers")
            {
                return Json(
                    new JsonObject
                    {
                        ["id"] = "collection-1",
                        ["name"] = CollectionNames.Drawers,
                    });
            }

            if (method == HttpMethod.Post.Method &&
                path == "/api/v2/tenants/default_tenant/databases/default_database/collections/collection-1/query")
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                Assert.True(body?["query_embeddings"] is JsonArray, "Search requests should send query_embeddings to Chroma.");

                return Json(
                    new JsonObject
                    {
                        ["ids"] = new JsonArray(new JsonArray("drawer-1")),
                        ["documents"] = new JsonArray(new JsonArray("authentication notes")),
                        ["metadatas"] = new JsonArray(
                            new JsonArray(
                                new JsonObject
                                {
                                    ["wing"] = "project",
                                    ["room"] = "backend",
                                    ["source_file"] = "C:\\project\\auth.py",
                                })),
                        ["distances"] = new JsonArray(new JsonArray(_distance)),
                    });
            }

            throw new InvalidOperationException($"Unhandled fake Chroma request: {method} {path}");
        }

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(node.ToJsonString()),
            };

        private static async Task<JsonObject?> ReadBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                return null;
            }

            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content) as JsonObject;
        }
    }

    private sealed class TestTextEmbeddingGenerator : ITextEmbeddingGenerator
    {
        private const int Dimensions = 256;
        private static readonly Regex TokenPattern = new(@"\b[a-z0-9_]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Dictionary<string, int> _vocabulary = new(StringComparer.Ordinal);
        private readonly object _sync = new();

        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            texts.Select(EmbedText).ToArray();

        private float[] EmbedText(string text)
        {
            var tokens = TokenPattern.Matches(text.ToLowerInvariant())
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var embedding = new float[Dimensions];
            foreach (var token in tokens)
            {
                embedding[ResolveTokenIndex(token)] = 1f;
            }

            double magnitudeSquared = 0d;
            foreach (var value in embedding)
            {
                magnitudeSquared += value * value;
            }

            if (magnitudeSquared > double.Epsilon)
            {
                var scale = (float)(1d / Math.Sqrt(magnitudeSquared));
                for (var index = 0; index < embedding.Length; index++)
                {
                    embedding[index] *= scale;
                }
            }

            return embedding;
        }

        private int ResolveTokenIndex(string token)
        {
            lock (_sync)
            {
                if (_vocabulary.TryGetValue(token, out var existing))
                {
                    return existing;
                }

                var index = _vocabulary.Count % Dimensions;
                _vocabulary[token] = index;
                return index;
            }
        }
    }

    private sealed record SeedRecord(string Id, string Document, JsonObject Metadata, float[] Embedding);
}
