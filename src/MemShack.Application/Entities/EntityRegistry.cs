using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Utilities;

namespace MemShack.Application.Entities;

public sealed class EntityRegistry
{
    private static readonly string[] PersonContextPatterns =
    [
        @"\b{name}\s+said\b",
        @"\b{name}\s+told\b",
        @"\b{name}\s+asked\b",
        @"\b{name}\s+laughed\b",
        @"\b{name}\s+smiled\b",
        @"\b{name}\s+was\b",
        @"\b{name}\s+is\b",
        @"\b{name}\s+called\b",
        @"\b{name}\s+texted\b",
        @"\bwith\s+{name}\b",
        @"\bsaw\s+{name}\b",
        @"\bcalled\s+{name}\b",
        @"\btook\s+{name}\b",
        @"\bpicked\s+up\s+{name}\b",
        @"\bdrop(?:ped)?\s+(?:off\s+)?{name}\b",
        @"\b{name}(?:'s|s')\b",
        @"\bhey\s+{name}\b",
        @"\bthanks?\s+{name}\b",
        @"^{name}[:\s]",
        @"\bmy\s+(?:son|daughter|kid|child|brother|sister|friend|partner|colleague|coworker)\s+{name}\b",
    ];

    private static readonly string[] ConceptContextPatterns =
    [
        @"\bhave\s+you\s+{name}\b",
        @"\bif\s+you\s+{name}\b",
        @"\b{name}\s+since\b",
        @"\b{name}\s+again\b",
        @"\bnot\s+{name}\b",
        @"\b{name}\s+more\b",
        @"\bwould\s+{name}\b",
        @"\bcould\s+{name}\b",
        @"\bwill\s+{name}\b",
        @"(?:the\s+)?{name}\s+(?:of|in|at|for|to)\b",
    ];

    private static readonly HashSet<string> CommonEnglishWords =
    [
        "ever", "grace", "will", "bill", "mark", "april", "may", "june", "joy", "hope", "faith", "chance",
        "chase", "hunter", "dash", "flash", "star", "sky", "river", "brook", "lane", "art", "clay", "gil",
        "nat", "max", "rex", "ray", "jay", "rose", "violet", "lily", "ivy", "ash", "reed", "sage",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "january", "february",
        "march", "july", "august", "september", "october", "november", "december",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly RegistryData _data;
    private readonly EntityDetector _detector;
    private readonly IWikipediaResearchClient _wikipediaResearchClient;

    private EntityRegistry(
        RegistryData data,
        string filePath,
        EntityDetector? detector = null,
        IWikipediaResearchClient? wikipediaResearchClient = null)
    {
        _data = data;
        FilePath = filePath;
        _detector = detector ?? new EntityDetector();
        _wikipediaResearchClient = wikipediaResearchClient ?? new WikipediaSummaryResearchClient();
    }

    public string FilePath { get; }

    public string Mode => _data.Mode ?? "personal";

    public IReadOnlyDictionary<string, RegistryPerson> People => _data.People ?? EmptyPeople;

    public IReadOnlyList<string> Projects => _data.Projects is { } projects ? projects : Array.Empty<string>();

    public IReadOnlyList<string> AmbiguousFlags => _data.AmbiguousFlags is { } ambiguousFlags ? ambiguousFlags : Array.Empty<string>();

    public bool WikipediaResearchSupported => _wikipediaResearchClient.IsSupported;

    private static IReadOnlyDictionary<string, RegistryPerson> EmptyPeople { get; } =
        new Dictionary<string, RegistryPerson>(StringComparer.Ordinal);

    public static EntityRegistry Load(
        string? configDirectory = null,
        EntityDetector? detector = null,
        IWikipediaResearchClient? wikipediaResearchClient = null)
    {
        var path = ResolveFilePath(configDirectory);
        if (File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<RegistryData>(File.ReadAllText(path)) ?? RegistryData.Empty();
                Normalize(data);
                return new EntityRegistry(data, path, detector, wikipediaResearchClient);
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        return new EntityRegistry(RegistryData.Empty(), path, detector, wikipediaResearchClient);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOptions));
    }

