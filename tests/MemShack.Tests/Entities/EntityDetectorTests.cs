using MemShack.Application.Entities;

namespace MemShack.Tests.Entities;

[TestClass]
public sealed class EntityDetectorTests
{
    private readonly EntityDetector _detector = new();

    [TestMethod]
    public void DetectEntitiesFromText_ClassifiesPeopleAndProjects()
    {
        var text = """
            > Riley: are we ready?
            Riley said the plan still works.
            Riley told me she wanted to ship it carefully.
            Thanks Riley, she already tested everything.
            Riley laughed when she saw the green build.

            We are building MemShack now.
            MemShack v2 replaces the older scripts.
            I deployed MemShack yesterday.
            The MemShack architecture is much simpler.
            """;

        var result = _detector.DetectEntitiesFromText(text);

        var person = Assert.Single(result.People, entity => entity.Name == "Riley");
        var project = Assert.Single(result.Projects, entity => entity.Name == "MemShack");

        Assert.True(person.Confidence >= 0.7);
        Assert.True(project.Confidence >= 0.7);
    }

    [TestMethod]
    public void ScanForDetection_PrefersProseFiles()
    {
        using var temp = new Utilities.TemporaryDirectory();
        Directory.CreateDirectory(temp.GetPath("repo", "docs"));
        Directory.CreateDirectory(temp.GetPath("repo", "src"));
        temp.WriteFile(Path.Combine("repo", "docs", "notes.txt"), "Riley said hello.\nRiley smiled.\nRiley left.");
        temp.WriteFile(Path.Combine("repo", "docs", "journal.md"), "MemShack is real.\nMemShack ships soon.\nMemShack matters.");
        temp.WriteFile(Path.Combine("repo", "docs", "ideas.rst"), "Architecture notes.\nDecision log.\nPlan.");
        temp.WriteFile(Path.Combine("repo", "src", "app.py"), "class Riley: pass\n");

        var files = _detector.ScanForDetection(temp.GetPath("repo"));

        Assert.All(files, file => Assert.DoesNotContain("\\src\\", file, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.EndsWith("journal.md", StringComparison.OrdinalIgnoreCase));
    }
}
