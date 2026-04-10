using System.Text.Json;
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
        Assert.Contains("mems hook", stdout.ToString());
        Assert.Contains("mems instructions", stdout.ToString());
        Assert.Contains("mems mcp", stdout.ToString());
        Assert.Contains("mems shutdowndb", stdout.ToString());
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
        Assert.Contains("mems hook", stdout.ToString());
        Assert.Contains("mems instructions", stdout.ToString());
        Assert.Contains("mems mcp", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [TestMethod]
    public async Task Hook_PrintsSetupGuidance()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["hook"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("MemShack hook setup:", stdout.ToString());
        Assert.Contains("Hook assets:", stdout.ToString());
        Assert.Contains(".claude/settings.local.json", stdout.ToString());
        Assert.Contains(".codex/hooks.json", stdout.ToString());
        Assert.Contains("bash ", stdout.ToString());
    }

    [TestMethod]
    public async Task Instructions_PrintsSetupGuidance()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["instructions"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("MemShack instructions setup:", stdout.ToString());
        Assert.Contains("Instruction assets:", stdout.ToString());
        Assert.Contains("codex.md", stdout.ToString());
        Assert.Contains("claude-code.md", stdout.ToString());
        Assert.Contains("mems wake-up", stdout.ToString());
    }

    [TestMethod]
    public async Task Hook_WithOutputDir_ExportsHookAssets()
    {
        using var temp = new TemporaryDirectory();
        var exportRoot = temp.GetPath("hook-export");
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["hook", "--output-dir", exportRoot], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(Path.Combine(exportRoot, "README.md")));
        Assert.True(File.Exists(Path.Combine(exportRoot, "memshack_save_hook.sh")));
        Assert.True(File.Exists(Path.Combine(exportRoot, "memshack_precompact_hook.sh")));
        Assert.Contains("Exported the hook files", stdout.ToString());
    }

    [TestMethod]
    public async Task Hook_WithUnexpectedArgument_PrintsUsage()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["hook", "--bad-flag"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Usage: mems hook [--output-dir <dir>]", stderr.ToString());
    }

    [TestMethod]
    public async Task Instructions_WithOutputDir_ExportsInstructionAssets()
    {
        using var temp = new TemporaryDirectory();
        var exportRoot = temp.GetPath("instructions-export");
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["instructions", "--output-dir", exportRoot], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(Path.Combine(exportRoot, "README.md")));
        Assert.True(File.Exists(Path.Combine(exportRoot, "codex.md")));
        Assert.True(File.Exists(Path.Combine(exportRoot, "claude-code.md")));
        Assert.Contains("Exported the instruction files", stdout.ToString());
    }

    [TestMethod]
    public async Task Instructions_WithUnexpectedArgument_PrintsUsage()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["instructions", "--bad-flag"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Usage: mems instructions [--output-dir <dir>]", stderr.ToString());
    }

    [TestMethod]
    public async Task Mcp_PrintsSetupGuidance()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["--palace", palacePath, "mcp"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("MemShack MCP quick setup:", stdout.ToString());
        Assert.Contains("claude mcp add mempalace -- dotnet run --project", stdout.ToString());
        Assert.Contains("--palace", stdout.ToString());
    }

    [TestMethod]
    public async Task InternalWhereChroma_PrintsBundledCandidatePath()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["__where-chroma"], stdout, stderr);
        var output = stdout.ToString().Trim();

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(
            output == "unsupported-platform" ||
            output.EndsWith($"{Path.DirectorySeparatorChar}chroma{Path.DirectorySeparatorChar}win-x64{Path.DirectorySeparatorChar}chroma.exe", StringComparison.Ordinal) ||
            output.EndsWith($"{Path.DirectorySeparatorChar}chroma{Path.DirectorySeparatorChar}linux-x64{Path.DirectorySeparatorChar}chroma", StringComparison.Ordinal) ||
            output.EndsWith($"{Path.DirectorySeparatorChar}chroma{Path.DirectorySeparatorChar}osx-arm64{Path.DirectorySeparatorChar}chroma", StringComparison.Ordinal),
            $"Unexpected bundled Chroma candidate path: {output}");
    }

    [TestMethod]
    public async Task DefaultMineBackend_RequiresChromaWhenCompatibilityIsNotConfigured()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.ConfigJson), """
            {
              "chroma_auto_install": false
            }
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(Path.Combine(projectRoot, ConfigFileNames.MempalaceYaml), """
            wing: project
            rooms:
              - name: backend
                description: Backend code
            """);
        File.WriteAllText(Path.Combine(projectRoot, "backend", "auth.py"), "print('hello')\n");

        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["--palace", palacePath, "mine", projectRoot], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("managed Chroma database", stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [TestMethod]
    public async Task ShutdownDb_WithoutRecordedSidecar_PrintsFriendlyMessage()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["--palace", palacePath, "shutdowndb"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("MemShack ShutdownDb", stdout.ToString());
        Assert.Contains("No managed Chroma sidecar is recorded for this palace.", stdout.ToString());
    }

    [TestMethod]
    public async Task ShutdownDb_WithExplicitChromaUrl_DoesNotAttemptManagedSidecarShutdown()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.ConfigJson), """
            {
              "chroma_url": "http://example.test:8000"
            }
            """);
        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["--palace", palacePath, "shutdowndb"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Configured to use an external Chroma server.", stdout.ToString());
        Assert.Contains("http://example.test:8000", stdout.ToString());
    }

    [TestMethod]
    public async Task Search_WithoutPalace_PrintsInitAndMineGuidance()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.ConfigJson), """
            {
              "vector_store_backend": "compatibility"
            }
            """);
        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["--palace", palacePath, "search", "missing palace"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains($"No palace found at {palacePath}", stderr.ToString());
        Assert.Contains("Run: mems init <dir> then mems mine <dir>", stderr.ToString());
    }

    [TestMethod]
    public async Task Search_WithoutQuery_PrintsUsage()
    {
        using var temp = new TemporaryDirectory();
        var app = new CliApp(configDirectory: temp.GetPath("config"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["search"], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Usage: mems search <query> [--wing NAME] [--room NAME]", stderr.ToString());
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
        Assert.Contains("Config saved", stdout.ToString());
        Assert.Contains("Next step:", stdout.ToString());
    }

    [TestMethod]
    public async Task Init_WithTrailingSeparator_WritesWingFromDirectoryName()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var projectRoot = temp.GetPath("project-root");
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(Path.Combine(projectRoot, "backend", "app.py"), "print('hello')\n" + new string('a', 80));

        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["init", projectRoot + Path.DirectorySeparatorChar, "--yes"], stdout, stderr);
        var config = File.ReadAllText(Path.Combine(projectRoot, ConfigFileNames.MempalaceYaml));

        Assert.Equal(0, exitCode);
        Assert.Contains("wing: project_root", config);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [TestMethod]
    public async Task Init_WithYes_ShowsEntityDetectionAndWritesEntitiesJson()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "backend"));
        File.WriteAllText(
            Path.Combine(projectRoot, "docs", "notes.md"),
            """
            > Riley: are we ready?
            Riley said the plan still works.
            Riley told me she wanted to ship it carefully.
            Thanks Riley, she already tested everything.
            Riley laughed when she saw the green build.

            We are building Tool now.
            Tool v2 replaces the older scripts.
            I deployed Tool yesterday.
            The Tool architecture is much simpler.
            """);
        File.WriteAllText(Path.Combine(projectRoot, "backend", "app.py"), "print('hello')\n");

        var app = new CliApp(configDirectory: configDirectory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["init", projectRoot, "--yes"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Scanning for entities in:", stdout.ToString());
        Assert.Contains("MemShack - Entity Detection", stdout.ToString());
        Assert.Contains("Entities saved:", stdout.ToString());
        Assert.Contains("Local setup", stdout.ToString());
        Assert.Contains("ROOM:", stdout.ToString());

        var entitiesPath = Path.Combine(projectRoot, ConfigFileNames.EntitiesJson);
        Assert.True(File.Exists(entitiesPath));

        using var document = JsonDocument.Parse(File.ReadAllText(entitiesPath));
        var root = document.RootElement;
        Assert.Contains(
            root.GetProperty("people").EnumerateArray().Select(item => item.GetString()).OfType<string>(),
            name => name == "Riley");
        Assert.Contains(
            root.GetProperty("projects").EnumerateArray().Select(item => item.GetString()).OfType<string>(),
            name => name == "Tool");
        Assert.True(root.GetProperty("entities").TryGetProperty("Riley", out _));
        Assert.True(root.GetProperty("entities").TryGetProperty("Tool", out _));
    }

    [TestMethod]
    public async Task Init_WithoutYes_ShowsInteractiveEntityReview()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.WriteAllText(
            Path.Combine(projectRoot, "docs", "notes.md"),
            """
            > Riley: are we ready?
            Riley said the plan still works.
            Riley told me she wanted to ship it carefully.

            We are building Tool now.
            Tool v2 replaces the older scripts.
            The Tool architecture is much simpler.
            """);

        var app = new CliApp(configDirectory: configDirectory);
        var stdin = new StringReader(Environment.NewLine);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await app.RunAsync(["init", projectRoot], stdin, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Your choice [enter/edit/add]:", stdout.ToString());
        Assert.Contains("Confirmed:", stdout.ToString());
        Assert.Contains("Accept all rooms", stdout.ToString());
        Assert.True(File.Exists(Path.Combine(projectRoot, ConfigFileNames.EntitiesJson)));
    }

    [TestMethod]
    public async Task ProjectFlow_MineSearchWakeUpStatusAndRepair_Work()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        var palacePath = temp.GetPath("palace");
        var projectRoot = temp.GetPath("project");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.ConfigJson), """
            {
              "vector_store_backend": "compatibility"
            }
            """);
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
        Assert.Contains("MemShack Mine", mineOut.ToString());
        Assert.Contains("Wing:    project", mineOut.ToString());
        Assert.Contains("By room:", mineOut.ToString());
        Assert.Contains("Next: mems search \"what you're looking for\"", mineOut.ToString());
        Assert.Contains("Drawers filed", mineOut.ToString());
        Assert.Contains("Store:   legacy compatibility JSON", mineOut.ToString());
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
        File.WriteAllText(Path.Combine(configDirectory, ConfigFileNames.ConfigJson), """
            {
              "vector_store_backend": "compatibility"
            }
            """);
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
                    "âº 9:41 PM Tuesday, April 01, 2026",
                    "> plan migration tasks",
                    "Sure.",
                    "line 5",
                    "line 6",
                    "line 7",
                    "line 8",
                    "line 9",
                    "line 10",
                    "Claude Code v1",
                    "âº 10:05 PM Tuesday, April 01, 2026",
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
        Assert.Contains("Store: legacy compatibility JSON", mineOut.ToString());
        Assert.Contains(Path.Combine(palacePath, "collections", $"{CollectionNames.Drawers}.json"), mineOut.ToString());
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