    public void Seed(
        string mode,
        IReadOnlyList<Onboarding.OnboardingPerson> people,
        IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        _data.Mode = mode;
        _data.Projects = projects.Distinct(StringComparer.Ordinal).ToList();
        _data.People ??= new Dictionary<string, RegistryPerson>(StringComparer.Ordinal);
        _data.AmbiguousFlags ??= [];
        _data.People.Clear();
        _data.AmbiguousFlags.Clear();

        aliases ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var reverseAliases = aliases.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var person in people)
        {
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            _data.People[person.Name] = new RegistryPerson
            {
                Source = "onboarding",
                Contexts = [person.Context],
                Aliases = reverseAliases.TryGetValue(person.Name, out var alias) ? [alias] : [],
                Relationship = person.Relationship,
                Confidence = 1.0,
            };

            if (reverseAliases.TryGetValue(person.Name, out alias))
            {
                _data.People[alias] = new RegistryPerson
                {
                    Source = "onboarding",
                    Contexts = [person.Context],
                    Aliases = [person.Name],
                    Relationship = person.Relationship,
                    Confidence = 1.0,
                    Canonical = person.Name,
                };
            }
        }

        foreach (var name in _data.People.Keys)
        {
            if (CommonEnglishWords.Contains(name.ToLowerInvariant()) && !_data.AmbiguousFlags.Contains(name.ToLowerInvariant(), StringComparer.Ordinal))
            {
                _data.AmbiguousFlags.Add(name.ToLowerInvariant());
            }
        }

