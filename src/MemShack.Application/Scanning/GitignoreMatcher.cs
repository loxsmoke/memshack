using System.Text.RegularExpressions;
using MemShack.Core.Utilities;

namespace MemShack.Application.Scanning;

public sealed class GitignoreMatcher
{
    private sealed record Rule(string Pattern, bool Anchored, bool DirectoryOnly, bool Negated);

    private readonly IReadOnlyList<Rule> _rules;

    public GitignoreMatcher(string baseDirectory, IReadOnlyList<(string Pattern, bool Anchored, bool DirectoryOnly, bool Negated)> rules)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory);
        _rules = rules
            .Select(rule => new Rule(rule.Pattern, rule.Anchored, rule.DirectoryOnly, rule.Negated))
            .ToArray();
    }

    public string BaseDirectory { get; }

    public static GitignoreMatcher? FromDirectory(string directoryPath)
    {
        var gitignorePath = Path.Combine(directoryPath, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(gitignorePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var rules = new List<(string Pattern, bool Anchored, bool DirectoryOnly, bool Negated)>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(@"\#", StringComparison.Ordinal) || line.StartsWith(@"\!", StringComparison.Ordinal))
            {
                line = line[1..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var negated = line.StartsWith('!');
            if (negated)
            {
                line = line[1..];
            }

            var anchored = line.StartsWith('/');
            if (anchored)
            {
                line = line.TrimStart('/');
            }

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line.TrimEnd('/');
            }

            if (line.Length == 0)
            {
                continue;
            }

            rules.Add((line, anchored, directoryOnly, negated));
        }

        return rules.Count == 0
            ? null
            : new GitignoreMatcher(directoryPath, rules);
    }

    public bool? Matches(string path, bool? isDirectory = null)
    {
        var relative = Path.GetRelativePath(BaseDirectory, Path.GetFullPath(path));
        if (relative is "." or "")
        {
            return null;
        }

        var normalized = PathUtilities.ToPosixPath(relative).Trim('/');
        if (normalized.Length == 0 || normalized.StartsWith("../", StringComparison.Ordinal) || normalized == "..")
        {
            return null;
        }

        var directory = isDirectory ?? Directory.Exists(path);
        bool? ignored = null;

        foreach (var rule in _rules)
        {
            if (RuleMatches(rule, normalized, directory))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    public static bool IsGitignored(string path, IEnumerable<GitignoreMatcher> matchers, bool isDirectory = false)
    {
        var ignored = false;
        foreach (var matcher in matchers)
        {
            var decision = matcher.Matches(path, isDirectory);
            if (decision.HasValue)
            {
                ignored = decision.Value;
            }
        }

        return ignored;
    }

    private static bool RuleMatches(Rule rule, string relativePath, bool isDirectory)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternParts = rule.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (rule.DirectoryOnly)
        {
            var targetParts = isDirectory ? parts : parts.Take(Math.Max(parts.Length - 1, 0)).ToArray();
            if (targetParts.Length == 0)
            {
                return false;
            }

            if (rule.Anchored || patternParts.Length > 1)
            {
                return MatchFromRoot(targetParts, patternParts);
            }

            return targetParts.Any(part => GlobMatches(rule.Pattern, part));
        }

        if (rule.Anchored || patternParts.Length > 1)
        {
            return MatchFromRoot(parts, patternParts);
        }

        return parts.Any(part => GlobMatches(rule.Pattern, part));
    }

    private static bool MatchFromRoot(IReadOnlyList<string> targetParts, IReadOnlyList<string> patternParts)
    {
        var cache = new Dictionary<(int PathIndex, int PatternIndex), bool>();
        return Matches(0, 0);

        bool Matches(int pathIndex, int patternIndex)
        {
            if (cache.TryGetValue((pathIndex, patternIndex), out var cached))
            {
                return cached;
            }

            bool result;
            if (patternIndex == patternParts.Count)
            {
                result = true;
            }
            else if (pathIndex == targetParts.Count)
            {
                result = patternParts.Skip(patternIndex).All(part => part == "**");
            }
            else
            {
                var patternPart = patternParts[patternIndex];
                if (patternPart == "**")
                {
                    result = Matches(pathIndex, patternIndex + 1) || Matches(pathIndex + 1, patternIndex);
                }
                else if (!GlobMatches(patternPart, targetParts[pathIndex]))
                {
                    result = false;
                }
                else
                {
                    result = Matches(pathIndex + 1, patternIndex + 1);
                }
            }

            cache[(pathIndex, patternIndex)] = result;
            return result;
        }
    }

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(value, regex, RegexOptions.CultureInvariant);
    }
}
