using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Models;

namespace MemShack.McpServer;

public sealed partial class MemShackMcpServer
{
    private const int MaxGroupedCountBuckets = 200;
    private const int MaxTaxonomyWings = 200;
    private const int MaxTaxonomyRoomsPerWing = 200;
    private const int MaxDiaryReadEntries = 100;
    private const int MaxSlugLength = 80;
    private const int MaxNameLength = 160;
    private const int MaxContentLength = 200_000;

    private static readonly Regex UnsafeControlCharacterPattern = new(
        @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlugSeparatorPattern = new(@"[\s/\\]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlugInvalidCharacterPattern = new(@"[^a-z0-9._-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedSeparatorPattern = new(@"([_-])\1+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _writeAheadLogMutex = new(1, 1);

    private static string SanitizeRequiredSlug(JsonObject arguments, string key) =>
        SanitizeSlug(GetRequiredString(arguments, key), key);

    private static string SanitizeOptionalSlug(JsonObject arguments, string key, string defaultValue) =>
        SanitizeSlug(GetOptionalString(arguments, key) ?? defaultValue, key);

    private static string? SanitizeOptionalNullableSlug(JsonObject arguments, string key)
    {
        var value = GetOptionalString(arguments, key);
        return string.IsNullOrWhiteSpace(value) ? null : SanitizeSlug(value, key);
    }

    private static string SanitizeAgentName(JsonObject arguments, string key = "agent_name") =>
        SanitizeDisplayName(GetRequiredString(arguments, key), key);

    private static string SanitizeAddedBy(JsonObject arguments, string key = "added_by", string defaultValue = "mcp")
    {
        var value = GetOptionalString(arguments, key);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : SanitizeDisplayName(value, key);
    }

    private static string SanitizeEntityName(JsonObject arguments, string key) =>
        SanitizeDisplayName(GetRequiredString(arguments, key), key);

    private static string? SanitizeOptionalEntityName(JsonObject arguments, string key)
    {
        var value = GetOptionalString(arguments, key);
        return string.IsNullOrWhiteSpace(value) ? null : SanitizeDisplayName(value, key);
    }

    private static string SanitizeContent(JsonObject arguments, string key) =>
        SanitizeContentValue(GetRequiredString(arguments, key), key);

    private static string? SanitizeOptionalSourceFile(JsonObject arguments, string key = "source_file")
    {
        var value = GetOptionalString(arguments, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return SanitizeBoundedText(value, key, maxLength: 1024, preserveNewlines: false);
    }

    private static string? SanitizeOptionalIsoDate(JsonObject arguments, string key)
    {
        var value = GetOptionalString(arguments, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = SanitizeBoundedText(value, key, maxLength: 32, preserveNewlines: false);
        if (!DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new InvalidOperationException($"Invalid {key}: expected YYYY-MM-DD.");
        }

        return normalized;
    }

    private static string SanitizeDisplayName(string value, string fieldName)
    {
        var normalized = SanitizeBoundedText(value, fieldName, MaxNameLength, preserveNewlines: false);
        normalized = WhitespacePattern.Replace(normalized, " ");
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"Missing required argument: {fieldName}");
        }

        return normalized;
    }

    private static string SanitizeSlug(string value, string fieldName)
    {
        var normalized = SanitizeBoundedText(value, fieldName, MaxSlugLength, preserveNewlines: false).ToLowerInvariant();
        normalized = SlugSeparatorPattern.Replace(normalized, "_");
        normalized = SlugInvalidCharacterPattern.Replace(normalized, "_");
        normalized = RepeatedSeparatorPattern.Replace(normalized, "$1");
        normalized = normalized.Trim('_', '-', '.');

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"Invalid {fieldName}: no usable characters remain after sanitization.");
        }

        return normalized;
    }

    private static string SanitizeContentValue(string value, string fieldName)
    {
        var normalized = SanitizeBoundedText(value, fieldName, MaxContentLength, preserveNewlines: true);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"Missing required argument: {fieldName}");
        }

        return normalized;
    }

