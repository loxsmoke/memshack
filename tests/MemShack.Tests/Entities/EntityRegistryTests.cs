using MemShack.Application.Entities;
using MemShack.Application.Onboarding;
using MemShack.Core.Constants;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Entities;

[TestClass]
public sealed class EntityRegistryTests
{
    [TestMethod]
    public void SeedAndLookup_PreserveAliasesAndProjects()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var registry = EntityRegistry.Load(configDirectory);

        registry.Seed(
            "combo",
            [new OnboardingPerson("Maxwell", "friend", "personal")],
            ["MemShack"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Max"] = "Maxwell" });

        var aliasLookup = registry.Lookup("Max");
        var projectLookup = registry.Lookup("MemShack");

        Assert.Equal("person", aliasLookup.Type);
        Assert.Equal("Maxwell", aliasLookup.Name);
        Assert.Equal("project", projectLookup.Type);
        Assert.True(File.Exists(Path.Combine(configDirectory, ConfigFileNames.EntityRegistryJson)));
    }

    [TestMethod]
    public void LearnFromText_PersistsHighConfidencePeople()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var registry = EntityRegistry.Load(configDirectory);

        var learned = registry.LearnFromText("""
            > Riley: hello
            Riley said hello.
            Riley told me she was ready.
            Thanks Riley, she solved it.
            Riley laughed because she finished the work.
            """);

        var reloaded = EntityRegistry.Load(configDirectory);

        Assert.Contains(learned, entity => entity.Name == "Riley" && entity.Type == "person");
        Assert.Equal("person", reloaded.Lookup("Riley").Type);
    }

    [TestMethod]
    public void Research_UsesWikipediaClientAndCachesResult()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var client = new StubWikipediaResearchClient(new WikipediaResearchResult(
            "Orchid",
            "concept",
            0.60,
            "orchid is a flowering plant",
            "Orchid"));

        var firstRegistry = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        var first = firstRegistry.Research("Orchid");

        var secondRegistry = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        var second = secondRegistry.Research("Orchid");

        Assert.Equal("concept", first.InferredType);
        Assert.Equal("Orchid", first.WikiTitle);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("concept", second.InferredType);
        Assert.False(second.Confirmed);
    }

    [TestMethod]
    public void Research_AutoConfirm_MakesConfirmedCacheVisibleToLookup()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var client = new StubWikipediaResearchClient(new WikipediaResearchResult(
            "Orchid",
            "concept",
            0.60,
            "orchid is a flowering plant",
            "Orchid"));

        var registry = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        var researched = registry.Research("Orchid", autoConfirm: true);
        var lookup = registry.Lookup("Orchid");

        Assert.True(researched.Confirmed);
        Assert.Equal("concept", lookup.Type);
        Assert.Equal("wiki", lookup.Source);
    }

    [TestMethod]
    public void ConfirmResearch_PromotesPersonIntoRegistry_AndFlagsAmbiguousWords()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var client = new StubWikipediaResearchClient(new WikipediaResearchResult(
            "Grace",
            "person",
            0.80,
            "grace is a feminine given name",
            "Grace"));

        var registry = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        registry.Research("Grace");
        var confirmed = registry.ConfirmResearch("Grace", "person", relationship: "friend");
        var reloaded = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        var lookup = reloaded.Lookup("Grace", "I went with Grace today");

        Assert.True(confirmed.Confirmed);
        Assert.Equal("person", lookup.Type);
        Assert.Equal("wiki", lookup.Source);
        Assert.Contains("grace", reloaded.AmbiguousFlags);
    }

    [TestMethod]
    public void Lookup_DisambiguatesConfirmedWikiCache_ForCommonEnglishWords()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var client = new StubWikipediaResearchClient(new WikipediaResearchResult(
            "Grace",
            "person",
            0.80,
            "grace is a feminine given name",
            "Grace"));

        var registry = EntityRegistry.Load(configDirectory, wikipediaResearchClient: client);
        registry.Research("Grace", autoConfirm: true);

        var personLookup = registry.Lookup("Grace", "I went with Grace today");
        var conceptLookup = registry.Lookup("Grace", "the grace of patience matters");

        Assert.Equal("person", personLookup.Type);
        Assert.Equal("concept", conceptLookup.Type);
        Assert.Equal("context_disambiguated", conceptLookup.Source);
    }

    private sealed class StubWikipediaResearchClient : IWikipediaResearchClient
    {
        private readonly WikipediaResearchResult _result;

        public StubWikipediaResearchClient(WikipediaResearchResult result)
        {
            _result = result;
        }

        public bool IsSupported => true;

        public int CallCount { get; private set; }

        public WikipediaResearchResult Lookup(string word)
        {
            CallCount++;
            return _result with { Word = word };
        }
    }
}
