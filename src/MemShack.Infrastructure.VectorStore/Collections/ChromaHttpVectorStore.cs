using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Infrastructure.VectorStore.Collections;

public sealed class ChromaHttpVectorStore : IVectorStore
{
    private const int GetBatchSize = 500;

    private readonly string _database;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _collectionIds = new(StringComparer.Ordinal);
    private readonly string _tenant;
    private ApiVersion _apiVersion;

    public ChromaHttpVectorStore(
        string baseUrl,
        string tenant = "default_tenant",
        string database = "default_database",
        HttpClient? httpClient = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _tenant = tenant;
        _database = database;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string BaseUrl { get; }

    public async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        _ = await ResolveCollectionIdAsync(collectionName, createIfMissing: true, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var version = await EnsureApiVersionAsync(cancellationToken);
        using var document = version switch
        {
            ApiVersion.V2 => await SendAsync(HttpMethod.Get, BuildV2CollectionsPath(), null, cancellationToken),
            _ => await SendAsync(HttpMethod.Get, BuildV1CollectionsPath(), null, cancellationToken),
        };

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray()
                .Select(GetCollectionName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("collections", out var collectionsElement) &&
            collectionsElement.ValueKind == JsonValueKind.Array)
        {
            return collectionsElement.EnumerateArray()
                .Select(GetCollectionName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();
        }

        return [];
    }

    public async Task<bool> AddDrawerAsync(
        string collectionName,
        DrawerRecord drawer,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collectionName, createIfMissing: true, cancellationToken)
            ?? throw new InvalidOperationException($"Could not resolve Chroma collection '{collectionName}'.");

        var body = new JsonObject
        {
            ["ids"] = new JsonArray(drawer.Id),
            ["documents"] = new JsonArray(drawer.Text),
            ["metadatas"] = new JsonArray(ToMetadataJson(drawer.Metadata)),
        };

        await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "add"), body, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteDrawerAsync(
        string collectionName,
        string drawerId,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collectionName, createIfMissing: false, cancellationToken);
        if (collectionId is null)
        {
            return false;
        }

        var existing = await GetByIdsAsync(collectionId, [drawerId], cancellationToken);
        if (existing.Count == 0)
        {
            return false;
        }

        var body = new JsonObject
        {
            ["ids"] = new JsonArray(drawerId),
        };

        await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "delete"), body, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string collectionName,
        string query,
        int limit,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collectionName, createIfMissing: false, cancellationToken);
        if (collectionId is null)
        {
            return [];
        }

        var body = new JsonObject
        {
            ["query_texts"] = new JsonArray(query),
            ["n_results"] = limit,
            ["include"] = new JsonArray("documents", "metadatas", "distances"),
        };

        var where = BuildWhereFilter(wing, room);
        if (where is not null)
        {
            body["where"] = where;
        }

        using var document = await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "query"), body, cancellationToken);
        var root = document.RootElement;

        var documents = GetNestedArray(root, "documents");
        var metadatas = GetNestedArray(root, "metadatas");
        var distances = GetNestedArray(root, "distances");
        if (documents.Count == 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        var firstDocuments = documents[0];
        var firstMetadatas = metadatas.Count > 0 ? metadatas[0] : [];
        var firstDistances = distances.Count > 0 ? distances[0] : [];

        for (var index = 0; index < firstDocuments.Count; index++)
        {
            var text = firstDocuments[index].GetString() ?? string.Empty;
            var metadata = index < firstMetadatas.Count && firstMetadatas[index].ValueKind == JsonValueKind.Object
                ? firstMetadatas[index]
                : default;
            var distance = index < firstDistances.Count && firstDistances[index].ValueKind == JsonValueKind.Number
                ? firstDistances[index].GetDouble()
                : 1d;

            var metadataDictionary = metadata.ValueKind == JsonValueKind.Object
                ? ParseMetadataDictionary(metadata)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            hits.Add(
                new SearchHit(
                    text,
                    GetMetadataString(metadata, "wing") ?? string.Empty,
                    GetMetadataString(metadata, "room") ?? string.Empty,
                    GetMetadataString(metadata, "source_file") ?? string.Empty,
                    Math.Round(1 - distance, 3),
                    metadataDictionary));
        }

        return hits;
    }

    public async Task<IReadOnlyList<DrawerRecord>> GetDrawersAsync(
        string collectionName,
        string? wing = null,
        string? room = null,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collectionName, createIfMissing: false, cancellationToken);
        if (collectionId is null)
        {
            return [];
        }

        var drawers = new List<DrawerRecord>();
        var offset = 0;
        while (true)
        {
            var body = new JsonObject
            {
                ["limit"] = GetBatchSize,
                ["offset"] = offset,
                ["include"] = new JsonArray("documents", "metadatas"),
            };

            var where = BuildWhereFilter(wing, room);
            if (where is not null)
            {
                body["where"] = where;
            }

            using var document = await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "get"), body, cancellationToken);
            var batch = ParseDrawers(document.RootElement);
            if (batch.Count == 0)
            {
                break;
            }

            drawers.AddRange(batch);
            if (batch.Count < GetBatchSize)
            {
                break;
            }

            offset += batch.Count;
        }

        return drawers;
    }

    public async Task<bool> HasSourceFileAsync(
        string collectionName,
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collectionName, createIfMissing: false, cancellationToken);
        if (collectionId is null)
        {
            return false;
        }

        var body = new JsonObject
        {
            ["limit"] = 1,
            ["include"] = new JsonArray("metadatas"),
            ["where"] = new JsonObject
            {
                ["source_file"] = Path.GetFullPath(sourceFile),
            },
        };

        using var document = await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "get"), body, cancellationToken);
        return GetIdCount(document.RootElement) > 0;
    }

    private async Task<string?> ResolveCollectionIdAsync(
        string collectionName,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (_collectionIds.TryGetValue(collectionName, out var cached))
        {
            return cached;
        }

        var version = await EnsureApiVersionAsync(cancellationToken);
        var path = version switch
        {
            ApiVersion.V2 => $"{BuildV2CollectionsPath()}/{Uri.EscapeDataString(collectionName)}",
            _ => BuildV1CollectionPath(collectionName),
        };

        using var response = await SendRawAsync(HttpMethod.Get, path, null, allowNotFound: true, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (!createIfMissing)
            {
                return null;
            }

            using var createDocument = version switch
            {
                ApiVersion.V2 => await SendAsync(
                    HttpMethod.Post,
                    BuildV2CollectionsPath(),
                    new JsonObject
                    {
                        ["name"] = collectionName,
                        ["metadata"] = null,
                        ["configuration"] = null,
                        ["get_or_create"] = true,
                    },
                    cancellationToken),
                _ => await SendAsync(
                    HttpMethod.Post,
                    BuildV1CollectionsPath(),
                    new JsonObject
                    {
                        ["name"] = collectionName,
                        ["metadata"] = null,
                        ["get_or_create"] = true,
                    },
                    cancellationToken),
            };

            var createdId = GetCollectionId(createDocument.RootElement)
                ?? throw new InvalidOperationException($"Chroma did not return an id for collection '{collectionName}'.");
            _collectionIds[collectionName] = createdId;
            return createdId;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var collectionId = GetCollectionId(document.RootElement);
        if (collectionId is null)
        {
            throw new InvalidOperationException($"Chroma did not return an id for collection '{collectionName}'.");
        }

        _collectionIds[collectionName] = collectionId;
        return collectionId;
    }

    private async Task<ApiVersion> EnsureApiVersionAsync(CancellationToken cancellationToken)
    {
        if (_apiVersion != ApiVersion.Unknown)
        {
            return _apiVersion;
        }

        if (await EndpointExistsAsync($"/api/v2/tenants/{Uri.EscapeDataString(_tenant)}", cancellationToken))
        {
            _apiVersion = ApiVersion.V2;
            return _apiVersion;
        }

        if (await EndpointExistsAsync($"/api/v1/tenants/{Uri.EscapeDataString(_tenant)}", cancellationToken))
        {
            _apiVersion = ApiVersion.V1;
            return _apiVersion;
        }

        throw new InvalidOperationException($"Could not detect a compatible Chroma API at {BaseUrl}.");
    }

    private async Task<bool> EndpointExistsAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, allowNotFound: true, cancellationToken);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    private string BuildV2CollectionsPath() =>
        $"/api/v2/tenants/{Uri.EscapeDataString(_tenant)}/databases/{Uri.EscapeDataString(_database)}/collections";

    private string BuildV1CollectionsPath() =>
        $"/api/v1/collections?tenant={Uri.EscapeDataString(_tenant)}&database={Uri.EscapeDataString(_database)}";

    private string BuildV1CollectionPath(string collectionName) =>
        $"/api/v1/collections/{Uri.EscapeDataString(collectionName)}?tenant={Uri.EscapeDataString(_tenant)}&database={Uri.EscapeDataString(_database)}";

    private string BuildCollectionActionPath(string collectionId, string action)
    {
        var encodedId = Uri.EscapeDataString(collectionId);
        return _apiVersion switch
        {
            ApiVersion.V2 => $"{BuildV2CollectionsPath()}/{encodedId}/{action}",
            _ => $"/api/v1/collections/{encodedId}/{action}",
        };
    }

    private async Task<IReadOnlyList<DrawerRecord>> GetByIdsAsync(
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(id => JsonValue.Create(id)!).ToArray()),
            ["include"] = new JsonArray("documents", "metadatas"),
        };

        using var document = await SendAsync(HttpMethod.Post, BuildCollectionActionPath(collectionId, "get"), body, cancellationToken);
        return ParseDrawers(document.RootElement);
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, allowNotFound: false, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (allowNotFound || response.StatusCode != HttpStatusCode.NotFound)
        {
            return response;
        }

        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Chroma request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {details}",
            null,
            response.StatusCode);
    }

    private static string? GetCollectionName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
    }

    private static string? GetCollectionId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            return idElement.GetString();
        }

        return element.TryGetProperty("collection_id", out var collectionIdElement) && collectionIdElement.ValueKind == JsonValueKind.String
            ? collectionIdElement.GetString()
            : null;
    }

    private static int GetIdCount(JsonElement root)
    {
        if (!root.TryGetProperty("ids", out var idsElement))
        {
            return 0;
        }

        return idsElement.ValueKind switch
        {
            JsonValueKind.Array => idsElement.GetArrayLength(),
            _ => 0,
        };
    }

    private static IReadOnlyList<DrawerRecord> ParseDrawers(JsonElement root)
    {
        var ids = root.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array
            ? idsElement.EnumerateArray().ToArray()
            : [];
        var documents = root.TryGetProperty("documents", out var documentsElement) && documentsElement.ValueKind == JsonValueKind.Array
            ? documentsElement.EnumerateArray().ToArray()
            : [];
        var metadatas = root.TryGetProperty("metadatas", out var metadatasElement) && metadatasElement.ValueKind == JsonValueKind.Array
            ? metadatasElement.EnumerateArray().ToArray()
            : [];

        var drawers = new List<DrawerRecord>();
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index].GetString() ?? string.Empty;
            var text = index < documents.Length && documents[index].ValueKind == JsonValueKind.String
                ? documents[index].GetString() ?? string.Empty
                : string.Empty;
            var metadata = index < metadatas.Length && metadatas[index].ValueKind == JsonValueKind.Object
                ? ParseDrawerMetadata(metadatas[index])
                : new DrawerMetadata();

            drawers.Add(new DrawerRecord(id, text, metadata));
        }

        return drawers;
    }

    private static IReadOnlyList<IReadOnlyList<JsonElement>> GetNestedArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Array ? (IReadOnlyList<JsonElement>)item.EnumerateArray().ToArray() : [])
            .ToArray();
    }

    private static DrawerMetadata ParseDrawerMetadata(JsonElement element)
    {
        return new DrawerMetadata
        {
            Wing = GetMetadataString(element, "wing") ?? string.Empty,
            Room = GetMetadataString(element, "room") ?? string.Empty,
            SourceFile = GetMetadataString(element, "source_file") ?? string.Empty,
            ChunkIndex = GetMetadataInt(element, "chunk_index"),
            AddedBy = GetMetadataString(element, "added_by") ?? string.Empty,
            FiledAt = GetMetadataString(element, "filed_at") ?? string.Empty,
            IngestMode = GetMetadataString(element, "ingest_mode") ?? string.Empty,
            ExtractMode = GetMetadataString(element, "extract_mode") ?? string.Empty,
            Hall = GetMetadataString(element, "hall") ?? string.Empty,
            Topic = GetMetadataString(element, "topic") ?? string.Empty,
            Type = GetMetadataString(element, "type") ?? string.Empty,
            Agent = GetMetadataString(element, "agent") ?? string.Empty,
            Date = GetMetadataString(element, "date") ?? string.Empty,
            Importance = GetNullableMetadataDouble(element, "importance"),
            EmotionalWeight = GetNullableMetadataDouble(element, "emotional_weight"),
            Weight = GetNullableMetadataDouble(element, "weight"),
            CompressionRatio = GetNullableMetadataDouble(element, "compression_ratio"),
            OriginalTokens = GetNullableMetadataInt(element, "original_tokens"),
            CompressedTokens = GetNullableMetadataInt(element, "compressed_tokens"),
        };
    }

    private static Dictionary<string, object?> ParseMetadataDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number => property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.ToString(),
            };
        }

        return dictionary;
    }

    private static string? GetMetadataString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetMetadataInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static double GetMetadataDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0d;

    private static int? GetNullableMetadataInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? GetNullableMetadataDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static JsonObject ToMetadataJson(DrawerMetadata metadata)
    {
        var json = new JsonObject
        {
            ["wing"] = metadata.Wing,
            ["room"] = metadata.Room,
            ["source_file"] = metadata.SourceFile,
            ["chunk_index"] = metadata.ChunkIndex,
            ["added_by"] = metadata.AddedBy,
            ["filed_at"] = metadata.FiledAt,
        };

        AddOptional(json, "ingest_mode", metadata.IngestMode);
        AddOptional(json, "extract_mode", metadata.ExtractMode);
        AddOptional(json, "hall", metadata.Hall);
        AddOptional(json, "topic", metadata.Topic);
        AddOptional(json, "type", metadata.Type);
        AddOptional(json, "agent", metadata.Agent);
        AddOptional(json, "date", metadata.Date);
        AddOptional(json, "importance", metadata.Importance);
        AddOptional(json, "emotional_weight", metadata.EmotionalWeight);
        AddOptional(json, "weight", metadata.Weight);
        AddOptional(json, "compression_ratio", metadata.CompressionRatio);
        AddOptional(json, "original_tokens", metadata.OriginalTokens);
        AddOptional(json, "compressed_tokens", metadata.CompressedTokens);
        return json;
    }

    private static JsonNode? BuildWhereFilter(string? wing, string? room)
    {
        if (!string.IsNullOrWhiteSpace(wing) && !string.IsNullOrWhiteSpace(room))
        {
            return new JsonObject
            {
                ["$and"] = new JsonArray(
                    new JsonObject { ["wing"] = wing },
                    new JsonObject { ["room"] = room }),
            };
        }

        if (!string.IsNullOrWhiteSpace(wing))
        {
            return new JsonObject { ["wing"] = wing };
        }

        return !string.IsNullOrWhiteSpace(room)
            ? new JsonObject { ["room"] = room }
            : null;
    }

    private static void AddOptional(JsonObject json, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            json[propertyName] = value;
        }
    }

    private static void AddOptional(JsonObject json, string propertyName, int value)
    {
        if (value > 0)
        {
            json[propertyName] = value;
        }
    }

    private static void AddOptional(JsonObject json, string propertyName, double value)
    {
        if (Math.Abs(value) > double.Epsilon)
        {
            json[propertyName] = value;
        }
    }

    private static void AddOptional(JsonObject json, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            json[propertyName] = value.Value;
        }
    }

    private static void AddOptional(JsonObject json, string propertyName, double? value)
    {
        if (value.HasValue)
        {
            json[propertyName] = value.Value;
        }
    }

    private enum ApiVersion
    {
        Unknown,
        V1,
        V2,
    }
}
