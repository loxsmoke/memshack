using MemShack.Application.Splitting;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Splitting;

[TestClass]
public sealed class MegaFileSplitterTests
{
    [TestMethod]
    public void FindSessionBoundaries_IgnoresContextRestoreHeaders()
    {
        var lines = new[]
        {
            "Claude Code v1",
            "real session",
            "line 3",
            "line 4",
            "line 5",
            "line 6",
            "Claude Code v1",
            "Ctrl+E to show 20 previous messages",
            "Claude Code v1",
            "fresh session",
        };

        var boundaries = MegaFileSplitter.FindSessionBoundaries(lines);

        Assert.Equal([0, 8], boundaries);
    }

    [TestMethod]
    public void Split_WritesPerSessionFilesAndBackup()
    {
        using var temp = new TemporaryDirectory();
        var configDirectory = temp.GetPath("config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "known_names.json"),
            """{"names":["Alice"],"username_map":{"alice":"Alice"}}""");

        var sourceDirectory = temp.GetPath("transcripts");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "mega.txt");
        File.WriteAllText(
            sourcePath,
            string.Join(
                '\n',
                [
                    "Claude Code v1",
                    "⏺ 9:41 PM Tuesday, April 01, 2026",
                    "/Users/alice/project",
                    "> plan migration work",
                    "Sure.",
                    "line 6",
                    "line 7",
                    "line 8",
                    "line 9",
                    "line 10",
                    "Claude Code v1",
                    "⏺ 10:05 PM Tuesday, April 01, 2026",
                    "> review compression output",
                    "Okay.",
                    "line 15",
                    "line 16",
                    "line 17",
                    "line 18",
                    "line 19",
                    "line 20",
                ]));

        var splitter = new MegaFileSplitter(configDirectory);
        var result = splitter.Split(sourceDirectory);

        Assert.Equal(1, result.MegaFileCount);
        Assert.Equal(2, result.SessionsCreated);
        Assert.All(result.Files.SelectMany(file => file.Sessions), session => Assert.True(File.Exists(session.OutputPath)));
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "mega.mega_backup")));
    }
}
