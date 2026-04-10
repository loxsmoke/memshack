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

    [TestMethod]
    public void ExtractMemories_FindsEmotionalMemory()
    {
        var text = "I feel proud and grateful that we finally got this migration over the line after such a hard week.";

        var memories = _extractor.ExtractMemories(text);

        var memory = Assert.Single(memories);
        Assert.Equal("emotional", memory.MemoryType);
    }

    [TestMethod]
    public void ExtractMemories_UnresolvedProblemStaysProblem()
    {
        var text = "The auth refresh bug keeps failing in production and the login flow still breaks for some users.";

        var memories = _extractor.ExtractMemories(text);

        var memory = Assert.Single(memories);
        Assert.Equal("problem", memory.MemoryType);
    }

    [TestMethod]
    public void ExtractMemories_SplitsSpeakerTurnsIntoSeparateMemories()
    {
        var text = """
            > should we switch the project to SQLite?
            We decided to go with SQLite because it reduces moving parts for the first migration.

            Assistant: Sounds good.

            > why is the deploy still red?
            The auth refresh bug keeps failing in production and still needs a fix.
            """;

        var memories = _extractor.ExtractMemories(text);

        Assert.Equal(2, memories.Count);
        Assert.Equal("decision", memories[0].MemoryType);
        Assert.Equal("problem", memories[1].MemoryType);
    }
}
