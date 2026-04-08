using MemShack.Application.Normalization;
using MemShack.Application.Spellcheck;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Normalization;

[TestClass]
public sealed class TranscriptNormalizerTests
{
    private readonly TranscriptNormalizer _normalizer = new();

    [TestMethod]
    public void NormalizeContent_PassesThroughPlainText()
    {
        var content = "Hello world\nSecond line\n";

        var result = _normalizer.NormalizeContent(content, ".txt");

        Assert.Equal(content, result);
    }

    [TestMethod]
    public void NormalizeContent_ParsesClaudeJson()
    {
        var content = """
            [
              { "role": "user", "content": "Hi" },
              { "role": "assistant", "content": "Hello" }
            ]
            """;

        var result = _normalizer.NormalizeContent(content, ".json");

        Assert.Contains("> Hi", result);
        Assert.Contains("Hello", result);
    }

    [TestMethod]
    public void NormalizeContent_ParsesClaudeCodeJsonl()
    {
        var content = """
            {"type":"human","message":{"content":"What changed?"}}
            {"type":"assistant","message":{"content":"The config loader now prefers people_map.json."}}
            """;

        var result = _normalizer.NormalizeContent(content, ".jsonl");

        Assert.Contains("> What changed?", result);
        Assert.Contains("people_map.json", result);
    }

    [TestMethod]
    public void NormalizeContent_ParsesCodexJsonl()
    {
        var content = """
            {"type":"session_meta","cwd":"C:\\dev\\mempalace"}
            {"type":"event_msg","payload":{"type":"user_message","message":"Can you add tests?"}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"Yes, I added deterministic tests under the root tests folder."}}
            """;

        var result = _normalizer.NormalizeContent(content, ".jsonl");

        Assert.Contains("> Can you add tests?", result);
        Assert.Contains("deterministic tests", result);
    }

    [TestMethod]
    public void NormalizeContent_SpellchecksJsonUserTurns()
    {
        var content = """
            [
              { "role": "user", "content": "lsresdy knoe the answer befor" },
              { "role": "assistant", "content": "I can help." }
            ]
            """;

        var result = _normalizer.NormalizeContent(content, ".json");

        Assert.Contains("> already know the answer before", result);
    }

    [TestMethod]
    public void NormalizeContent_SpellchecksExistingTranscriptLines()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "known_names.json"), """["riley"]""");

        var normalizer = new TranscriptNormalizer(new TranscriptSpellchecker(configDirectory));
        var content = """
            > lsresdy knoe Riley
            Okay.

            > befor we start
            Sure.

            > pleese continue
            Continuing.
            """;

        var result = normalizer.NormalizeContent(content, ".txt");

        Assert.Contains("> already know Riley", result);
        Assert.Contains("> before we start", result);
        Assert.Contains("> please continue", result);
    }
}
