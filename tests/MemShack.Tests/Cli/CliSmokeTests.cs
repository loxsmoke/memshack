using MemShack.Cli;
using MemShack.Core.Constants;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Cli;

[TestClass]
public sealed class CliSmokeTests
{
    [TestMethod]
    public async Task NoArgs_PrintsHelpAndCoreCommands()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync([], stdout, stderr);

        Assert.Equal(0, exitCode);
        AssertBanner(stdout.ToString());
        Assert.Contains("C# port of MemPalace - The highest-scoring AI memory system ever benchmarked", stdout.ToString());
        Assert.Contains("mems init <dir>", stdout.ToString());
        Assert.Contains("mems mine <dir>", stdout.ToString());
        Assert.Contains("mems wake-up", stdout.ToString());
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("help")]
    public async Task ExplicitHelp_PrintsHelpAndReturnsSuccess(string helpArg)
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync([helpArg], stdout, stderr);

        Assert.Equal(0, exitCode);
        AssertBanner(stdout.ToString());
        Assert.Contains("C# port of MemPalace - The highest-scoring AI memory system ever benchmarked", stdout.ToString());
        Assert.Contains("mems init <dir>", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [TestMethod]
    public async Task Init_CreatesProjectAndGlobalConfig()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(Path.Combine(projectRoot, "backend", "app.py"), "print('hello')\n" + new string('a', 80));

        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["init", projectRoot, "--yes"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(projectRoot, ConfigFileNames.MempalaceYaml)));
        Assert.True(File.Exists(Path.Combine(configDirectory, ConfigFileNames.ConfigJson)));
        Assert.Contains("Project config saved", stdout.ToString());
    }

    [TestMethod]
    public async Task ProjectFlow_MineSearchWakeUpStatusAndRepair_Work()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.IdentityText), "I am Atlas, a helpful assistant.");
        File.WriteAllText(Path.Combine(projectRoot, ConfigFileNames.MempalaceYaml), """
            wing: project
            rooms:
              - name: backend
                description: Backend code
              - name: general
                description: General
            """);
        File.WriteAllText(
            Path.Combine(projectRoot, "backend", "auth.py"),
            string.Join('\n', Enumerable.Repeat("JWT authentication tokens protect the backend API.", 30)));

        var app = new CliApp(configDirectory: configDirectory);

        var mineOut = new StringWriter();
        var searchOut = new StringWriter();
        var wakeOut = new StringWriter();
        var statusOut = new StringWriter();
        var repairOut = new StringWriter();
        var stderr = new StringWriter();

        var mineCode = await app.RunAsync(["--palace", palacePath, "mine", projectRoot], mineOut, stderr);
        var searchCode = await app.RunAsync(["--palace", palacePath, "search", "JWT authentication"], searchOut, stderr);
        var wakeCode = await app.RunAsync(["--palace", palacePath, "wake-up"], wakeOut, stderr);
        var statusCode = await app.RunAsync(["--palace", palacePath, "status"], statusOut, stderr);
        var repairCode = await app.RunAsync(["--palace", palacePath, "repair"], repairOut, stderr);

        Assert.Equal(0, mineCode);
        Assert.Equal(0, searchCode);
        Assert.Equal(0, wakeCode);
        Assert.Equal(0, statusCode);
        Assert.Equal(0, repairCode);
        Assert.Contains("Drawers filed", mineOut.ToString());
        Assert.Contains("Results for: \"JWT authentication\"", searchOut.ToString());
        Assert.Contains("Wake-up text", wakeOut.ToString());
        Assert.Contains("I am Atlas", wakeOut.ToString());
        Assert.Contains("WING: project", statusOut.ToString());
        Assert.Contains("Repair complete", repairOut.ToString());
        Assert.True(Directory.Exists($"{palacePath}.backup"));
    }

    [TestMethod]
    public async Task ConvoMineSplitAndCompress_Work()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        var convoRoot = temp.GetPath("convos");
        var splitRoot = temp.GetPath("split");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(convoRoot);
        Directory.CreateDirectory(splitRoot);
        File.WriteAllText(Path.Combine(convoRoot, "chat.txt"), """
            > What changed?
            We switched the backend auth flow.

            > Why?
            It fixed the broken token refresh problem.

            > What next?
            Write tests.
            """);
        File.WriteAllText(
            Path.Combine(splitRoot, "mega.txt"),
            string.Join(
                '\n',
                [
                    "Claude Code v1",
                    "⏺ 9:41 PM Tuesday, April 01, 2026",
                    "> plan migration tasks",
                    "Sure.",
                    "line 5",
                    "line 6",
                    "line 7",
                    "line 8",
                    "line 9",
                    "line 10",
                    "Claude Code v1",
                    "⏺ 10:05 PM Tuesday, April 01, 2026",
                    "> review compression work",
                    "Okay.",
                    "line 15",
                    "line 16",
                    "line 17",
                    "line 18",
                    "line 19",
                    "line 20",
                ]));

        var app = new CliApp(configDirectory: configDirectory);
        var mineOut = new StringWriter();
        var splitOut = new StringWriter();
        var compressDryRunOut = new StringWriter();
        var compressOut = new StringWriter();
        var stderr = new StringWriter();

        var mineCode = await app.RunAsync(["--palace", palacePath, "mine", convoRoot, "--mode", "convos", "--wing", "chat_notes"], mineOut, stderr);
        var splitCode = await app.RunAsync(["split", splitRoot, "--dry-run"], splitOut, stderr);
        var compressDryRunCode = await app.RunAsync(["--palace", palacePath, "compress", "--dry-run"], compressDryRunOut, stderr);
        var compressCode = await app.RunAsync(["--palace", palacePath, "compress"], compressOut, stderr);

        Assert.Equal(0, mineCode);
        Assert.Equal(0, splitCode);
        Assert.Equal(0, compressDryRunCode);
        Assert.Equal(0, compressCode);
        Assert.Contains("Mode: convos", mineOut.ToString());
        Assert.Contains("would create 2 files", splitOut.ToString());
        Assert.Contains("dry run", compressDryRunOut.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stored", compressOut.ToString());
        Assert.True(File.Exists(Path.Combine(palacePath, "collections", $"{CollectionNames.Compressed}.json")));
    }

    private static void AssertBanner(string output)
    {
        var firstLine = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

        Assert.True(firstLine.StartsWith("MemShack v", StringComparison.Ordinal));
        Assert.True(firstLine.EndsWith("- Give your AI a memory. No API key required.", StringComparison.Ordinal));
    }
}
