using System.Text.Json;
using System.Text.RegularExpressions;
using MemShack.Application.Entities;
using MemShack.Core.Constants;
using MemShack.Core.Utilities;

namespace MemShack.Application.Spellcheck;

public sealed class TranscriptSpellchecker
{
    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsCamel = new(@"[A-Z][a-z]+[A-Z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsAllCaps = new(@"^[A-Z_@#$%^&*()+=\[\]{}|<>?.:/\\]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsTechnical = new(@"[-_]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsUrl = new(@"https?://|www\.|/Users/|~/|\.[a-z]{2,4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IsCodeOrEmoji = new(@"[`*_#{}[\]\\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenRegex = new(@"(\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> CorrectionMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lsresdy"] = "already",
            ["knoe"] = "know",
            ["kno"] = "know",
            ["befor"] = "before",
            ["befroe"] = "before",
            ["meny"] = "many",
            ["diferent"] = "different",
            ["tesing"] = "testing",
            ["pleese"] = "please",
            ["chekc"] = "check",
            ["realy"] = "really",
            ["writte"] = "write",
            ["alredy"] = "already",
            ["cant"] = "can't",
            ["fine-tunned"] = "fine-tuned",
            ["coherentlyy"] = "coherently",
        };

    private readonly Lazy<HashSet<string>> _knownNames;

    public TranscriptSpellchecker(string? configDirectory = null)
    {
        _knownNames = new Lazy<HashSet<string>>(() => LoadKnownNames(configDirectory), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string SpellcheckUserText(string text, IReadOnlySet<string>? knownNames = null)
    {
        var effectiveKnownNames = knownNames ?? _knownNames.Value;

        return TokenRegex.Replace(text, match =>
        {
            var token = match.Value;
            var stripped = token.TrimEnd('.', ',', '!', '?', ';', ':', '\'', '"', ')');
            var punctuation = token[stripped.Length..];
            if (string.IsNullOrWhiteSpace(stripped) || ShouldSkip(stripped, effectiveKnownNames))
            {
                return token;
            }

            if (char.IsUpper(stripped[0]))
            {
                return token;
            }

            if (!CorrectionMap.TryGetValue(stripped, out var corrected))
            {
                return token;
            }

            return corrected + punctuation;
        });
    }

    public string SpellcheckTranscriptLine(string line, IReadOnlySet<string>? knownNames = null)
    {
        var stripped = line.TrimStart();
        if (!stripped.StartsWith('>'))
        {
            return line;
        }

        var prefixLength = line.Length - stripped.Length + 1;
        if (line.Length > prefixLength && line[prefixLength] == ' ')
        {
            prefixLength++;
        }

        var message = line[prefixLength..];
        return string.IsNullOrWhiteSpace(message)
            ? line
            : line[..prefixLength] + SpellcheckUserText(message, knownNames);
    }

    public string SpellcheckTranscript(string content, IReadOnlySet<string>? knownNames = null)
    {
        return string.Join('\n', content.Split('\n').Select(line => SpellcheckTranscriptLine(line, knownNames)));
    }

    private static bool ShouldSkip(string token, IReadOnlySet<string> knownNames)
    {
        if (token.Length < 4)
        {
            return true;
        }

        if (HasDigit.IsMatch(token) ||
            IsCamel.IsMatch(token) ||
            IsAllCaps.IsMatch(token) ||
            IsTechnical.IsMatch(token) ||
            IsUrl.IsMatch(token) ||
            IsCodeOrEmoji.IsMatch(token))
        {
            return true;
        }

        return knownNames.Contains(token.ToLowerInvariant());
    }

    private static HashSet<string> LoadKnownNames(string? configDirectory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var registry = EntityRegistry.Load(configDirectory);
            foreach (var pair in registry.People)
            {
                names.Add(pair.Key.ToLowerInvariant());
                foreach (var alias in pair.Value.Aliases)
                {
                    names.Add(alias.ToLowerInvariant());
                }
            }
        }
        catch (Exception)
        {
        }

        var knownNamesPath = ResolveKnownNamesPath(configDirectory);
        if (File.Exists(knownNamesPath))
        {
            try
            {
                var node = JsonDocument.Parse(File.ReadAllText(knownNamesPath)).RootElement;
                if (node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in node.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                    {
                        names.Add(item.GetString()!.ToLowerInvariant());
                    }
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("names", out var namesProperty))
                {
                    foreach (var item in namesProperty.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                    {
                        names.Add(item.GetString()!.ToLowerInvariant());
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return names;
    }

    private static string ResolveKnownNamesPath(string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            return Path.Combine(Path.GetFullPath(PathUtilities.ExpandHome(configDirectory)), ConfigFileNames.KnownNamesJson);
        }

        return Path.Combine(
            MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
            ConfigFileNames.KnownNamesJson);
    }

    private static int EditDistance(string a, string b)
    {
        if (a == b)
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[^1];
    }
}
