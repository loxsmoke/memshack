using System.Text.Json;
using System.Text.RegularExpressions;
using MemShack.Core.Models;

namespace MemShack.Application.Compression;

public sealed class AaakDialect
{
    private static readonly Regex TopicWordRegex = new(@"[a-zA-Z][a-zA-Z_-]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SentenceSplitter = new(@"[.!?\n]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> EmotionSignals =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decided"] = "determ",
            ["prefer"] = "convict",
            ["worried"] = "anx",
            ["excited"] = "excite",
            ["frustrated"] = "frust",
            ["confused"] = "confuse",
            ["love"] = "love",
            ["hate"] = "rage",
            ["hope"] = "hope",
            ["fear"] = "fear",
            ["trust"] = "trust",
            ["happy"] = "joy",
            ["sad"] = "grief",
            ["surprised"] = "surprise",
            ["grateful"] = "grat",
            ["curious"] = "curious",
            ["wonder"] = "wonder",
            ["anxious"] = "anx",
            ["relieved"] = "relief",
            ["satisf"] = "satis",
            ["disappoint"] = "grief",
            ["concern"] = "anx",
        };

    private static readonly IReadOnlyDictionary<string, string> FlagSignals =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decided"] = "DECISION",
            ["chose"] = "DECISION",
            ["switched"] = "DECISION",
            ["migrated"] = "DECISION",
            ["replaced"] = "DECISION",
            ["instead of"] = "DECISION",
            ["because"] = "DECISION",
            ["founded"] = "ORIGIN",
            ["created"] = "ORIGIN",
            ["started"] = "ORIGIN",
            ["born"] = "ORIGIN",
            ["launched"] = "ORIGIN",
            ["first time"] = "ORIGIN",
            ["core"] = "CORE",
            ["fundamental"] = "CORE",
            ["essential"] = "CORE",
            ["principle"] = "CORE",
            ["belief"] = "CORE",
            ["always"] = "CORE",
            ["never forget"] = "CORE",
            ["turning point"] = "PIVOT",
            ["changed everything"] = "PIVOT",
            ["realized"] = "PIVOT",
            ["breakthrough"] = "PIVOT",
            ["epiphany"] = "PIVOT",
            ["api"] = "TECHNICAL",
            ["database"] = "TECHNICAL",
            ["architecture"] = "TECHNICAL",
            ["deploy"] = "TECHNICAL",
            ["infrastructure"] = "TECHNICAL",
            ["algorithm"] = "TECHNICAL",
            ["framework"] = "TECHNICAL",
            ["server"] = "TECHNICAL",
            ["config"] = "TECHNICAL",
        };

    private static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
        "do", "does", "did", "will", "would", "could", "should", "may", "might", "shall", "can",
        "to", "of", "in", "for", "on", "with", "at", "by", "from", "as", "into", "about", "between",
        "through", "during", "before", "after", "above", "below", "up", "down", "out", "off", "over",
        "under", "again", "further", "then", "once", "here", "there", "when", "where", "why", "how",
        "all", "each", "every", "both", "few", "more", "most", "other", "some", "such", "no", "nor",
        "not", "only", "own", "same", "so", "than", "too", "very", "just", "don", "now", "and", "but",
        "or", "if", "while", "that", "this", "these", "those", "it", "its", "i", "we", "you", "he",
        "she", "they", "me", "him", "her", "us", "them", "my", "your", "his", "our", "their", "what",
        "which", "who", "whom", "also", "much", "many", "like", "because", "since", "get", "got", "use",
        "used", "using", "make", "made", "thing", "things", "way", "well", "really", "want", "need",
    ];

    private readonly Dictionary<string, string> _canonicalEntityCodes;
    private readonly HashSet<string> _skipNames;

    public AaakDialect(IReadOnlyDictionary<string, string>? entities = null, IReadOnlyList<string>? skipNames = null)
    {
        _canonicalEntityCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        _skipNames = new HashSet<string>((skipNames ?? Array.Empty<string>()).Select(name => name.ToLowerInvariant()), StringComparer.Ordinal);

        if (entities is null)
        {
            return;
        }

        foreach (var pair in entities)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                _canonicalEntityCodes[pair.Key] = pair.Value;
            }
        }
    }

    public static AaakDialect FromConfig(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipNames = new List<string>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("entities", out var entitiesElement) && entitiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in entitiesElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        entities[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }

            if (root.TryGetProperty("skip_names", out var skipNamesElement) && skipNamesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in skipNamesElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                {
                    skipNames.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        return new AaakDialect(entities, skipNames);
    }

    public string Compress(string text, DrawerMetadata? metadata = null)
    {
        var entities = DetectEntitiesInText(text);
        var entityPart = entities.Count > 0 ? string.Join('+', entities.Take(3)) : "???";
        var topics = ExtractTopics(text);
        var topicPart = topics.Count > 0 ? string.Join('_', topics.Take(3)) : "misc";
        var keySentence = ExtractKeySentence(text);
        var emotions = DetectSignals(text, EmotionSignals);
        var flags = DetectSignals(text, FlagSignals);

        var lines = new List<string>();
        if (metadata is not null && (!string.IsNullOrWhiteSpace(metadata.SourceFile) || !string.IsNullOrWhiteSpace(metadata.Wing)))
        {
            lines.Add(
                string.Join('|',
                [
                    metadata.Wing.Length > 0 ? metadata.Wing : "?",
                    metadata.Room.Length > 0 ? metadata.Room : "?",
                    !string.IsNullOrWhiteSpace(metadata.Date) ? metadata.Date : "?",
                    !string.IsNullOrWhiteSpace(metadata.SourceFile) ? Path.GetFileNameWithoutExtension(metadata.SourceFile) : "?",
                ]));
        }

        var entry = new List<string> { $"0:{entityPart}", topicPart };
        if (keySentence.Length > 0)
        {
            entry.Add($"\"{keySentence.Replace("\"", "'", StringComparison.Ordinal)}\"");
        }

        if (emotions.Count > 0)
        {
            entry.Add(string.Join('+', emotions));
        }

        if (flags.Count > 0)
        {
            entry.Add(string.Join('+', flags));
        }

        lines.Add(string.Join('|', entry));
        return string.Join('\n', lines);
    }

    public AaakCompressionStats CompressionStats(string originalText, string compressed)
    {
        var originalTokens = CountTokens(originalText);
        var compressedTokens = CountTokens(compressed);
        return new AaakCompressionStats(
            originalTokens,
            compressedTokens,
            Math.Round(originalTokens / (double)Math.Max(compressedTokens, 1), 1),
            originalText.Length,
            compressed.Length);
    }

    public static int CountTokens(string text)
    {
        var wordCount = Regex.Matches(text, @"\S+").Count;
        return Math.Max(1, (int)(wordCount * 1.3));
    }

    private IReadOnlyList<string> DetectEntitiesInText(string text)
    {
        var found = new List<string>();
        foreach (var pair in _canonicalEntityCodes)
        {
            if (_skipNames.Any(skipName => pair.Key.Contains(skipName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (pair.Key.Any(char.IsUpper) &&
                text.Contains(pair.Key, StringComparison.OrdinalIgnoreCase) &&
                !found.Contains(pair.Value, StringComparer.Ordinal))
            {
                found.Add(pair.Value);
            }
        }

        if (found.Count > 0)
        {
            return found;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < words.Length; index++)
        {
            var clean = Regex.Replace(words[index], @"[^a-zA-Z]", string.Empty);
            if (clean.Length < 2 || index == 0 || !char.IsUpper(clean[0]) || !clean[1..].All(char.IsLower))
            {
                continue;
            }

            if (StopWords.Contains(clean.ToLowerInvariant()))
            {
                continue;
            }

            var code = clean[..Math.Min(3, clean.Length)].ToUpperInvariant();
            if (!found.Contains(code, StringComparer.Ordinal))
            {
                found.Add(code);
            }

            if (found.Count >= 3)
            {
                break;
            }
        }

        return found;
    }

    private static IReadOnlyList<string> DetectSignals(string text, IReadOnlyDictionary<string, string> signalMap)
    {
        var lowered = text.ToLowerInvariant();
        var detected = new List<string>();
        foreach (var pair in signalMap)
        {
            if (lowered.Contains(pair.Key, StringComparison.Ordinal) && !detected.Contains(pair.Value, StringComparer.Ordinal))
            {
                detected.Add(pair.Value);
            }
        }

        return detected.Take(3).ToArray();
    }

    private static IReadOnlyList<string> ExtractTopics(string text, int maxTopics = 3)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in TopicWordRegex.Matches(text))
        {
            var word = match.Value;
            var lowered = word.ToLowerInvariant();
            if (lowered.Length < 3 || StopWords.Contains(lowered))
            {
                continue;
            }

            frequencies[lowered] = frequencies.TryGetValue(lowered, out var count) ? count + 1 : 1;
        }

        foreach (Match match in TopicWordRegex.Matches(text))
        {
            var word = match.Value;
            var lowered = word.ToLowerInvariant();
            if (!frequencies.ContainsKey(lowered))
            {
                continue;
            }

            if (char.IsUpper(word[0]))
            {
                frequencies[lowered] += 2;
            }

            if (word.Contains('_', StringComparison.Ordinal) ||
                word.Contains('-', StringComparison.Ordinal) ||
                word[1..].Any(char.IsUpper))
            {
                frequencies[lowered] += 2;
            }
        }

        return frequencies
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(maxTopics)
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static string ExtractKeySentence(string text)
    {
        var decisionWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "decided", "because", "instead", "prefer", "switched", "chose", "realized", "important",
            "key", "critical", "discovered", "learned", "conclusion", "solution", "reason", "why",
            "breakthrough", "insight",
        };

        var sentences = SentenceSplitter.Split(text)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 10)
            .ToArray();
        if (sentences.Length == 0)
        {
            return string.Empty;
        }

        var best = sentences
            .Select(sentence =>
            {
                var lowered = sentence.ToLowerInvariant();
                var score = decisionWords.Count(word => lowered.Contains(word, StringComparison.Ordinal)) * 2;
                if (sentence.Length < 80)
                {
                    score++;
                }

                if (sentence.Length < 40)
                {
                    score++;
                }

                if (sentence.Length > 150)
                {
                    score -= 2;
                }

                return (Sentence: sentence, Score: score);
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Sentence.Length)
            .First()
            .Sentence;

        return best.Length > 55 ? $"{best[..52]}..." : best;
    }
}
