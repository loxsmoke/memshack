using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Deduplication;

public sealed class DuplicateCleanupService
{
    public const double DefaultSimilarityThreshold = 0.85d;
    public const int DefaultMinimumGroupSize = 5;

    private static readonly Regex TokenPattern = new(@"\b[a-z0-9_]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IVectorStore _vectorStore;

    public DuplicateCleanupService(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    public async Task<DuplicateCleanupStats> GetStatsAsync(
        string collectionName = CollectionNames.Drawers,
        string? wing = null,
        string? sourcePattern = null,
        int minimumGroupSize = DefaultMinimumGroupSize,
        CancellationToken cancellationToken = default)
    {
        var drawers = await _vectorStore.GetDrawersAsync(collectionName, cancellationToken: cancellationToken);
        var filtered = FilterDrawers(drawers, wing, sourcePattern);
        var groups = GroupBySource(filtered, minimumGroupSize);

        var largestGroups = groups
            .OrderByDescending(group => group.Drawers.Count)
            .ThenBy(group => group.SourceFile, StringComparer.Ordinal)
            .Take(15)
            .Select(group => new DuplicateSourceGroupSummary(group.SourceFile, group.Drawers.Count))
            .ToArray();

        var estimatedDuplicates = groups
            .Where(group => group.Drawers.Count > 20)
            .Sum(group => (int)Math.Round(group.Drawers.Count * 0.4d));

        return new DuplicateCleanupStats(
            filtered.Count,
            groups.Count,
            groups.Sum(group => group.Drawers.Count),
            estimatedDuplicates,
            wing,
            sourcePattern,
            largestGroups);
    }

    public async Task<DuplicateCleanupResult> DeduplicateAsync(
        string collectionName = CollectionNames.Drawers,
        double threshold = DefaultSimilarityThreshold,
        bool dryRun = true,
        string? wing = null,
        string? sourcePattern = null,
        int minimumGroupSize = DefaultMinimumGroupSize,
        CancellationToken cancellationToken = default)
    {
        var drawers = await _vectorStore.GetDrawersAsync(collectionName, cancellationToken: cancellationToken);
        var filtered = FilterDrawers(drawers, wing, sourcePattern);
        var groups = GroupBySource(filtered, minimumGroupSize)
            .OrderByDescending(group => group.Drawers.Count)
            .ThenBy(group => group.SourceFile, StringComparer.Ordinal)
            .ToArray();

        var groupResults = new List<DuplicateSourceCleanupResult>();
        var deletedCount = 0;
        var keptCount = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupResult = await DeduplicateGroupAsync(collectionName, group, threshold, dryRun, cancellationToken);
            groupResults.Add(groupResult);
            deletedCount += groupResult.DeletedCount;
            keptCount += groupResult.KeptCount;
        }

        var finalDrawers = dryRun ? drawers.Count : (await _vectorStore.GetDrawersAsync(collectionName, cancellationToken: cancellationToken)).Count;
        return new DuplicateCleanupResult(
            threshold,
            dryRun,
            wing,
            sourcePattern,
            drawers.Count,
            filtered.Count,
            groups.Length,
            keptCount,
            deletedCount,
            finalDrawers,
            groupResults);
    }

    private async Task<DuplicateSourceCleanupResult> DeduplicateGroupAsync(
        string collectionName,
        DuplicateSourceGroup group,
        double threshold,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var ordered = group.Drawers
            .OrderByDescending(drawer => (drawer.Text?.Length ?? 0))
            .ThenBy(drawer => drawer.Id, StringComparer.Ordinal)
            .ToArray();

        var kept = new List<DrawerRecord>();
        var deleted = new List<DrawerRecord>();

        foreach (var drawer in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(drawer.Text) || drawer.Text.Trim().Length < 20)
            {
                deleted.Add(drawer);
                continue;
            }

            if (kept.Count == 0)
            {
                kept.Add(drawer);
                continue;
            }

            var isDuplicate = kept
                .Take(5)
                .Any(existing => CalculateSimilarity(existing.Text, drawer.Text) >= threshold);

            if (isDuplicate)
            {
                deleted.Add(drawer);
            }
            else
            {
                kept.Add(drawer);
            }
        }

        if (!dryRun)
        {
            foreach (var drawer in deleted)
            {
                await _vectorStore.DeleteDrawerAsync(collectionName, drawer.Id, cancellationToken);
            }
        }

        return new DuplicateSourceCleanupResult(
            group.SourceFile,
            group.Drawers.Count,
            kept.Count,
            deleted.Count,
            deleted.Select(drawer => drawer.Id).ToArray());
    }

