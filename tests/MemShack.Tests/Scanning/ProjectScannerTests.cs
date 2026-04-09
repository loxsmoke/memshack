using MemShack.Application.Scanning;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Scanning;

[TestClass]
public sealed class ProjectScannerTests
{
    private readonly ProjectScanner _scanner = new();

    [TestMethod]
    public void ScanProject_RespectsGitignore()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(".gitignore", "ignored.py\ngenerated/\n");
        temp.WriteFile("src/app.py", "print('hello')\n" + new string('a', 80));
        temp.WriteFile("ignored.py", "print('ignore')\n" + new string('b', 80));
        temp.WriteFile("generated/artifact.py", "print('artifact')\n" + new string('c', 80));

        var files = ScanRelative(temp.Root);

        Assert.Equal(["src/app.py"], files);
    }

    [TestMethod]
    public void ScanProject_RespectsNestedGitignoreNegation()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(".gitignore", "*.csv\n");
        temp.WriteFile("subrepo/.gitignore", "!keep.csv\n");
        temp.WriteFile("drop.csv", "a,b,c\n" + new string('1', 80));
        temp.WriteFile("subrepo/keep.csv", "a,b,c\n" + new string('2', 80));

        var files = ScanRelative(temp.Root);

        Assert.Equal(["subrepo/keep.csv"], files);
    }

    [TestMethod]
    public void ScanProject_DoesNotReincludeFileFromIgnoredDirectory()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(".gitignore", "generated/\n!generated/keep.py\n");
        temp.WriteFile("generated/drop.py", "print('drop')\n" + new string('d', 80));
        temp.WriteFile("generated/keep.py", "print('keep')\n" + new string('e', 80));

        var files = ScanRelative(temp.Root);

        Assert.Empty(files);
    }

    [TestMethod]
    public void ScanProject_CanIncludeIgnoredDirectory()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(".gitignore", "docs/\n");
        temp.WriteFile("docs/guide.md", "# Guide\n" + new string('g', 80));

        var files = ScanRelative(temp.Root, includeIgnored: ["docs"]);

        Assert.Equal(["docs/guide.md"], files);
    }

    [TestMethod]
    public void ScanProject_IncludeOverrideBeatsSkipDirs()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(".pytest_cache/cache.py", "print('cache')\n" + new string('x', 80));

        var files = ScanRelative(temp.Root, respectGitignore: false, includeIgnored: [".pytest_cache"]);

        Assert.Equal([".pytest_cache/cache.py"], files);
    }

    [TestMethod]
    public void ScanProject_ProcessesCurrentDirectoryFilesBeforeDescending()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile("root.md", "# Root\n" + new string('r', 80));
        temp.WriteFile("nested/child.md", "# Child\n" + new string('c', 80));

        var files = _scanner.ScanProject(temp.Root, respectGitignore: false)
            .Select(path => Path.GetRelativePath(temp.Root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(2, files.Length);
        Assert.Equal("root.md", files[0]);
        Assert.Equal("nested/child.md", files[1]);
    }

    private IReadOnlyList<string> ScanRelative(
        string projectRoot,
        bool respectGitignore = true,
        IEnumerable<string>? includeIgnored = null)
    {
        return _scanner.ScanProject(projectRoot, respectGitignore, includeIgnored)
            .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}
