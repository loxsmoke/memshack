using MemShack.Application.Search;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Search;

[TestClass]
public sealed class MemorySearchServiceTests
{
    [TestMethod]
    public async Task SearchMemories_ReturnsProgrammaticResultShape()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var service = new MemorySearchService(store, temp.GetPath("palace"));

        var result = await service.SearchMemoriesAsync("JWT authentication");

        Assert.Equal("JWT authentication", result.Query);
        Assert.NotEmpty(result.Results);
        Assert.Null(result.Error);
    }

    [TestMethod]
    public async Task SearchMemories_PreservesWingAndRoomFilteringSemantics()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var service = new MemorySearchService(store, temp.GetPath("palace"));

        var wingOnly = await service.SearchMemoriesAsync("planning", wing: "notes");
        var roomOnly = await service.SearchMemoriesAsync("database", room: "backend");
        var both = await service.SearchMemoriesAsync("components", wing: "project", room: "frontend");

        Assert.All(wingOnly.Results, hit => Assert.Equal("notes", hit.Wing));
        Assert.All(roomOnly.Results, hit => Assert.Equal("backend", hit.Room));
        Assert.All(both.Results, hit => Assert.True(hit.Wing == "project" && hit.Room == "frontend"));
    }

    [TestMethod]
    public async Task SearchMemories_ReturnsBasenameAndRoundedSimilarity()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var service = new MemorySearchService(store, temp.GetPath("palace"));

        var result = await service.SearchMemoriesAsync("authentication");
        var hit = Assert.Single(result.Results.Take(1));

        Assert.Equal("auth.py", hit.SourceFile);
        Assert.Equal(Math.Round(hit.Similarity, 3), hit.Similarity);
    }

    [TestMethod]
    public async Task SearchMemories_ReturnsErrorWhenPalaceMissing()
    {
        using var temp = new TemporaryDirectory();
        var store = new MemShack.Infrastructure.VectorStore.Collections.ChromaCompatibilityVectorStore(temp.GetPath("missing-palace"));
        var service = new MemorySearchService(store, temp.GetPath("missing-palace"));

        var result = await service.SearchMemoriesAsync("anything");

        Assert.NotNull(result.Error);
        Assert.Contains("No palace found", result.Error!);
    }

    [TestMethod]
    public async Task FormatSearch_ProducesCliStyleOutput()
    {
        using var temp = new TemporaryDirectory();
        var store = await SeededPalaceFactory.CreateAsync(temp);
        var service = new MemorySearchService(store, temp.GetPath("palace"));

        var output = await service.FormatSearchAsync("authentication", wing: "project", room: "backend");

        Assert.Contains("Results for: \"authentication\"", output);
        Assert.Contains("Wing: project", output);
        Assert.Contains("Room: backend", output);
        Assert.Contains("Source: auth.py", output);
        Assert.Contains("Match:", output);
    }
}
