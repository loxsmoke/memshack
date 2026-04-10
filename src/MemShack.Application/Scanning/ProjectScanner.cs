using MemShack.Core.Interfaces;
using MemShack.Core.Utilities;

namespace MemShack.Application.Scanning;

public sealed class ProjectScanner : IProjectScanner
{
    private const long MaxProjectFileBytes = 10L * 1024 * 1024;

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

    private static readonly HashSet<string> UpstreamSkipDirectories =
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

    private static readonly HashSet<string> AdditionalSkipDirectories =
    [
        ".dotnet",
        ".nuget",
        "bin",
        "obj",
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

            var directories = new List<string>();

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (IsSymlinkPath(directory))
                {
                    continue;
                }

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

                directories.Add(directory);
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                if (IsSymlinkPath(file) || IsOversizedProjectFile(file))
                {
                    continue;
                }

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

            foreach (var directory in directories)
            {
                Walk(directory, matchers);
            }
        }
    }

    private static bool ShouldSkipDirectory(string directoryName) =>
        UpstreamSkipDirectories.Contains(directoryName) ||
        AdditionalSkipDirectories.Contains(directoryName) ||
        directoryName.EndsWith(".egg-info", StringComparison.Ordinal);

    private static bool IsSymlinkPath(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsOversizedProjectFile(string path)
    {
        try
        {
            return new FileInfo(path).Length > MaxProjectFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

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
