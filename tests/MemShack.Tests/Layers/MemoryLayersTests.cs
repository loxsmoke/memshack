using MemShack.Application.Layers;
using MemShack.Application.Search;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Layers;

[TestClass]
public sealed class MemoryLayersTests
{
    [TestMethod]
    public void Layer0_LoadsIdentityTextOrDefault()
    {
        using var temp = new TemporaryDirectory();
        var identityPath = temp.WriteFile("identity.txt", "I am Atlas, a direct and warm assistant.");

        var configured = new Layer0(identityPath);
        var missing = new Layer0(temp.GetPath("missing-identity.txt"));

        Assert.Contains("Atlas", configured.Render());
        Assert.True(configured.TokenEstimate() > 0);
        Assert.Contains("No identity configured", missing.Render());
    }

    [TestMethod]
    public async Task Layer1_GeneratesEssentialStoryFromTopWeightedDrawers()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        await store.AddDrawerAsync(
            CollectionNames.Drawers,
            new DrawerRecord(
                "drawer_project_backend_hero",
                "critical backend hero snippet",
                new DrawerMetadata
                {
                    Wing = "project",
                    Room = "backend",
                    SourceFile = temp.GetPath("src", "hero.txt"),
                    ChunkIndex = 99,
                    AddedBy = "seed",
                    FiledAt = "2026-04-07T10:00:00",
                    Importance = 100,
                }));
        for (var index = 0; index < 20; index++)
        {
            await store.AddDrawerAsync(
                CollectionNames.Drawers,
                new DrawerRecord(
                    $"drawer_extra_{index}",
                    $"extra snippet {index}",
                    new DrawerMetadata
                    {
                        Wing = "project",
                        Room = "misc",
                        SourceFile = temp.GetPath("src", $"extra-{index}.txt"),
                        ChunkIndex = index,
                        AddedBy = "seed",
                        FiledAt = "2026-04-07T10:00:00",
                        Importance = index,
                    }));
        }

        var layer = new Layer1(store);
        var output = await layer.GenerateAsync();

        Assert.Contains("## L1 - ESSENTIAL STORY", output);
        Assert.Contains("[backend]", output);
        Assert.Contains("hero.txt", output);
        Assert.DoesNotContain("extra snippet 0", output);
    }

    [TestMethod]
    public async Task Layer2_RetrievesFilteredDrawerText()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var layer = new Layer2(store);

        var output = await layer.RetrieveAsync(wing: "project", room: "backend");
        var none = await layer.RetrieveAsync(wing: "missing");

        Assert.Contains("## L2 - ON-DEMAND", output);
        Assert.Contains("[backend]", output);
        Assert.Contains("auth.py", output);
        Assert.Contains("No drawers found", none);
    }

    [TestMethod]
    public async Task Layer3_ProvidesFormattedAndRawSearch()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var service = new MemorySearchService(store, temp.GetPath("palace"));
        var layer = new Layer3(service);

        var output = await layer.SearchAsync("authentication", wing: "project");
        var raw = await layer.SearchRawAsync("planning", wing: "notes");

        Assert.Contains("## L3 - SEARCH RESULTS", output);
        Assert.Contains("project/backend", output);
        Assert.Contains("src: auth.py", output);
        Assert.All(raw, hit => Assert.Equal("notes", hit.Wing));
    }

    [TestMethod]
    public async Task MemoryStack_UnifiesWakeUpRecallSearchAndStatus()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var identityPath = temp.WriteFile("identity.txt", "I am Atlas.");
        var stack = new MemoryStack(store, temp.GetPath("palace"), identityPath);

        var wakeUp = await stack.WakeUpAsync(wing: "project");
        var recall = await stack.RecallAsync(wing: "notes");
        var search = await stack.SearchAsync("database");
        var status = await stack.StatusAsync();

        Assert.Contains("I am Atlas.", wakeUp);
        Assert.Contains("## L1 - ESSENTIAL STORY", wakeUp);
        Assert.Contains("## L2 - ON-DEMAND", recall);
        Assert.Contains("## L3 - SEARCH RESULTS", search);
        Assert.Equal(temp.GetPath("palace"), status.PalacePath);
        Assert.True(status.L0Identity.Exists);
        Assert.True(status.TotalDrawers > 0);
    }

    [TestMethod]
    public async Task EmptyPalacePaths_ReportMissingStateClearly()
    {
        using var temp = new TemporaryDirectory();
        var store = new ChromaCompatibilityVectorStore(temp.GetPath("palace"));
        var stack = new MemoryStack(store, temp.GetPath("palace"), temp.GetPath("missing-identity.txt"));

        var wakeUp = await stack.WakeUpAsync();
        var recall = await stack.RecallAsync();
        var raw = await stack.L3.SearchRawAsync("anything");

        Assert.Contains("No identity configured", wakeUp);
        Assert.Contains("## L1 - No palace found. Run: mempalace mine <dir>", wakeUp);
        Assert.Equal("No palace found.", recall);
        Assert.Empty(raw);
    }
}
