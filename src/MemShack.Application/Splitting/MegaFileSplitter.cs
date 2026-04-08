using System.Text.Json;
using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Utilities;

namespace MemShack.Application.Splitting;

public sealed class MegaFileSplitter
{
    private static readonly IReadOnlyList<string> FallbackKnownPeople =
    [
        "Alice",
        "Ben",
        "Riley",
        "Max",
        "Sam",
        "Devon",
        "Jordan",
    ];

    private static readonly Regex TimestampRegex = new(
        @"(?:⏺\s+)?(\d{1,2}:\d{2}\s+[AP]M)\s+\w+,\s+(\w+)\s+(\d{1,2}),\s+(\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SkipPromptRegex = new(
        @"^(\./|cd |ls |python|bash|git |cat |source |export |claude|./activate)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UsernamePathRegex = new(
        @"[\\/](?:Users|home)[\\/](\w+)[\\/]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string? _configDirectory;

    public MegaFileSplitter(string? configDirectory = null)
    {
        _configDirectory = configDirectory;
    }

    public MegaFileSplitRunResult Split(
        string sourceDirectory,
        string? outputDirectory = null,
        bool dryRun = false,
        int minSessions = 2,
        string? filePath = null)
    {
        var knownNamesConfig = LoadKnownNamesConfig();
        var knownPeople = LoadKnownPeople(knownNamesConfig);
        var usernameMap = LoadUsernameMap(knownNamesConfig);

        var sourcePath = Path.GetFullPath(PathUtilities.ExpandHome(sourceDirectory));
        var outputPath = string.IsNullOrWhiteSpace(outputDirectory)
            ? null
            : Path.GetFullPath(PathUtilities.ExpandHome(outputDirectory));

        var files = ResolveSourceFiles(sourcePath, filePath);
        var items = new List<MegaFileSplitItem>();
        var sessionsCreated = 0;

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            var boundaries = FindSessionBoundaries(lines);
            if (boundaries.Count < minSessions)
            {
                continue;
            }

            var splitItem = SplitFile(file, lines, boundaries, outputPath, knownPeople, usernameMap, dryRun);
            items.Add(splitItem);
            sessionsCreated += splitItem.Sessions.Count;
        }

        return new MegaFileSplitRunResult(
            sourcePath,
            outputPath,
            items.Count,
            sessionsCreated,
            dryRun,
            items);
    }

