using System.Text.RegularExpressions;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;

namespace MemShack.Application.Extraction;

public sealed partial class GeneralMemoryExtractor : IGeneralMemoryExtractor
{
    private static readonly IReadOnlyDictionary<string, string[]> MarkerSets = new Dictionary<string, string[]>
    {
        ["decision"] =
        [
            @"\blet'?s (use|go with|try|pick|choose|switch to)\b",
            @"\bwe (should|decided|chose|went with|picked|settled on)\b",
            @"\bi'?m going (to|with)\b",
            @"\bbetter (to|than|approach|option|choice)\b",
            @"\binstead of\b",
            @"\brather than\b",
            @"\bthe reason (is|was|being)\b",
            @"\bbecause\b",
            @"\btrade-?off\b",
            @"\bpros and cons\b",
            @"\bover\b.*\bbecause\b",
            @"\barchitecture\b",
            @"\bapproach\b",
            @"\bstrategy\b",
            @"\bpattern\b",
            @"\bstack\b",
            @"\bframework\b",
            @"\binfrastructure\b",
            @"\bset (it |this )?to\b",
            @"\bconfigure\b",
            @"\bdefault\b",
        ],
        ["preference"] =
        [
            @"\bi prefer\b",
            @"\balways use\b",
            @"\bnever use\b",
            @"\bdon'?t (ever |like to )?(use|do|mock|stub|import)\b",
            @"\bi like (to|when|how)\b",
            @"\bi hate (when|how|it when)\b",
            @"\bplease (always|never|don'?t)\b",
            @"\bmy (rule|preference|style|convention) is\b",
            @"\bwe (always|never)\b",
            @"\bfunctional\b.*\bstyle\b",
            @"\bimperative\b",
            @"\bsnake_?case\b",
            @"\bcamel_?case\b",
            @"\btabs\b.*\bspaces\b",
            @"\bspaces\b.*\btabs\b",
            @"\buse\b.*\binstead of\b",
        ],
        ["milestone"] =
        [
            @"\bit works\b",
            @"\bit worked\b",
            @"\bgot it working\b",
            @"\bfixed\b",
            @"\bsolved\b",
            @"\bbreakthrough\b",
            @"\bfigured (it )?out\b",
            @"\bnailed it\b",
            @"\bcracked (it|the)\b",
            @"\bfinally\b",
            @"\bfirst time\b",
            @"\bfirst ever\b",
            @"\bnever (done|been|had) before\b",
            @"\bdiscovered\b",
            @"\brealized\b",
            @"\bfound (out|that)\b",
            @"\bturns out\b",
            @"\bthe key (is|was|insight)\b",
            @"\bthe trick (is|was)\b",
            @"\bnow i (understand|see|get it)\b",
            @"\bbuilt\b",
            @"\bcreated\b",
            @"\bimplemented\b",
            @"\bshipped\b",
            @"\blaunched\b",
            @"\bdeployed\b",
            @"\breleased\b",
            @"\bprototype\b",
            @"\bproof of concept\b",
            @"\bdemo\b",
            @"\bversion \d",
            @"\bv\d+\.\d+",
            @"\d+x (compression|faster|slower|better|improvement|reduction)",
            @"\d+% (reduction|improvement|faster|better|smaller)",
        ],
        ["problem"] =
        [
            @"\b(bug|error|crash|fail|broke|broken|issue|problem)\b",
            @"\bdoesn'?t work\b",
            @"\bnot working\b",
            @"\bwon'?t\b.*\bwork\b",
            @"\bkeeps? (failing|crashing|breaking|erroring)\b",
            @"\broot cause\b",
            @"\bthe (problem|issue|bug) (is|was)\b",
            @"\bturns out\b.*\b(was|because|due to)\b",
            @"\bthe fix (is|was)\b",
            @"\bworkaround\b",
            @"\bthat'?s why\b",
            @"\bthe reason it\b",
            @"\bfixed (it |the |by )\b",
            @"\bsolution (is|was)\b",
            @"\bresolved\b",
            @"\bpatched\b",
            @"\bthe answer (is|was)\b",
            @"\b(had|need) to\b.*\binstead\b",
        ],
        ["emotional"] =
        [
            @"\blove\b",
            @"\bscared\b",
            @"\bafraid\b",
            @"\bproud\b",
            @"\bhurt\b",
            @"\bhappy\b",
            @"\bsad\b",
            @"\bcry\b",
            @"\bcrying\b",
            @"\bmiss\b",
            @"\bsorry\b",
            @"\bgrateful\b",
            @"\bangry\b",
            @"\bworried\b",
            @"\blonely\b",
            @"\bbeautiful\b",
            @"\bamazing\b",
            @"\bwonderful\b",
            @"i feel",
            @"i'm scared",
            @"i love you",
            @"i'm sorry",
            @"i can't",
            @"i wish",
            @"i miss",
            @"i need",
            @"never told anyone",
            @"nobody knows",
            @"\*[^*]+\*",
        ],
    };

