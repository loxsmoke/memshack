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
        var result = PathUtilities.ToProjectRelativePosixPath(@"C:\repo", @"C:\repo\src\app.py");

        Assert.Equal("src/app.py", result);
    }
}
