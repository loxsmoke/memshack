using MemShack.Application.Extraction;

namespace MemShack.Tests.Extraction;

[TestClass]
public sealed class GeneralMemoryExtractorTests
{
    private readonly GeneralMemoryExtractor _extractor = new();

    [TestMethod]
    public void ExtractMemories_FindsDecisionMemory()
    {
        var text = "We decided to go with SQLite because it keeps the first migration simple and reduces moving parts.";

        var memories = _extractor.ExtractMemories(text);

        var memory = Assert.Single(memories);
        Assert.Equal("decision", memory.MemoryType);
    }

    [TestMethod]
    public void ExtractMemories_ResolvedProblemBecomesMilestone()
    {
        var text = "The bug kept failing in production, but we figured it out and fixed the config path issue. It works now.";

        var memories = _extractor.ExtractMemories(text);

        var memory = Assert.Single(memories);
        Assert.Equal("milestone", memory.MemoryType);
    }

    [TestMethod]
    public void ExtractMemories_IgnoresCodeOnlyNoiseWhenScoring()
    {
        var text = """
            ```python
            def broken():
                return False
            ```

            I prefer keeping the migration adapters thin and boring so the compatibility layer stays easy to reason about.
            Please always use the compatibility adapter first instead of rewriting storage behavior during the same phase.
            """;

        var memories = _extractor.ExtractMemories(text);

        var memory = Assert.Single(memories);
        Assert.Equal("preference", memory.MemoryType);
    }
}
