using MemShack.Core.Interfaces;
using MemShack.Core.Utilities;

namespace MemShack.Application.Scanning;

public sealed class ProjectScanner : IProjectScanner
{
    private static readonly HashSet<string> ReadableExtensions =
    [
        ".txt",
        ".md",
        ".py",
        ".js",
        ".ts",
        ".jsx",
        ".tsx",
        ".json",
        ".yaml",
        ".yml",
        ".html",
        ".css",
        ".java",
        ".go",
        ".rs",
        ".rb",
        ".sh",
        ".csv",
        ".sql",
        ".toml",
    ];

    private static readonly HashSet<string> SkipDirectories =
    [
        ".git",
        "node_modules",
        "__pycache__",
        ".venv",
        "venv",
        "env",
        "dist",
        "build",
        ".next",
        "coverage",
        ".mempalace",
        ".ruff_cache",
        ".mypy_cache",
        ".pytest_cache",
        ".cache",
        ".tox",
        ".nox",
        ".idea",
        ".vscode",
        ".ipynb_checkpoints",
        ".eggs",
        "htmlcov",
        "target",
    ];

    private static readonly HashSet<string> SkipFilenames =
    [
        "mempalace.yaml",
        "mempalace.yml",
        "mempal.yaml",
        "mempal.yml",
        ".gitignore",
        "package-lock.json",
    ];

    public IReadOnlyList<string> ScanProject(
        string projectDirectory,
        bool respectGitignore = true,
        IEnumerable<string>? includeIgnored = null)
    {
        var projectPath = Path.GetFullPath(PathUtilities.ExpandHome(projectDirectory));
        var files = new List<string>();
        var includePaths = PathUtilities.NormalizeIncludePaths(includeIgnored);

        Walk(projectPath, respectGitignore ? [] : null);
        files.Sort(StringComparer.Ordinal);
        return files;

        void Walk(string currentDirectory, IReadOnlyList<GitignoreMatcher>? activeMatchers)
        {
            var matchers = activeMatchers is null ? null : new List<GitignoreMatcher>(activeMatchers);
            if (respectGitignore && matchers is not null)
            {
                var currentMatcher = GitignoreMatcher.FromDirectory(currentDirectory);
                if (currentMatcher is not null)
                {
                    matchers.Add(currentMatcher);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory).OrderBy(path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                var forceInclude = IsForceIncluded(directory, projectPath, includePaths);
                if (!forceInclude && ShouldSkipDirectory(name))
                {
                    continue;
                }

                if (respectGitignore && matchers is { Count: > 0 } && !forceInclude &&
                    GitignoreMatcher.IsGitignored(directory, matchers, isDirectory: true))
                {
                    continue;
                }

                Walk(directory, matchers);
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory).OrderBy(path => path, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(file);
                var extension = Path.GetExtension(file);
                var forceInclude = IsForceIncluded(file, projectPath, includePaths);
                var exactForceInclude = IsExactForceInclude(file, projectPath, includePaths);

                if (!forceInclude && SkipFilenames.Contains(fileName))
                {
                    continue;
                }

                if (!ReadableExtensions.Contains(extension) && !exactForceInclude)
                {
                    continue;
                }

                if (respectGitignore && matchers is { Count: > 0 } && !forceInclude &&
                    GitignoreMatcher.IsGitignored(file, matchers, isDirectory: false))
                {
                    continue;
                }

                files.Add(Path.GetFullPath(file));
            }
        }
    }

    private static bool ShouldSkipDirectory(string directoryName) =>
        SkipDirectories.Contains(directoryName) || directoryName.EndsWith(".egg-info", StringComparison.Ordinal);

    private static bool IsExactForceInclude(string path, string projectPath, IReadOnlySet<string> includePaths)
    {
        if (includePaths.Count == 0)
        {
            return false;
        }

        var relative = PathUtilities.ToProjectRelativePosixPath(projectPath, path);
        return includePaths.Contains(relative);
    }

    private static bool IsForceIncluded(string path, string projectPath, IReadOnlySet<string> includePaths)
    {
        if (includePaths.Count == 0)
        {
            return false;
        }

        var relative = PathUtilities.ToProjectRelativePosixPath(projectPath, path);
        if (relative.Length == 0)
        {
            return false;
        }

        foreach (var includePath in includePaths)
        {
            if (relative == includePath)
            {
                return true;
            }

            if (relative.StartsWith($"{includePath}/", StringComparison.Ordinal))
            {
                return true;
            }

            if (includePath.StartsWith($"{relative}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
