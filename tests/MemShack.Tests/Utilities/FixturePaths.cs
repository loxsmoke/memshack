namespace MemShack.Tests.Utilities;

internal static class FixturePaths
{
    private static readonly Lazy<string> RepoRootPathLazy = new(FindRepoRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RepoRootPath => RepoRootPathLazy.Value;

    public static string FixturesRoot => Path.Combine(RepoRootPath, "fixtures");

    public static string GetPhase0Path(params string[] segments) => Combine("phase0", segments);

    public static string GetPalace2ShackPath(params string[] segments) => Combine("palace2shack", segments);

    private static string Combine(string phase, params string[] segments)
    {
        var parts = new[] { FixturesRoot, phase }.Concat(segments).ToArray();
        return Path.Combine(parts);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "fixtures")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")) &&
                File.Exists(Path.Combine(current.FullName, "MemShack.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MemShack repo root from the test assembly output directory.");
    }
}