    private static string SanitizeBoundedText(string value, string fieldName, int maxLength, bool preserveNewlines)
    {
        var normalized = preserveNewlines
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : value;

        normalized = UnsafeControlCharacterPattern.Replace(normalized, string.Empty).Trim();
        if (!preserveNewlines)
        {
            normalized = normalized.Replace('\n', ' ').Replace('\t', ' ');
            normalized = WhitespacePattern.Replace(normalized, " ");
        }

        if (normalized.Length > maxLength)
        {
            throw new InvalidOperationException($"{fieldName} is too long.");
        }

        return normalized;
    }

    private static int ClampPositiveInt(int value, int defaultValue, int maxValue)
    {
        if (value <= 0)
        {
            return defaultValue;
        }

        return Math.Min(value, maxValue);
    }

    private static double ClampThreshold(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.9;
        }

        if (value < 0)
        {
            return 0;
        }

        if (value > 1)
        {
            return 1;
        }

        return value;
    }

    private static string CreateDeterministicDrawerId(string wing, string room, string content, string sourceFile) =>
        $"drawer_{wing}_{room}_{Sha256Hex($"{wing}\n{room}\n{sourceFile}\n{content}")[..24]}";

    private static string CreateDeterministicDiaryEntryId(string wing, string topic, string entry) =>
        $"diary_{wing}_{topic}_{Sha256Hex($"{wing}\n{topic}\n{entry}")[..24]}";

    private static string Sha256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<DrawerRecord?> FindDrawerByIdAsync(string drawerId, CancellationToken cancellationToken)
    {
        var drawers = await _vectorStore.GetDrawersAsync(_config.CollectionName, cancellationToken: cancellationToken);
        return drawers.FirstOrDefault(drawer => string.Equals(drawer.Id, drawerId, StringComparison.Ordinal));
    }

    private async Task<TripleRecord?> FindCurrentTripleAsync(
        string subject,
        string predicate,
        string @object,
        CancellationToken cancellationToken)
    {
        var facts = await _knowledgeGraphStore.QueryEntityAsync(subject, direction: "outgoing", cancellationToken: cancellationToken);
        return facts.FirstOrDefault(fact =>
            string.Equals(fact.Predicate, MemShack.Infrastructure.Sqlite.KnowledgeGraph.SqliteKnowledgeGraphStore.NormalizePredicate(predicate), StringComparison.Ordinal) &&
            string.Equals(fact.Object, @object, StringComparison.Ordinal) &&
            fact.Current);
    }

    private static Dictionary<string, int> LimitCounts(
        IEnumerable<KeyValuePair<string, int>> counts,
        int limit,
        out bool truncated)
    {
        var ordered = counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        truncated = ordered.Length > limit;
        return ordered
            .Take(limit)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, Dictionary<string, int>> LimitTaxonomy(
        IDictionary<string, Dictionary<string, int>> taxonomy,
        out bool wingsTruncated,
        out int omittedRoomGroups)
    {
        omittedRoomGroups = 0;
        var orderedWings = taxonomy
            .Select(entry => new KeyValuePair<string, int>(entry.Key, entry.Value.Values.Sum()))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        wingsTruncated = orderedWings.Length > MaxTaxonomyWings;
        var limited = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var wing in orderedWings.Take(MaxTaxonomyWings))
        {
            var orderedRooms = taxonomy[wing.Key]
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToArray();

            omittedRoomGroups += Math.Max(0, orderedRooms.Length - MaxTaxonomyRoomsPerWing);
            limited[wing.Key] = orderedRooms
                .Take(MaxTaxonomyRoomsPerWing)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }

        return limited;
    }

    private async Task AppendWriteAheadLogAsync(
        string operation,
        string phase,
        IReadOnlyDictionary<string, object?> details,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_config.PalacePath, ConfigFileNames.McpWriteAheadLogJsonl);
        Directory.CreateDirectory(_config.PalacePath);

        var entry = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["operation"] = operation,
            ["phase"] = phase,
        };

        foreach (var detail in details)
        {
            entry[detail.Key] = JsonSerializer.SerializeToNode(detail.Value);
        }

        await _writeAheadLogMutex.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                path,
                entry.ToJsonString(CompactJsonOptions) + Environment.NewLine,
                cancellationToken);
        }
        finally
        {
            _writeAheadLogMutex.Release();
        }
    }
}
