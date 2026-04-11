using MemShack.Application.Hooks;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Hooks;

[TestClass]
public sealed class HookTranscriptCounterTests
{
    [TestMethod]
    public void CountHumanMessages_CountsClaudeAndCodexUserMessages()
    {
        using var temp = new TemporaryDirectory();
        var transcriptPath = temp.WriteFile(
            "transcript.jsonl",
            string.Join(
                Environment.NewLine,
                [
                    """{"message":{"role":"user","content":"hello there"}}""",
                    """{"message":{"role":"assistant","content":"hi"}}""",
                    """{"message":{"role":"user","content":"<command-message>skip</command-message>"}}""",
                    """{"type":"event_msg","payload":{"type":"user_message","message":"codex hello"}}""",
                    """{"type":"event_msg","payload":{"type":"user_message","message":"<command-message>skip</command-message>"}}""",
                ]));

        var count = HookTranscriptCounter.CountHumanMessages(transcriptPath);

        Assert.Equal(2, count);
    }

    [TestMethod]
    public void SaveHook_UsesCliHumanMessageCounter()
    {
        var hookPath = Path.Combine(FixturePaths.RepoRootPath, "hooks", "memshack_save_hook.sh");
        var script = File.ReadAllText(hookPath);

        Assert.Contains("__count-human-messages", script);
        Assert.DoesNotContain("PYEOF", script);
    }
}
