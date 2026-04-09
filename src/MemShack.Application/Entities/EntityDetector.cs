using System.Text.RegularExpressions;

namespace MemShack.Application.Entities;

public sealed class EntityDetector
{
    private readonly bool _includeCamelCaseCandidates;

    public EntityDetector(bool includeCamelCaseCandidates = true)
    {
        _includeCamelCaseCandidates = includeCamelCaseCandidates;
    }

    private static readonly string[] PersonVerbPatterns =
    [
        @"\b{name}\s+said\b",
        @"\b{name}\s+asked\b",
        @"\b{name}\s+told\b",
        @"\b{name}\s+replied\b",
        @"\b{name}\s+laughed\b",
        @"\b{name}\s+smiled\b",
        @"\b{name}\s+felt\b",
        @"\b{name}\s+thinks?\b",
        @"\b{name}\s+wants?\b",
        @"\b{name}\s+loves?\b",
        @"\b{name}\s+decided\b",
        @"\b{name}\s+wrote\b",
        @"\bhey\s+{name}\b",
        @"\bthanks?\s+{name}\b",
        @"\bhi\s+{name}\b",
    ];

    private static readonly string[] PronounPatterns =
    [
        @"\bshe\b",
        @"\bher\b",
        @"\bhe\b",
        @"\bhim\b",
        @"\bthey\b",
        @"\bthem\b",
        @"\btheir\b",
    ];