    private static IReadOnlyList<DrawerRecord> FilterDrawers(
        IReadOnlyList<DrawerRecord> drawers,
        string? wing,
        string? sourcePattern)
    {
        IEnumerable<DrawerRecord> filtered = drawers;

        if (!string.IsNullOrWhiteSpace(wing))
        {
            filtered = filtered.Where(drawer => string.Equals(drawer.Metadata.Wing, wing, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(sourcePattern))
        {
            filtered = filtered.Where(
                drawer => !string.IsNullOrWhiteSpace(drawer.Metadata.SourceFile) &&
                          drawer.Metadata.SourceFile.Contains(sourcePattern, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToArray();
    }

    private static IReadOnlyList<DuplicateSourceGroup> GroupBySource(IReadOnlyList<DrawerRecord> drawers, int minimumGroupSize)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return drawers
            .Where(drawer => !string.IsNullOrWhiteSpace(drawer.Metadata.SourceFile))
            .GroupBy(drawer => drawer.Metadata.SourceFile, comparer)
            .Select(group => new DuplicateSourceGroup(group.Key, group.ToArray()))
            .Where(group => group.Drawers.Count >= minimumGroupSize)
            .ToArray();
    }

    private static double CalculateSimilarity(string left, string right)
    {
        var leftVector = Tokenize(left);
        var rightVector = Tokenize(right);
        if (leftVector.Count == 0 || rightVector.Count == 0)
        {
            return 0d;
        }

        double dotProduct = 0d;
        foreach (var (token, leftCount) in leftVector)
        {
            if (rightVector.TryGetValue(token, out var rightCount))
            {
                dotProduct += leftCount * rightCount;
            }
        }

        var leftMagnitude = Math.Sqrt(leftVector.Values.Sum(value => value * value));
        var rightMagnitude = Math.Sqrt(rightVector.Values.Sum(value => value * value));
        if (leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon)
        {
            return 0d;
        }

        return dotProduct / (leftMagnitude * rightMagnitude);
    }

    private static Dictionary<string, int> Tokenize(string value)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in TokenPattern.Matches(value.ToLowerInvariant()))
        {
            counts[match.Value] = counts.TryGetValue(match.Value, out var current)
                ? current + 1
                : 1;
        }

        return counts;
    }
}

public sealed record DuplicateCleanupStats(
    int TotalDrawers,
    int SourceGroupCount,
    int DrawersInCandidateGroups,
    int EstimatedDuplicates,
    string? Wing,
    string? SourcePattern,
    IReadOnlyList<DuplicateSourceGroupSummary> LargestGroups);

public sealed record DuplicateSourceGroupSummary(string SourceFile, int DrawerCount);

public sealed record DuplicateCleanupResult(
    double Threshold,
    bool DryRun,
    string? Wing,
    string? SourcePattern,
    int TotalDrawersBefore,
    int TotalFilteredDrawers,
    int SourceGroupCount,
    int KeptCount,
    int DeletedCount,
    int TotalDrawersAfter,
    IReadOnlyList<DuplicateSourceCleanupResult> GroupResults);

public sealed record DuplicateSourceCleanupResult(
    string SourceFile,
    int OriginalCount,
    int KeptCount,
    int DeletedCount,
    IReadOnlyList<string> DeletedIds);

internal sealed record DuplicateSourceGroup(string SourceFile, IReadOnlyList<DrawerRecord> Drawers);
