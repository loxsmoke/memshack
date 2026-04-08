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
}
