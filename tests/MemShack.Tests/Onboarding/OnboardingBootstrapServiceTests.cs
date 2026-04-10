using MemShack.Application.Onboarding;
using MemShack.Core.Constants;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Onboarding;

[TestClass]
public sealed class OnboardingBootstrapServiceTests
{
    [TestMethod]
    public void QuickSetup_WritesRegistryAndBootstrapFiles()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var service = new OnboardingBootstrapService();

        var registry = service.QuickSetup(
            new OnboardingSetup(
                "combo",
                [new OnboardingPerson("Riley", "daughter", "personal")],
                ["MemShack"],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Ry"] = "Riley" },
                ["family", "projects"]),
            configDirectory);

        var registryPath = Path.Combine(configDirectory, ConfigFileNames.EntityRegistryJson);
        var aaakPath = Path.Combine(configDirectory, ConfigFileNames.AaakEntitiesMarkdown);
        var factsPath = Path.Combine(configDirectory, ConfigFileNames.CriticalFactsMarkdown);

        Assert.Equal("combo", registry.Mode);
        Assert.True(File.Exists(registryPath));
        Assert.True(File.Exists(aaakPath));
        Assert.True(File.Exists(factsPath));
        Assert.Contains("Riley", File.ReadAllText(aaakPath));
        Assert.Contains("Wings: family, projects", File.ReadAllText(factsPath));
    }

    [TestMethod]
    public void QuickSetup_GeneratesDistinctEntityCodesWhenPrefixesCollide()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var service = new OnboardingBootstrapService();

        service.QuickSetup(
            new OnboardingSetup(
                "combo",
                [new OnboardingPerson("Mara", "friend", "personal"), new OnboardingPerson("Mark", "coworker", "work")],
                ["MarketMap"],
                new Dictionary<string, string>(StringComparer.Ordinal),
                ["family", "projects"]),
            configDirectory);

        var aaak = File.ReadAllText(Path.Combine(configDirectory, ConfigFileNames.AaakEntitiesMarkdown));

        Assert.Contains("MAR=Mara", aaak);
        Assert.Contains("MARK=Mark", aaak);
        Assert.Contains("MARKE=MarketMap", aaak);
    }

    [TestMethod]
    public void QuickSetup_PersistsAliasesAndGroupsPeopleByContext()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var service = new OnboardingBootstrapService();

        var registry = service.QuickSetup(
            new OnboardingSetup(
                "combo",
                [new OnboardingPerson("Riley", "daughter", "personal"), new OnboardingPerson("Casey", "tech lead", "work")],
                ["MemShack"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["chief"] = "Riley",
                },
                ["family", "projects"]),
            configDirectory);

        var facts = File.ReadAllText(Path.Combine(configDirectory, ConfigFileNames.CriticalFactsMarkdown));

        Assert.True(registry.People.ContainsKey("Riley"));
        Assert.True(registry.People.ContainsKey("chief"));
        Assert.Equal("Riley", registry.People["chief"].Canonical);
        Assert.Contains("## People (personal)", facts);
        Assert.Contains("## People (work)", facts);
        Assert.Contains("**Riley**", facts);
        Assert.Contains("**Casey**", facts);
    }
}