    private static readonly string[] DialoguePatterns =
    [
        @"^>\s*{name}[:\s]",
        @"^{name}:\s",
        @"^\[{name}\]",
        @"""{name}\s+said",
    ];

    private static readonly string[] ProjectVerbPatterns =
    [
        @"\bbuilding\s+{name}\b",
        @"\bbuilt\s+{name}\b",
        @"\bship(?:ping|ped)?\s+{name}\b",
        @"\blaunch(?:ing|ed)?\s+{name}\b",
        @"\bdeploy(?:ing|ed)?\s+{name}\b",
        @"\binstall(?:ing|ed)?\s+{name}\b",
        @"\bthe\s+{name}\s+architecture\b",
        @"\bthe\s+{name}\s+pipeline\b",
        @"\bthe\s+{name}\s+system\b",
        @"\bthe\s+{name}\s+repo\b",
        @"\b{name}\s+v\d+\b",
        @"\b{name}\.py\b",
        @"\b{name}-core\b",
        @"\b{name}-local\b",
        @"\bimport\s+{name}\b",
        @"\bpip\s+install\s+{name}\b",
    ];

    private static readonly HashSet<string> Stopwords =
    [
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by", "from",
        "as", "is", "was", "are", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
        "will", "would", "could", "should", "may", "might", "must", "shall", "can", "this", "that", "these",
        "those", "it", "its", "they", "them", "their", "we", "our", "you", "your", "i", "my", "me", "he",
        "she", "his", "her", "who", "what", "when", "where", "why", "how", "which", "if", "then", "so", "not",
        "no", "yes", "ok", "okay", "just", "very", "really", "also", "already", "still", "even", "only", "here",
        "there", "now", "too", "up", "out", "about", "like", "use", "get", "got", "make", "made", "take", "put",
        "come", "go", "see", "know", "think", "return", "print", "def", "class", "import", "step", "usage", "run",
        "check", "find", "add", "set", "list", "args", "dict", "str", "int", "bool", "path", "file", "type",
        "name", "note", "example", "option", "result", "error", "warning", "info", "every", "each", "more", "less",
        "next", "last", "first", "second", "stack", "layer", "mode", "test", "stop", "start", "copy", "move",
        "source", "target", "output", "input", "data", "item", "key", "value", "returns", "raises", "yields",
        "self", "cls", "kwargs", "world", "well", "want", "topic", "choose", "human", "humans", "people", "things",
        "something", "nothing", "everything", "anything", "someone", "everyone", "anyone", "way", "time", "day",
        "life", "place", "thing", "part", "kind", "sort", "case", "point", "idea", "fact", "sense", "question",
        "answer", "reason", "number", "version", "system", "hey", "hi", "hello", "thanks", "thank", "right", "let",
        "click", "hit", "press", "tap", "drag", "drop", "open", "close", "save", "load", "launch", "install",
        "download", "upload", "scroll", "select", "enter", "submit", "cancel", "confirm", "delete", "paste",
        "write", "read", "search", "show", "hide", "desktop", "documents", "downloads", "users", "home", "library",
        "applications", "preferences", "settings", "terminal", "actor", "vector", "remote", "control", "duration",
        "fetch", "agents", "tools", "others", "guards", "ethics", "regulation", "learning", "thinking", "memory",
        "language", "intelligence", "technology", "society", "culture", "future", "history", "science", "model",
        "models", "network", "networks", "training", "inference",
    ];

    private static readonly HashSet<string> ProseExtensions =
    [
        ".txt",
        ".md",
        ".rst",
        ".csv",
    ];

    private static readonly HashSet<string> ReadableExtensions =
    [
        ".txt", ".md", ".py", ".js", ".ts", ".json", ".yaml", ".yml", ".csv", ".rst", ".toml", ".sh", ".rb", ".go", ".rs",
    ];

    private static readonly HashSet<string> SkipDirectories =
    [
        ".git", "node_modules", "__pycache__", ".venv", "venv", "env", "dist", "build", ".next", "coverage", ".mempalace",
        ".dotnet", ".nuget", "bin", "obj",
    ];

    public IReadOnlyList<string> ScanForDetection(string projectDirectory, int maxFiles = 10, bool prioritizeRelevantFiles = false)
    {
        var projectPath = Path.GetFullPath(projectDirectory);
        var proseFiles = new List<string>();
        var readableFiles = new List<string>();

        ScanDirectory(projectPath, proseFiles, readableFiles);
        var files = proseFiles.Count >= 3 ? proseFiles : proseFiles.Concat(readableFiles);
        if (!prioritizeRelevantFiles)
        {
            return files.Take(maxFiles).ToArray();
        }

        return files
            .OrderByDescending(filePath => ScoreDetectionFile(projectPath, filePath))
            .ThenBy(filePath => filePath, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToArray();
    }

    public DetectedEntities DetectEntities(IEnumerable<string> filePaths, int maxFiles = 10)
    {
        var allText = new List<string>();
        var allLines = new List<string>();
        var filesRead = 0;

        foreach (var filePath in filePaths)
        {
            if (filesRead >= maxFiles)
            {
                break;
            }

            try
            {
                var content = File.ReadAllText(filePath);
                if (content.Length > 5_000)
                {
                    content = content[..5_000];
                }

                allText.Add(content);
                allLines.AddRange(content.Split('\n'));
                filesRead++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var combinedText = string.Join('\n', allText);
        var candidates = ExtractCandidates(combinedText);
        if (candidates.Count == 0)
        {
            return new DetectedEntities([], [], []);
        }

        var people = new List<DetectedEntity>();
        var projects = new List<DetectedEntity>();
        var uncertain = new List<DetectedEntity>();

        foreach (var candidate in candidates.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            var scores = ScoreEntity(candidate.Key, combinedText, allLines);
            var entity = ClassifyEntity(candidate.Key, candidate.Value, scores);
            switch (entity.Type)
            {
                case "person":
                    people.Add(entity);
                    break;
                case "project":
                    projects.Add(entity);
                    break;
                default:
                    uncertain.Add(entity);
                    break;
            }
        }

        return new DetectedEntities(
            people.OrderByDescending(item => item.Confidence).Take(15).ToArray(),
            projects.OrderByDescending(item => item.Confidence).Take(10).ToArray(),
            uncertain.OrderByDescending(item => item.Frequency).Take(8).ToArray());
    }

    public DetectedEntities DetectEntitiesFromText(string text)
    {
        var lines = text.Split('\n');
        var candidates = ExtractCandidates(text);
        if (candidates.Count == 0)
        {
            return new DetectedEntities([], [], []);
        }

        var people = new List<DetectedEntity>();
        var projects = new List<DetectedEntity>();
        var uncertain = new List<DetectedEntity>();

        foreach (var candidate in candidates.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            var scores = ScoreEntity(candidate.Key, text, lines);
            var entity = ClassifyEntity(candidate.Key, candidate.Value, scores);
            switch (entity.Type)
            {
                case "person":
                    people.Add(entity);
                    break;
                case "project":
                    projects.Add(entity);
                    break;
                default:
                    uncertain.Add(entity);
                    break;
            }
        }

        return new DetectedEntities(
            people.OrderByDescending(item => item.Confidence).ToArray(),
            projects.OrderByDescending(item => item.Confidence).ToArray(),
            uncertain.OrderByDescending(item => item.Frequency).ToArray());
    }

    public IReadOnlyDictionary<string, int> ExtractCandidates(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"\b([A-Z][a-z]{1,19})\b"))
        {
            var word = match.Groups[1].Value;
            if (word.Length <= 1 || Stopwords.Contains(word.ToLowerInvariant()))
            {
                continue;
            }

            counts[word] = counts.TryGetValue(word, out var count) ? count + 1 : 1;
        }

        foreach (Match match in Regex.Matches(text, @"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b"))
        {
            var phrase = match.Groups[1].Value;
            if (phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => Stopwords.Contains(word.ToLowerInvariant())))
            {
                continue;
            }

            counts[phrase] = counts.TryGetValue(phrase, out var count) ? count + 1 : 1;
        }

        if (_includeCamelCaseCandidates)
        {
            foreach (Match match in Regex.Matches(text, @"\b([A-Z][a-z]+(?:[A-Z][a-z]+)+)\b"))
            {
                var phrase = match.Groups[1].Value;
                if (Stopwords.Contains(phrase.ToLowerInvariant()))
                {
                    continue;
                }

                counts[phrase] = counts.TryGetValue(phrase, out var count) ? count + 1 : 1;
            }
        }

        return counts
            .Where(item => item.Value >= 3)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    public EntityScores ScoreEntity(string name, string text, IReadOnlyList<string> lines)
    {
        var escaped = Regex.Escape(name);
        var personScore = 0;
        var projectScore = 0;
        var personSignals = new List<string>();
        var projectSignals = new List<string>();

        foreach (var pattern in DialoguePatterns.Select(pattern => new Regex(pattern.Replace("{name}", escaped), RegexOptions.IgnoreCase | RegexOptions.Multiline)))
        {
            var matches = pattern.Matches(text).Count;
            if (matches > 0)
            {
                personScore += matches * 3;
                personSignals.Add($"dialogue marker ({matches}x)");
            }
        }

        foreach (var pattern in PersonVerbPatterns.Select(pattern => new Regex(pattern.Replace("{name}", escaped), RegexOptions.IgnoreCase)))
        {
            var matches = pattern.Matches(text).Count;
            if (matches > 0)
            {
                personScore += matches * 2;
                personSignals.Add($"'{name} ...' action ({matches}x)");
            }
        }

        var nameLower = name.ToLowerInvariant();
        var pronounHits = 0;
        var nameLineIndexes = lines
            .Select((line, index) => new { Line = line, Index = index })
            .Where(item => item.Line.Contains(nameLower, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Index);
        foreach (var index in nameLineIndexes)
        {
            var start = Math.Max(0, index - 2);
            var count = Math.Min(lines.Count - start, 5);
            var windowText = string.Join(' ', lines.Skip(start).Take(count)).ToLowerInvariant();
            if (PronounPatterns.Any(pattern => Regex.IsMatch(windowText, pattern, RegexOptions.IgnoreCase)))
            {
                pronounHits++;
            }
        }

        if (pronounHits > 0)
        {
            personScore += pronounHits * 2;
            personSignals.Add($"pronoun nearby ({pronounHits}x)");
        }

        var directAddress = Regex.Matches(text, $@"\bhey\s+{escaped}\b|\bthanks?\s+{escaped}\b|\bhi\s+{escaped}\b", RegexOptions.IgnoreCase).Count;
        if (directAddress > 0)
        {
            personScore += directAddress * 4;
            personSignals.Add($"addressed directly ({directAddress}x)");
        }

        foreach (var pattern in ProjectVerbPatterns.Select(pattern => new Regex(pattern.Replace("{name}", escaped), RegexOptions.IgnoreCase)))
        {
            var matches = pattern.Matches(text).Count;
            if (matches > 0)
            {
                projectScore += matches * 2;
                projectSignals.Add($"project verb ({matches}x)");
            }
        }

        var versioned = Regex.Matches(text, $@"\b{escaped}[-v]\w+", RegexOptions.IgnoreCase).Count;
        if (versioned > 0)
        {
            projectScore += versioned * 3;
            projectSignals.Add($"versioned/hyphenated ({versioned}x)");
        }

        var codeRef = Regex.Matches(text, $@"\b{escaped}\.(py|js|ts|yaml|yml|json|sh)\b", RegexOptions.IgnoreCase).Count;
        if (codeRef > 0)
        {
            projectScore += codeRef * 3;
            projectSignals.Add($"code file reference ({codeRef}x)");
        }

        return new EntityScores(personScore, projectScore, personSignals.Take(3).ToArray(), projectSignals.Take(3).ToArray());
    }

    public DetectedEntity ClassifyEntity(string name, int frequency, EntityScores scores)
    {
        var total = scores.PersonScore + scores.ProjectScore;
        if (total == 0)
        {
            return new DetectedEntity(name, "uncertain", Math.Round(Math.Min(0.4, frequency / 50d), 2), frequency, [$"appears {frequency}x, no strong type signals"]);
        }

        var personRatio = scores.PersonScore / (double)total;
        var signalCategories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signal in scores.PersonSignals)
        {
            if (signal.Contains("dialogue", StringComparison.Ordinal))
            {
                signalCategories.Add("dialogue");
            }
            else if (signal.Contains("action", StringComparison.Ordinal))
            {
                signalCategories.Add("action");
            }
            else if (signal.Contains("pronoun", StringComparison.Ordinal))
            {
                signalCategories.Add("pronoun");
            }
            else if (signal.Contains("addressed", StringComparison.Ordinal))
            {
                signalCategories.Add("addressed");
            }
        }

        if (personRatio >= 0.7 && signalCategories.Count >= 2 && scores.PersonScore >= 5)
        {
            return new DetectedEntity(name, "person", Math.Round(Math.Min(0.99, 0.5 + personRatio * 0.5), 2), frequency, scores.PersonSignals);
        }

        if (personRatio >= 0.7)
        {
            return new DetectedEntity(name, "uncertain", 0.4, frequency, scores.PersonSignals.Concat([$"appears {frequency}x \u2014 pronoun-only match"]).ToArray());
        }

        if (personRatio <= 0.3)
        {
            return new DetectedEntity(name, "project", Math.Round(Math.Min(0.99, 0.5 + (1 - personRatio) * 0.5), 2), frequency, scores.ProjectSignals.Count > 0 ? scores.ProjectSignals : [$"appears {frequency}x"]);
        }

        return new DetectedEntity(name, "uncertain", 0.5, frequency, scores.PersonSignals.Concat(scores.ProjectSignals).Take(3).Concat(["mixed signals \u2014 needs review"]).ToArray());
    }

    private static void ScanDirectory(string directory, ICollection<string> proseFiles, ICollection<string> readableFiles)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (ProseExtensions.Contains(extension))
            {
                proseFiles.Add(file);
            }
            else if (ReadableExtensions.Contains(extension))
            {
                readableFiles.Add(file);
            }
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            if (SkipDirectories.Contains(Path.GetFileName(childDirectory)))
            {
                continue;
            }

            ScanDirectory(childDirectory, proseFiles, readableFiles);
        }
    }

    private static int ScoreDetectionFile(string projectPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(projectPath, filePath);
        var segments = relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        var score = 0;

        score -= segments.Length * 5;

        if (segments.Length == 1)
        {
            score += 100;
        }

        if (fileName == "readme")
        {
            score += 90;
        }

        if (fileName.Contains("migration", StringComparison.Ordinal))
        {
            score += 75;
        }

        if (fileName.Contains("tool", StringComparison.Ordinal) || fileName.Contains("install", StringComparison.Ordinal))
        {
            score += 60;
        }

        if (fileName.Contains("roadmap", StringComparison.Ordinal) || fileName.Contains("architecture", StringComparison.Ordinal))
        {
            score += 45;
        }

        if (segments.Any(segment => string.Equals(segment, "notes", StringComparison.OrdinalIgnoreCase)))
        {
            score += 40;
        }

        if (segments.Any(segment => string.Equals(segment, ".github", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 150;
        }

        if (segments.Any(segment =>
                string.Equals(segment, "compatibility", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "validation", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 120;
        }

        if (segments.Any(segment =>
                string.Equals(segment, ".dotnet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".nuget", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 120;
        }

        if (segments.Any(segment =>
                string.Equals(segment, "benchmarks", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "examples", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 80;
        }

        if (segments.Any(segment => string.Equals(segment, "fixtures", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 10;
        }

        return score;
    }
}

public sealed record EntityScores(
    int PersonScore,
    int ProjectScore,
    IReadOnlyList<string> PersonSignals,
    IReadOnlyList<string> ProjectSignals);