        Save();
    }

    public EntityLookupResult Lookup(string word, string context = "")
    {
        foreach (var pair in People)
        {
            if (MatchesPerson(word, pair.Key, pair.Value))
            {
                if (AmbiguousFlags.Contains(word.ToLowerInvariant(), StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(context))
                {
                    var resolved = Disambiguate(word, context, pair.Value);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }

                return new EntityLookupResult(
                    "person",
                    pair.Value.Confidence,
                    pair.Value.Source,
                    pair.Key,
                    false,
                    pair.Value.Contexts);
            }
        }

        var project = Projects.FirstOrDefault(project => string.Equals(project, word, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return new EntityLookupResult("project", 1.0, "onboarding", project, false);
        }

        if (TryGetConfirmedWikiCacheEntry(word, out _, out var wikiEntry))
        {
            if (string.Equals(wikiEntry.EffectiveType, "person", StringComparison.OrdinalIgnoreCase) &&
                CommonEnglishWords.Contains(word.ToLowerInvariant()) &&
                !string.IsNullOrWhiteSpace(context))
            {
                var resolved = Disambiguate(
                    word,
                    context,
                    new RegistryPerson
                    {
                        Source = "wiki",
                        Contexts = [Mode == "combo" ? "personal" : Mode],
                        Confidence = wikiEntry.Confidence,
                    });

                if (resolved is not null)
                {
                    return resolved;
                }
            }

            return new EntityLookupResult(wikiEntry.EffectiveType, wikiEntry.Confidence, "wiki", word, false);
        }

        return new EntityLookupResult("unknown", 0.0, "none", word, false);
    }

    public IReadOnlyList<DetectedEntity> LearnFromText(string text, double minConfidence = 0.75)
    {
        var detected = _detector.DetectEntitiesFromText(text);
        var learned = new List<DetectedEntity>();

        foreach (var entity in detected.People)
        {
            if (entity.Confidence < minConfidence || People.ContainsKey(entity.Name) || Projects.Contains(entity.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            (_data.People ??= new Dictionary<string, RegistryPerson>(StringComparer.Ordinal))[entity.Name] = new RegistryPerson
            {
                Source = "learned",
                Contexts = [Mode == "combo" ? "personal" : Mode],
                Relationship = string.Empty,
                Confidence = entity.Confidence,
                SeenCount = entity.Frequency,
            };

            if (CommonEnglishWords.Contains(entity.Name.ToLowerInvariant()) && !AmbiguousFlags.Contains(entity.Name.ToLowerInvariant(), StringComparer.Ordinal))
            {
                (_data.AmbiguousFlags ??= []).Add(entity.Name.ToLowerInvariant());
            }

            learned.Add(entity);
        }

        if (learned.Count > 0)
        {
            Save();
        }

        return learned;
    }

    public IReadOnlyList<string> ExtractPeopleFromQuery(string query)
    {
        var found = new List<string>();

        foreach (var pair in People)
        {
            var names = new[] { pair.Key }.Concat(pair.Value.Aliases);
            foreach (var name in names)
            {
                if (!Regex.IsMatch(query, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                if (AmbiguousFlags.Contains(name.ToLowerInvariant(), StringComparer.Ordinal))
                {
                    var disambiguated = Disambiguate(name, query, pair.Value);
                    if (disambiguated?.Type != "person")
                    {
                        continue;
                    }
                }

                if (!found.Contains(pair.Key, StringComparer.Ordinal))
                {
                    found.Add(pair.Key);
                }
            }
        }

        return found;
    }

    public IReadOnlyList<string> ExtractUnknownCandidates(string query)
    {
        var candidates = Regex.Matches(query, @"\b[A-Z][a-z]{2,15}\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Where(word => !CommonEnglishWords.Contains(word.ToLowerInvariant()))
            .Where(word => Lookup(word).Type == "unknown")
            .ToArray();

        return candidates;
    }

    public WikipediaResearchResult Research(string word, bool autoConfirm = false)
    {
        word = word.Trim();
        if (string.IsNullOrWhiteSpace(word))
        {
            return WikipediaResearchResult.Unknown(string.Empty);
        }

        _data.WikiCache ??= new Dictionary<string, WikipediaResearchCacheEntry>(StringComparer.OrdinalIgnoreCase);

        if (TryGetWikiCacheEntry(word, out var cachedWord, out var cachedResult))
        {
            if (autoConfirm && !cachedResult.Confirmed)
            {
                cachedResult.Confirmed = true;
                cachedResult.ConfirmedType ??= cachedResult.InferredType;
                _data.WikiCache[cachedWord] = cachedResult;
                Save();
            }

            return cachedResult.ToResult(cachedWord);
        }

        var lookupResult = _wikipediaResearchClient.Lookup(word);
        var result = lookupResult with
        {
            Word = word,
            Confirmed = autoConfirm,
            ConfirmedType = autoConfirm ? lookupResult.InferredType : null,
        };

        _data.WikiCache[word] = WikipediaResearchCacheEntry.FromResult(result);
        Save();
        return result;
    }

    public WikipediaResearchResult ConfirmResearch(
        string word,
        string entityType,
        string relationship = "",
        string context = "personal")
    {
        word = word.Trim();
        entityType = entityType.Trim();

        _data.WikiCache ??= new Dictionary<string, WikipediaResearchCacheEntry>(StringComparer.OrdinalIgnoreCase);

        if (!TryGetWikiCacheEntry(word, out _, out var cacheEntry))
        {
            cacheEntry = new WikipediaResearchCacheEntry
            {
                InferredType = entityType,
                Confidence = string.Equals(entityType, "person", StringComparison.OrdinalIgnoreCase) ? 0.90 : 0.80,
            };
            _data.WikiCache[word] = cacheEntry;
        }

        cacheEntry.Confirmed = true;
        cacheEntry.ConfirmedType = entityType;

        if (string.Equals(entityType, "person", StringComparison.OrdinalIgnoreCase))
        {
            (_data.People ??= new Dictionary<string, RegistryPerson>(StringComparer.Ordinal))[word] = new RegistryPerson
            {
                Source = "wiki",
                Contexts = [string.IsNullOrWhiteSpace(context) ? "personal" : context],
                Aliases = [],
                Relationship = relationship,
                Confidence = Math.Max(0.90, cacheEntry.Confidence),
            };

            var loweredWord = word.ToLowerInvariant();
            if (CommonEnglishWords.Contains(loweredWord) &&
                !(_data.AmbiguousFlags ??= []).Contains(loweredWord, StringComparer.Ordinal))
            {
                _data.AmbiguousFlags.Add(loweredWord);
            }
        }

        Save();
        return cacheEntry.ToResult(word);
    }

    public string Summary()
    {
        var peoplePreview = string.Join(", ", People.Keys.Take(8));
        return string.Join('\n',
        [
            $"Mode: {Mode}",
            $"People: {People.Count} ({(peoplePreview.Length > 0 ? peoplePreview : "(none)")}{(People.Count > 8 ? "..." : string.Empty)})",
            $"Projects: {(Projects.Count > 0 ? string.Join(", ", Projects) : "(none)")}",
            $"Ambiguous flags: {(AmbiguousFlags.Count > 0 ? string.Join(", ", AmbiguousFlags) : "(none)")}",
            $"Wikipedia research: {(WikipediaResearchSupported ? "enabled (programmatic)" : "disabled")}",
            $"Wiki cache: {_data.WikiCache?.Count ?? 0} entries",
        ]);
    }

    internal static string ResolveFilePath(string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            return Path.Combine(Path.GetFullPath(PathUtilities.ExpandHome(configDirectory)), ConfigFileNames.EntityRegistryJson);
        }

        return Path.Combine(
            MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
            ConfigFileNames.EntityRegistryJson);
    }

    private static void Normalize(RegistryData data)
    {
        data.People ??= new Dictionary<string, RegistryPerson>(StringComparer.Ordinal);
        data.Projects ??= [];
        data.AmbiguousFlags ??= [];
        data.WikiCache = data.WikiCache is null
            ? new Dictionary<string, WikipediaResearchCacheEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, WikipediaResearchCacheEntry>(data.WikiCache, StringComparer.OrdinalIgnoreCase);
        data.Mode ??= "personal";
        data.Version = data.Version == 0 ? 1 : data.Version;

        foreach (var person in data.People.Values)
        {
            person.Contexts ??= [];
            person.Aliases ??= [];
            person.Source ??= "onboarding";
        }
    }

    private static bool MatchesPerson(string word, string canonical, RegistryPerson person)
    {
        return string.Equals(word, canonical, StringComparison.OrdinalIgnoreCase) ||
               person.Aliases.Any(alias => string.Equals(word, alias, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetConfirmedWikiCacheEntry(
        string word,
        out string cachedWord,
        out WikipediaResearchCacheEntry cacheEntry)
    {
        if (TryGetWikiCacheEntry(word, out cachedWord, out cacheEntry) && cacheEntry.Confirmed)
        {
            return true;
        }

        cachedWord = string.Empty;
        cacheEntry = WikipediaResearchCacheEntry.Empty;
        return false;
    }

    private bool TryGetWikiCacheEntry(
        string word,
        out string cachedWord,
        out WikipediaResearchCacheEntry cacheEntry)
    {
        if (_data.WikiCache is not null)
        {
            foreach (var pair in _data.WikiCache)
            {
                if (string.Equals(pair.Key, word, StringComparison.OrdinalIgnoreCase))
                {
                    cachedWord = pair.Key;
                    cacheEntry = pair.Value;
                    return true;
                }
            }
        }

        cachedWord = string.Empty;
        cacheEntry = WikipediaResearchCacheEntry.Empty;
        return false;
    }

    private static EntityLookupResult? Disambiguate(string word, string context, RegistryPerson person)
    {
        var escaped = Regex.Escape(word.ToLowerInvariant());
        var lowered = context.ToLowerInvariant();

        var personScore = PersonContextPatterns.Count(pattern => Regex.IsMatch(lowered, pattern.Replace("{name}", escaped), RegexOptions.IgnoreCase | RegexOptions.Multiline));
        var conceptScore = ConceptContextPatterns.Count(pattern => Regex.IsMatch(lowered, pattern.Replace("{name}", escaped), RegexOptions.IgnoreCase | RegexOptions.Multiline));

        if (personScore > conceptScore)
        {
            return new EntityLookupResult(
                "person",
                Math.Min(0.95, 0.7 + personScore * 0.1),
                person.Source,
                word,
                false,
                person.Contexts,
                "context_patterns");
        }

        if (conceptScore > personScore)
        {
            return new EntityLookupResult(
                "concept",
                Math.Min(0.90, 0.7 + conceptScore * 0.1),
                "context_disambiguated",
                word,
                false,
                DisambiguatedBy: "context_patterns");
        }

        return null;
    }

    private sealed class RegistryData
    {
        public int Version { get; set; } = 1;

        public string? Mode { get; set; } = "personal";

        public Dictionary<string, RegistryPerson>? People { get; set; } = new(StringComparer.Ordinal);

        public List<string>? Projects { get; set; } = [];

        public List<string>? AmbiguousFlags { get; set; } = [];

        public Dictionary<string, WikipediaResearchCacheEntry>? WikiCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static RegistryData Empty() => new();
    }
}

public sealed class RegistryPerson
{
    public string Source { get; set; } = "onboarding";

    public List<string> Contexts { get; set; } = [];

    public List<string> Aliases { get; set; } = [];

    public string Relationship { get; set; } = string.Empty;

    public double Confidence { get; set; } = 1.0;

    public string? Canonical { get; set; }

    public int? SeenCount { get; set; }
}

public sealed class WikipediaResearchCacheEntry
{
    public static WikipediaResearchCacheEntry Empty { get; } = new();

    [JsonPropertyName("inferred_type")]
    public string InferredType { get; set; } = "unknown";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("wiki_summary")]
    public string? WikiSummary { get; set; }

    [JsonPropertyName("wiki_title")]
    public string? WikiTitle { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; set; }

    [JsonPropertyName("confirmed_type")]
    public string? ConfirmedType { get; set; }

    public string EffectiveType => string.IsNullOrWhiteSpace(ConfirmedType) ? InferredType : ConfirmedType;

    public WikipediaResearchResult ToResult(string word) =>
        new(
            word,
            InferredType,
            Confidence,
            WikiSummary,
            WikiTitle,
            Note,
            Confirmed,
            ConfirmedType);

    public static WikipediaResearchCacheEntry FromResult(WikipediaResearchResult result) =>
        new()
        {
            InferredType = result.InferredType,
            Confidence = result.Confidence,
            WikiSummary = result.WikiSummary,
            WikiTitle = result.WikiTitle,
            Note = result.Note,
            Confirmed = result.Confirmed,
            ConfirmedType = result.ConfirmedType,
        };
}
