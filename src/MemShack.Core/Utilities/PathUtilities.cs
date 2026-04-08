namespace MemShack.Core.Utilities;

public static class PathUtilities
{
    public static string GetHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.GetFullPath(home);
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.GetFullPath(userProfile);
        }

        var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
        var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
        if (!string.IsNullOrWhiteSpace(homeDrive) && !string.IsNullOrWhiteSpace(homePath))
        {
            return Path.GetFullPath(Path.Combine(homeDrive, homePath));
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~")
        {
            return GetHomeDirectory();
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(GetHomeDirectory(), path[2..]);
        }

        return path;
    }

    public static string ToPosixPath(string path) => path.Replace('\\', '/');

    public static string ToProjectRelativePosixPath(string projectPath, string targetPath)
    {
        var relative = Path.GetRelativePath(projectPath, targetPath);
        return ToPosixPath(relative).Trim('/');
    }

    public static HashSet<string> NormalizeIncludePaths(IEnumerable<string>? includeIgnored)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        if (includeIgnored is null)
        {
            return normalized;
        }

        foreach (var rawEntry in includeIgnored)
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                continue;
            }

            foreach (var splitEntry in rawEntry.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = splitEntry.Trim().Trim('/', '\\');
                if (candidate.Length == 0)
                {
                    continue;
                }

                normalized.Add(ToPosixPath(candidate));
            }
        }

        return normalized;
    }
}
