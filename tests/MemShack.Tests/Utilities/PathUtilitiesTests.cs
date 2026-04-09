using MemShack.Core.Utilities;

namespace MemShack.Tests.Utilities;

[TestClass]
public sealed class PathUtilitiesTests
{
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
    public void ToProjectRelativePosixPath_NormalizesSeparators()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "repo");
        var targetPath = Path.Combine(projectPath, "src", "app.py");
        var result = PathUtilities.ToProjectRelativePosixPath(projectPath, targetPath);

        Assert.Equal("src/app.py", result);
    }
}
