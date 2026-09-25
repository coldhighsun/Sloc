namespace Sloc.Core.Scanning;

/// <summary>
/// The git working tree enclosing a directory, found the way git discovers a repository: by
/// walking up from the directory to the nearest one holding a <c>.git</c> directory, or a
/// <c>.git</c> file pointing at the repository directory (a linked worktree or a submodule).
/// </summary>
/// <param name="Root">The working tree's root directory.</param>
/// <param name="CommonDirectory">
/// The repository directory holding the shared <c>config</c> and <c>info/exclude</c>: the
/// <c>.git</c> directory itself, or for a linked worktree the main repository's.
/// </param>
internal sealed record GitWorkTree(string Root, string CommonDirectory)
{
    /// <summary>
    /// Gets the path of the repository's config file.
    /// </summary>
    public string ConfigPath => Path.Combine(CommonDirectory, "config");

    /// <summary>
    /// Gets the path of the repository's <c>info/exclude</c> file.
    /// </summary>
    public string InfoExcludePath => Path.Combine(CommonDirectory, "info", "exclude");

    /// <summary>
    /// Finds the working tree <paramref name="directory"/> belongs to.
    /// </summary>
    /// <param name="directory">The directory to start from.</param>
    /// <returns>
    /// The working tree, or <see langword="null"/> when no ancestor (including
    /// <paramref name="directory"/> itself) holds a <c>.git</c> entry, or the nearest
    /// <c>.git</c> file does not point at an existing directory.
    /// </returns>
    public static GitWorkTree? Find(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit))
            {
                return new GitWorkTree(current.FullName, ResolveCommonDirectory(dotGit));
            }

            if (File.Exists(dotGit))
            {
                // Like git, stop at the first .git entry, even an unusable one.
                return ReadGitFile(dotGit, current.FullName) is { } gitDirectory
                    ? new GitWorkTree(current.FullName, ResolveCommonDirectory(gitDirectory))
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the repository-relative directories above <paramref name="scanPrefix"/>, the
    /// root (the empty string) first, not including <paramref name="scanPrefix"/> itself.
    /// </summary>
    /// <param name="scanPrefix">A <c>/</c>-separated repository-relative directory, empty for the root.</param>
    public static IEnumerable<string> AncestorDirectories(string scanPrefix)
    {
        if (scanPrefix.Length == 0)
        {
            yield break;
        }

        yield return string.Empty;
        for (var slash = scanPrefix.IndexOf('/'); slash >= 0; slash = scanPrefix.IndexOf('/', slash + 1))
        {
            yield return scanPrefix[..slash];
        }
    }

    /// <summary>
    /// Returns <paramref name="directory"/>'s <c>/</c>-separated path relative to
    /// <see cref="Root"/>, the empty string for the root itself.
    /// </summary>
    /// <param name="directory">A directory at or below <see cref="Root"/>.</param>
    public string RelativeDirectory(string directory)
    {
        var relative = Path.GetRelativePath(Root, Path.GetFullPath(directory)).Replace('\\', '/').Trim('/');
        return relative == "." ? string.Empty : relative;
    }

    /// <summary>
    /// Reads the file named <paramref name="fileName"/> (e.g. <c>.gitignore</c>) in each
    /// directory above <paramref name="scanPrefix"/> (see <see cref="AncestorDirectories"/>),
    /// skipping any that is missing or cannot be read.
    /// </summary>
    /// <param name="scanPrefix">The scan root's repository-relative directory.</param>
    /// <param name="fileName">The rule file's name.</param>
    /// <returns>Each file read, with its repository-relative directory.</returns>
    public IEnumerable<(string Directory, string[] Lines)> ReadAncestorFiles(string scanPrefix, string fileName)
    {
        foreach (var directory in AncestorDirectories(scanPrefix))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(Root, directory, fileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return (directory, lines);
        }
    }

    /// <summary>
    /// Reads the repository directory a <c>.git</c> file points at (<c>gitdir: &lt;path&gt;</c>,
    /// relative to the directory holding the file).
    /// </summary>
    /// <param name="path">The <c>.git</c> file's path.</param>
    /// <param name="workTreeRoot">The directory holding the file.</param>
    /// <returns>The repository directory, or <see langword="null"/> if the file is unreadable, malformed, or points nowhere.</returns>
    private static string? ReadGitFile(string path, string workTreeRoot)
    {
        const string Prefix = "gitdir:";
        string contents;
        try
        {
            contents = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var line = contents.Split('\n', 2)[0].Trim();
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var gitDirectory = Path.GetFullPath(line[Prefix.Length..].Trim(), workTreeRoot);
        return Directory.Exists(gitDirectory) ? gitDirectory : null;
    }

    /// <summary>
    /// Returns the common directory of <paramref name="gitDirectory"/>: the one named by its
    /// <c>commondir</c> file (relative to <paramref name="gitDirectory"/>) for a linked
    /// worktree, otherwise <paramref name="gitDirectory"/> itself.
    /// </summary>
    /// <param name="gitDirectory">The repository directory.</param>
    private static string ResolveCommonDirectory(string gitDirectory)
    {
        try
        {
            var commonDirectory = File.ReadAllText(Path.Combine(gitDirectory, "commondir")).Trim();
            if (commonDirectory.Length > 0)
            {
                return Path.GetFullPath(commonDirectory, gitDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No commondir file: not a linked worktree.
        }

        return gitDirectory;
    }
}