    private static readonly HashSet<string> PositiveWords =
    [
        "pride",
        "proud",
        "joy",
        "happy",
        "love",
        "loving",
        "beautiful",
        "amazing",
        "wonderful",
        "incredible",
        "fantastic",
        "brilliant",
        "perfect",
        "excited",
        "thrilled",
        "grateful",
        "warm",
        "breakthrough",
        "success",
        "works",
        "working",
        "solved",
        "fixed",
        "nailed",
        "heart",
        "hug",
        "precious",
        "adore",
    ];

    private static readonly HashSet<string> NegativeWords =
    [
        "bug",
        "error",
        "crash",
        "crashing",
        "crashed",
        "fail",
        "failed",
        "failing",
        "failure",
        "broken",
        "broke",
        "breaking",
        "breaks",
        "issue",
        "problem",
        "wrong",
        "stuck",
        "blocked",
        "unable",
        "impossible",
        "missing",
        "terrible",
        "horrible",
        "awful",
        "worse",
        "worst",
        "panic",
        "disaster",
        "mess",
    ];

    private static readonly Regex[] CodeLinePatterns =
    [
        new(@"^\s*[\$#]\s", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*(cd|source|echo|export|pip|npm|git|python|bash|curl|wget|mkdir|rm|cp|mv|ls|cat|grep|find|chmod|sudo|brew|docker)\s", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"^\s*```", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*(import|from|def|class|function|const|let|var|return)\s", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"^\s*[A-Z_]{2,}=", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*\|", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*[-]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*[{}\[\]]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*(if|for|while|try|except|elif|else:)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"^\s*\w+\.\w+\(", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^\s*\w+ = \w+\.\w+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    public IReadOnlyList<ExtractedMemory> ExtractMemories(string text, double minConfidence = 0.3)
    {
        var memories = new List<ExtractedMemory>();
        foreach (var segment in SplitIntoSegments(text))
        {
            if (segment.Trim().Length < 20)
            {
                continue;
            }

            var prose = ExtractProse(segment);
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var markerSet in MarkerSets)
            {
                var score = ScoreMarkers(prose, markerSet.Value);
                if (score > 0)
                {
                    scores[markerSet.Key] = score;
                }
            }

            if (scores.Count == 0)
            {
                continue;
            }

            var lengthBonus = segment.Length switch
            {
                > 500 => 2,
                > 200 => 1,
                _ => 0,
            };

            var memoryType = scores.OrderByDescending(item => item.Value).First().Key;
            var maxScore = scores[memoryType] + lengthBonus;
            memoryType = Disambiguate(memoryType, prose, scores);

            var confidence = Math.Min(1.0, maxScore / 5.0);
            if (confidence < minConfidence)
            {
                continue;
            }

            memories.Add(new ExtractedMemory(segment.Trim(), memoryType, memories.Count));
        }

        return memories;
    }

    private static IReadOnlyList<string> SplitIntoSegments(string text)
    {
        var lines = text.Split('\n');
        var turnPatterns = SpeakerTurnPatterns();
        var turnCount = 0;

        foreach (var line in lines)
        {
            var stripped = line.Trim();
            if (turnPatterns.Any(pattern => pattern.IsMatch(stripped)))
            {
                turnCount++;
            }
        }

        if (turnCount >= 3)
        {
            return SplitByTurns(lines, turnPatterns);
        }

        var paragraphs = text
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        if (paragraphs.Count <= 1 && lines.Length > 20)
        {
            return lines
                .Chunk(25)
                .Select(group => string.Join('\n', group).Trim())
                .Where(group => group.Length > 0)
                .ToArray();
        }

        return paragraphs;
    }

    private static IReadOnlyList<string> SplitByTurns(IReadOnlyList<string> lines, IReadOnlyList<Regex> turnPatterns)
    {
        var segments = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            var stripped = line.Trim();
            var isTurn = turnPatterns.Any(pattern => pattern.IsMatch(stripped));

            if (isTurn && current.Count > 0)
            {
                segments.Add(string.Join('\n', current));
                current = [line];
            }
            else
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            segments.Add(string.Join('\n', current));
        }

        return segments;
    }

    private static string ExtractProse(string text)
    {
        var lines = text.Split('\n');
        var prose = new List<string>();
        var inCode = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                continue;
            }

            if (!IsCodeLine(line))
            {
                prose.Add(line);
            }
        }

        var result = string.Join('\n', prose).Trim();
        return result.Length > 0 ? result : text;
    }

    private static bool IsCodeLine(string line)
    {
        var stripped = line.Trim();
        if (stripped.Length == 0)
        {
            return false;
        }

        if (CodeLinePatterns.Any(pattern => pattern.IsMatch(stripped)))
        {
            return true;
        }

        var alphaCharacters = stripped.Count(char.IsLetter);
        var alphaRatio = alphaCharacters / (double)Math.Max(stripped.Length, 1);
        return alphaRatio < 0.4 && stripped.Length > 10;
    }

    private static double ScoreMarkers(string text, IEnumerable<string> markers)
    {
        var lowered = text.ToLowerInvariant();
        double score = 0;

        foreach (var marker in markers)
        {
            score += Regex.Matches(lowered, marker, RegexOptions.CultureInvariant).Count;
        }

        return score;
    }

    private static string Disambiguate(string memoryType, string text, IReadOnlyDictionary<string, double> scores)
    {
        var sentiment = GetSentiment(text);

        if (memoryType == "problem" && HasResolution(text))
        {
            if (scores.TryGetValue("emotional", out var emotionalScore) && emotionalScore > 0 && sentiment == "positive")
            {
                return "emotional";
            }

            return "milestone";
        }

        if (memoryType == "problem" && sentiment == "positive")
        {
            if (scores.TryGetValue("milestone", out var milestoneScore) && milestoneScore > 0)
            {
                return "milestone";
            }

            if (scores.TryGetValue("emotional", out var emotionalScore) && emotionalScore > 0)
            {
                return "emotional";
            }
        }

        return memoryType;
    }

    private static string GetSentiment(string text)
    {
        var words = WordPattern().Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var positive = words.Count(PositiveWords.Contains);
        var negative = words.Count(NegativeWords.Contains);

        return positive.CompareTo(negative) switch
        {
            > 0 => "positive",
            < 0 => "negative",
            _ => "neutral",
        };
    }

    private static bool HasResolution(string text)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        string[] patterns =
        [
            @"\bfixed\b",
            @"\bsolved\b",
            @"\bresolved\b",
            @"\bpatched\b",
            @"\bgot it working\b",
            @"\bit works\b",
            @"\bnailed it\b",
            @"\bfigured (it )?out\b",
            @"\bthe (fix|answer|solution)\b",
        ];

        return patterns.Any(pattern => Regex.IsMatch(text, pattern, Options));
    }

    [GeneratedRegex(@"\b\w+\b", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^>\s", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteTurnPattern();

    [GeneratedRegex(@"^(Human|User|Q)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HumanTurnPattern();

    [GeneratedRegex(@"^(Assistant|AI|A|Claude|ChatGPT)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssistantTurnPattern();

    private static IReadOnlyList<Regex> SpeakerTurnPatterns() =>
        [QuoteTurnPattern(), HumanTurnPattern(), AssistantTurnPattern()];
}
