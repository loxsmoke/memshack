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

    [TestMethod]
    public void ScanForDetection_VisitsCurrentDirectoryBeforeNestedDirectories()
    {
        using var temp = new Utilities.TemporaryDirectory();
        temp.WriteFile(Path.Combine("repo", "migration.md"), "Migration plan.\nMigration notes.\nMigration review.");
        Directory.CreateDirectory(temp.GetPath("repo", "docs"));
        temp.WriteFile(Path.Combine("repo", "docs", "nested.md"), "Nested notes.\nNested notes.\nNested notes.");

        var files = _detector.ScanForDetection(temp.GetPath("repo")).ToArray();

        var rootIndex = Array.FindIndex(files, file => file.EndsWith("migration.md", StringComparison.OrdinalIgnoreCase));
        var nestedIndex = Array.FindIndex(files, file => file.EndsWith("nested.md", StringComparison.OrdinalIgnoreCase));

        Assert.True(rootIndex >= 0);
        Assert.True(nestedIndex >= 0);
        Assert.True(rootIndex < nestedIndex);
    }

    [TestMethod]
    public void DetectEntitiesFromText_CanSkipCamelCaseCandidatesForParity()
    {
        var detector = new EntityDetector(includeCamelCaseCandidates: false);
        var text = """
            We are building MemShack now.
            MemShack v2 replaces the older scripts.
            I deployed MemShack yesterday.
            The MemShack architecture is much simpler.
            """;

        var result = detector.DetectEntitiesFromText(text);

        Assert.DoesNotContain(result.Projects, entity => entity.Name == "MemShack");
        Assert.DoesNotContain(result.Uncertain, entity => entity.Name == "MemShack");
    }

    [TestMethod]
    public void ScanForDetection_CanPrioritizeRelevantDocsAheadOfValidationNoise()
    {
        using var temp = new Utilities.TemporaryDirectory();
        temp.WriteFile(Path.Combine("repo", "README.md"), "Tool install notes.\nTool install notes.\nTool install notes.");
        temp.WriteFile(Path.Combine("repo", "docs", "migration-guide.md"), "Migration plan.\nMigration plan.\nMigration plan.");
        temp.WriteFile(Path.Combine("repo", "docs", "tool-installation.md"), "Tool setup.\nTool setup.\nTool setup.");
        temp.WriteFile(Path.Combine("repo", "docs", "compatibility", "wikipedia-decisions.md"), "Wikipedia-vs decisions.\nWikipedia-vs decisions.\nWikipedia-vs decisions.");
        temp.WriteFile(Path.Combine("repo", "docs", "validation", "validation-report.md"), "Validation-vs notes.\nValidation-vs notes.\nValidation-vs notes.");
        temp.WriteFile(Path.Combine("repo", "fixtures", "project-corpus", "notes", "roadmap.md"), "Roadmap notes.\nRoadmap notes.\nRoadmap notes.");

        var files = _detector.ScanForDetection(temp.GetPath("repo"), prioritizeRelevantFiles: true).ToArray();

        Assert.Contains(files, file => file.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.EndsWith("migration-guide.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.EndsWith("tool-installation.md", StringComparison.OrdinalIgnoreCase));

        var compatibilityIndex = Array.FindIndex(files, file => file.EndsWith("wikipedia-decisions.md", StringComparison.OrdinalIgnoreCase));
        var validationIndex = Array.FindIndex(files, file => file.EndsWith("validation-report.md", StringComparison.OrdinalIgnoreCase));
        var readmeIndex = Array.FindIndex(files, file => file.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));

        Assert.True(readmeIndex >= 0);
        Assert.True(compatibilityIndex > readmeIndex);
        Assert.True(validationIndex > readmeIndex);
    }

    [TestMethod]
    public void ExtractCandidates_IncludesMultiWordNamesWhenRepeated()
    {
        var text = """
            Ada Lovelace reviewed the design.
            Ada Lovelace approved the plan.
            Ada Lovelace wrote the notes.
            """;

        var candidates = _detector.ExtractCandidates(text);

        Assert.Equal(3, candidates["Ada Lovelace"]);
    }

    [TestMethod]
    public void DetectEntitiesFromText_PronounOnlySignalsRemainUncertain()
    {
        var text = """
            Mems can help her later.
            I think Mems will stay close to her notes.
            Mems was nearby when her test run finished.
            """;

        var result = _detector.DetectEntitiesFromText(text);

        Assert.DoesNotContain(result.People, entity => entity.Name == "Mems");
        Assert.Contains(result.Uncertain, entity => entity.Name == "Mems");
    }
}
