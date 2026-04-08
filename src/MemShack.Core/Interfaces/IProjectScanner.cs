namespace MemShack.Core.Interfaces;

public interface IProjectScanner
{
    IReadOnlyList<string> ScanProject(
        string projectDirectory,
        bool respectGitignore = true,
        IEnumerable<string>? includeIgnored = null);
}
