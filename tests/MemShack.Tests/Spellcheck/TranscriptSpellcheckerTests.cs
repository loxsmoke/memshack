using MemShack.Application.Spellcheck;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Spellcheck;

[TestClass]
public sealed class TranscriptSpellcheckerTests
{
    [TestMethod]
    public void SpellcheckUserText_CorrectsTyposAndPreservesKnownNames()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "known_names.json"), """["riley"]""");
        var spellchecker = new TranscriptSpellchecker(configDirectory);

        var result = spellchecker.SpellcheckUserText("lsresdy knoe Riley befor");

        Assert.Equal("already know Riley before", result);
    }

    [TestMethod]
    public void SpellcheckUserText_SkipsTechnicalTokensUrlsAndAllCaps()
    {
        var spellchecker = new TranscriptSpellchecker();

        var result = spellchecker.SpellcheckUserText(
            "check memshack_save_hook.sh and https://example.com with API_TOKEN while coherentlyy");

        Assert.Contains("memshack_save_hook.sh", result);
        Assert.Contains("https://example.com", result);
        Assert.Contains("API_TOKEN", result);
        Assert.Contains("coherently", result);
    }

    [TestMethod]
    public void SpellcheckTranscriptLine_OnlySpellchecksQuotedTurns()
    {
        var spellchecker = new TranscriptSpellchecker();

        var quoted = spellchecker.SpellcheckTranscriptLine("> pleese chekc this befor");
        var plain = spellchecker.SpellcheckTranscriptLine("pleese chekc this befor");

        Assert.Equal("> please check this before", quoted);
        Assert.Equal("pleese chekc this befor", plain);
    }

    [TestMethod]
    public void SpellcheckTranscript_SpellchecksOnlyUserMessages()
    {
        var spellchecker = new TranscriptSpellchecker();
        var transcript = """
            > lsresdy knoe the answer
            pleese chekc the docs first

            > befor we ship
            Sure.
            """;

        var result = spellchecker.SpellcheckTranscript(transcript);

        Assert.Contains("> already know the answer", result);
        Assert.Contains("pleese chekc the docs first", result);
        Assert.Contains("> before we ship", result);
    }
}
