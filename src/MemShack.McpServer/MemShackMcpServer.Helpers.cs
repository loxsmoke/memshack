using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemShack.Core.Models;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private async Task<bool> HasCollectionAsync(CancellationToken cancellationToken)
    {
        var collections = await _vectorStore.ListCollectionsAsync(cancellationToken);
        return collections.Contains(_config.CollectionName, StringComparer.Ordinal);
    }

    private async Task<PalaceGraphSnapshot> BuildGraphSnapshotAsync(CancellationToken cancellationToken)
    {
        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        return _graphBuilder.BuildGraph(drawers.Select(drawer => drawer.Metadata));
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> FindDuplicateMatchesAsync(
        string content,
        double threshold,
        CancellationToken cancellationToken)
    {
        var queryTokens = Tokenize(content);
        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);

        return drawers
            .Select(drawer => new
            {
                Drawer = drawer,
                Similarity = CalculateSimilarity(queryTokens, Tokenize(drawer.Text)),
            })
            .Where(match => match.Similarity >= threshold)
            .OrderByDescending(match => match.Similarity)
            .ThenBy(match => match.Drawer.Id, StringComparer.Ordinal)
            .Take(5)
            .Select(match => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = match.Drawer.Id,
                ["wing"] = match.Drawer.Metadata.Wing,
                ["room"] = match.Drawer.Metadata.Room,
                ["similarity"] = Math.Round(match.Similarity, 3),
                ["content"] = Truncate(match.Drawer.Text, 200),
            })
            .ToArray();
    }

    private static Dictionary<string, object?> ToTripleDictionary(TripleRecord triple)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subject"] = triple.Subject,
            ["predicate"] = triple.Predicate,
            ["object"] = triple.Object,
            ["valid_from"] = triple.ValidFrom,
            ["valid_to"] = triple.ValidTo,
            ["confidence"] = triple.Confidence,
            ["source_closet"] = triple.SourceCloset,
            ["source_file"] = triple.SourceFile,
            ["id"] = triple.Id,
            ["direction"] = triple.Direction,
            ["current"] = triple.Current,
        };
    }

    private Dictionary<string, object?> NoPalace()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["error"] = "No palace found",
            ["palace_path"] = _config.PalacePath,
            ["hint"] = "Run: mempalace init <dir> && mempalace mine <dir>",
        };
    }

    private static string SerializeToolResult(object? result)
    {
        if (result is JsonNode node)
        {
            return node.ToJsonString(PrettyJsonOptions);
        }

        return JsonSerializer.Serialize(result, PrettyJsonOptions);
    }

    private static JsonObject CreateSuccessResponse(JsonNode? requestId, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId?.DeepClone(),
            ["result"] = result.DeepClone(),
        };
    }

    private static JsonObject CreateErrorResponse(JsonNode? requestId, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
    }

    private static JsonObject EmptySchema() =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };

    private static JsonObject Schema(string[] required, params SchemaProperty[] properties)
    {
        var schema = EmptySchema();
        var propertyBag = (JsonObject)schema["properties"]!;

        foreach (var property in properties)
        {
            propertyBag[property.Name] = new JsonObject
            {
                ["type"] = property.Type,
                ["description"] = property.Description,
            };
        }

        if (required.Length > 0)
        {
            var requiredArray = new JsonArray();
            foreach (var name in required)
            {
                requiredArray.Add(name);
            }

            schema["required"] = requiredArray;
        }

        return schema;
    }

    private static JsonObject CoerceArguments(JsonObject arguments, JsonObject inputSchema)
    {
        var coerced = (JsonObject)arguments.DeepClone();
        var properties = inputSchema["properties"] as JsonObject;
        if (properties is null)
        {
            return coerced;
        }

        foreach (var entry in properties)
        {
            if (coerced[entry.Key] is null || entry.Value is not JsonObject propertySchema)
            {
                continue;
            }

            var declaredType = propertySchema["type"]?.GetValue<string>();
            coerced[entry.Key] = declaredType switch
            {
                "integer" => CoerceInteger(coerced[entry.Key]!),
                "number" => CoerceNumber(coerced[entry.Key]!),
                _ => coerced[entry.Key],
            };
        }

        return coerced;
    }

    private static JsonNode CoerceInteger(JsonNode node)
    {
        if (node is not JsonValue value)
        {
            throw new InvalidOperationException("Integer arguments must be scalar values.");
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return JsonValue.Create(intValue)!;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return JsonValue.Create(checked((int)longValue))!;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return JsonValue.Create((int)doubleValue)!;
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return JsonValue.Create((int)decimalValue)!;
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return JsonValue.Create(int.Parse(stringValue, CultureInfo.InvariantCulture))!;
        }

        throw new InvalidOperationException("Integer arguments must be coercible to int.");
    }

    private static JsonNode CoerceNumber(JsonNode node)
    {
        if (node is not JsonValue value)
        {
            throw new InvalidOperationException("Number arguments must be scalar values.");
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return JsonValue.Create(doubleValue)!;
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return JsonValue.Create((double)decimalValue)!;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return JsonValue.Create((double)intValue)!;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return JsonValue.Create((double)longValue)!;
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return JsonValue.Create(double.Parse(stringValue, CultureInfo.InvariantCulture))!;
        }

        throw new InvalidOperationException("Number arguments must be coercible to double.");
    }

    private static string GetRequiredString(JsonObject arguments, string key) =>
        GetOptionalString(arguments, key) ?? throw new InvalidOperationException($"Missing required argument: {key}");

    private static string? GetOptionalString(JsonObject arguments, string key)
    {
        if (arguments[key] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var stringValue)
            ? stringValue
            : value.ToJsonString();
    }

    private static int GetInt(JsonObject arguments, string key, int defaultValue)
    {
        if (arguments[key] is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return (int)doubleValue;
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return int.Parse(stringValue, CultureInfo.InvariantCulture);
        }

        return defaultValue;
    }

    private static double GetDouble(JsonObject arguments, string key, double defaultValue)
    {
        if (arguments[key] is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return double.Parse(stringValue, CultureInfo.InvariantCulture);
        }

        return defaultValue;
    }

    private static HashSet<string> Tokenize(string text) =>
        TokenPattern.Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static double CalculateSimilarity(IReadOnlySet<string> queryTokens, IReadOnlySet<string> textTokens)
    {
        if (queryTokens.Count == 0 || textTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(textTokens.Contains);
        return overlap / (double)queryTokens.Count;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static string NormalizeAgentName(string agentName) =>
        agentName.ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal);

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