    public static IReadOnlyList<int> FindSessionBoundaries(IReadOnlyList<string> lines)
    {
        var boundaries = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains("Claude Code v", StringComparison.Ordinal) && IsTrueSessionStart(lines, index))
            {
                boundaries.Add(index);
            }
        }

        return boundaries;
    }

    private static bool IsTrueSessionStart(IReadOnlyList<string> lines, int index)
    {
        var nearby = string.Join(string.Empty, lines.Skip(index).Take(6));
        return !nearby.Contains("Ctrl+E", StringComparison.OrdinalIgnoreCase) &&
               !nearby.Contains("previous messages", StringComparison.OrdinalIgnoreCase);
    }

    private static MegaFileSplitItem SplitFile(
        string filePath,
        IReadOnlyList<string> lines,
        IReadOnlyList<int> boundaries,
        string? outputDirectory,
        IReadOnlyList<string> knownPeople,
        IReadOnlyDictionary<string, string> usernameMap,
        bool dryRun)
    {
        var allBoundaries = boundaries.Concat([lines.Count]).ToArray();
        var sessions = new List<SplitSessionResult>();
        var resolvedOutputDirectory = outputDirectory ?? Path.GetDirectoryName(filePath) ?? Path.GetDirectoryName(Path.GetFullPath(filePath))!;

        if (!dryRun)
        {
            Directory.CreateDirectory(resolvedOutputDirectory);
        }

        for (var index = 0; index < allBoundaries.Length - 1; index++)
        {
            var start = allBoundaries[index];
            var end = allBoundaries[index + 1];
            var chunk = lines.Skip(start).Take(end - start).ToArray();
            if (chunk.Length < 10)
            {
                continue;
            }

            var timestamp = ExtractTimestamp(chunk) ?? $"part{index + 1:00}";
            var people = ExtractPeople(chunk, knownPeople, usernameMap);
            var subject = ExtractSubject(chunk);
            var rawSourceStem = Path.GetFileNameWithoutExtension(filePath);
            var sourceStem = Regex.Replace(rawSourceStem, @"[^\w-]", "_");
            if (sourceStem.Length > 40)
            {
                sourceStem = sourceStem[..40];
            }

            var peoplePart = people.Count > 0 ? string.Join('-', people.Take(3)) : "unknown";
            var fileName = $"{sourceStem}__{timestamp}_{peoplePart}_{subject}.txt";
            fileName = Regex.Replace(fileName, @"[^\w\.-]", "_");
            fileName = Regex.Replace(fileName, @"_+", "_");

            var sessionPath = Path.Combine(resolvedOutputDirectory, fileName);
            if (!dryRun)
            {
                File.WriteAllText(sessionPath, string.Join('\n', chunk));
            }

            sessions.Add(new SplitSessionResult(sessionPath, chunk.Length, !dryRun));
        }

        string? backupPath = null;
        if (!dryRun && sessions.Count > 0)
        {
            backupPath = Path.ChangeExtension(filePath, ".mega_backup");
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(filePath, backupPath);
        }

        return new MegaFileSplitItem(filePath, boundaries.Count, sessions, backupPath);
    }

    private static string? ExtractTimestamp(IReadOnlyList<string> lines)
    {
        var months = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["January"] = "01",
            ["February"] = "02",
            ["March"] = "03",
            ["April"] = "04",
            ["May"] = "05",
            ["June"] = "06",
            ["July"] = "07",
            ["August"] = "08",
            ["September"] = "09",
            ["October"] = "10",
            ["November"] = "11",
            ["December"] = "12",
        };

        foreach (var line in lines.Take(50))
        {
            var match = TimestampRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var month = months.TryGetValue(match.Groups[2].Value, out var monthValue) ? monthValue : "00";
            var day = match.Groups[3].Value.PadLeft(2, '0');
            var time = match.Groups[1].Value.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            return $"{match.Groups[4].Value}-{month}-{day}_{time}";
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractPeople(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> knownPeople,
        IReadOnlyDictionary<string, string> usernameMap)
    {
        var text = string.Join('\n', lines.Take(100));
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var person in knownPeople)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(person)}\b", RegexOptions.IgnoreCase))
            {
                found.Add(person);
            }
        }

        var usernameMatch = UsernamePathRegex.Match(text);
        if (usernameMatch.Success && usernameMap.TryGetValue(usernameMatch.Groups[1].Value, out var personName))
        {
            found.Add(personName);
        }

        return found.ToArray();
    }

    private static string ExtractSubject(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("> ", StringComparison.Ordinal))
            {
                continue;
            }

            var prompt = line[2..].Trim();
            if (prompt.Length <= 5 || SkipPromptRegex.IsMatch(prompt))
            {
                continue;
            }

            var cleaned = Regex.Replace(prompt, @"[^\w\s-]", string.Empty);
            cleaned = Regex.Replace(cleaned.Trim(), @"\s+", "-");
            return cleaned.Length > 60 ? cleaned[..60] : cleaned;
        }

        return "session";
    }

    private static IReadOnlyList<string> ResolveSourceFiles(string sourceDirectory, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return [Path.GetFullPath(PathUtilities.ExpandHome(filePath))];
        }

        if (!Directory.Exists(sourceDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(sourceDirectory, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private object? LoadKnownNamesConfig()
    {
        var configPath = ResolveKnownNamesPath();
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object>(File.ReadAllText(configPath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> LoadKnownPeople(object? knownNamesConfig)
    {
        if (knownNamesConfig is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToArray();
            }

            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("names", out var names))
            {
                return names.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToArray();
            }
        }

        return FallbackKnownPeople;
    }

    private static IReadOnlyDictionary<string, string> LoadUsernameMap(object? knownNamesConfig)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (knownNamesConfig is JsonElement element &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("username_map", out var usernameMapElement) &&
            usernameMapElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in usernameMapElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    map[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }

        return map;
    }

    private string ResolveKnownNamesPath()
    {
        if (!string.IsNullOrWhiteSpace(_configDirectory))
        {
            return Path.Combine(Path.GetFullPath(PathUtilities.ExpandHome(_configDirectory)), ConfigFileNames.KnownNamesJson);
        }

        return Path.Combine(
            MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
            ConfigFileNames.KnownNamesJson);
    }
}
