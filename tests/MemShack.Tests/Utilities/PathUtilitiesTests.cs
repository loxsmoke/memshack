using MemShack.Core.Utilities;

namespace MemShack.Tests.Utilities;

[TestClass]
public sealed class PathUtilitiesTests
{
    private static readonly object CurrentDirectoryLock = new();

    [TestMethod]
    public void ExpandHome_ResolvesBareTilde()
    {
        var result = PathUtilities.ExpandHome("~");

        Assert.Equal(PathUtilities.GetHomeDirectory(), result);
    }

    [TestMethod]
    public void NormalizeIncludePaths_SplitsCommaSeparatedEntries()
    {
        var result = PathUtilities.NormalizeIncludePaths(["docs", @"generated\keep.py,README"]);

        Assert.Equal(3, result.Count);
        Assert.Contains("docs", result);
        Assert.Contains("generated/keep.py", result);
        Assert.Contains("README", result);
    }

    [TestMethod]
    public void GetLeafName_IgnoresTrailingDirectorySeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo-name");
        var result = PathUtilities.GetLeafName(root + Path.DirectorySeparatorChar);

        Assert.Equal("repo-name", result);
    }

    [TestMethod]
    [DataRow(".", "repo")]
    [DataRow("..", "parent")]
    [DataRow("../", "parent")]
    [DataRow(@"..\path", "path")]
    [DataRow(@"..\../", "grand")]
    public void GetLeafName_ResolvesRelativePaths(string input, string expected)
    {
        using var temp = new TemporaryDirectory();
        var grand = temp.GetPath("grand");
        var parent = Path.Combine(grand, "parent");
        var repo = Path.Combine(parent, "repo");
        var sibling = Path.Combine(parent, "path");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(sibling);

        lock (CurrentDirectoryLock)
        {
            var original = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = repo;

                var result = PathUtilities.GetLeafName(input);

                Assert.Equal(expected, result);
            }
            finally
            {
                Environment.CurrentDirectory = original;
            }
        }
    }

    [TestMethod]
    public void GetLeafName_MapsRootPathToStableName()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(root));

        var result = PathUtilities.GetLeafName(root!);
        var normalizedRoot = root!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expected = normalizedRoot.EndsWith(":", StringComparison.Ordinal)
            ? $"{char.ToLowerInvariant(normalizedRoot[0])}_drive"
            : "root";

        Assert.Equal(expected, result);
    }

    [TestMethod]
    public void ToProjectRelativePosixPath_NormalizesSeparators()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "repo");
        var targetPath = Path.Combine(projectPath, "src", "app.py");
        var result = PathUtilities.ToProjectRelativePosixPath(projectPath, targetPath);

        Assert.Equal("src/app.py", result);
    }
}
