using Sloc.Core.Models;

namespace Sloc.Core.Scanning;

/// <summary>
/// The result of a single <see cref="ScanTreeWalker.Walk"/> pass: every <c>.gitignore</c>
/// and <c>.gitattributes</c> file discovered, the directories skipped due to symlink/junction
/// loop protection, the file paths found (when requested), and any directories that could
/// not be read.
/// </summary>
internal sealed record ScanTreeWalkResult(
    IReadOnlyList<GitIgnoreRules.GitIgnoreFile> GitignoreFiles,
    IReadOnlyList<GitAttributesRules.AttributesFile> AttributesFiles,
    IReadOnlyList<string> SymlinkedDirectories,
    IReadOnlyList<string> FilePaths,
    IReadOnlySet<string> ReparsePointFilePaths,
    IReadOnlyList<SkippedEntry> Skipped);

/// <summary>
/// Performs a single recursive directory-tree walk that can simultaneously discover
/// <c>.gitignore</c> files, <c>.gitattributes</c> files, candidate file paths, and
/// symlinked/junctioned directories that must be excluded for loop protection —
/// so callers that need more than one of these no longer have to walk the same tree
/// more than once.
/// </summary>
internal static class ScanTreeWalker
{
    public static ScanTreeWalkResult Walk(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        bool followSymlinks,
        bool collectGitignore,
        bool collectGitattributes,
        bool collectFiles,
        Action<int, string>? onDirectoryVisited)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        var fullRoot = Path.GetFullPath(root);
        // Keeps a filesystem root's own separator: trimming "C:\" to "C:" would make it
        // drive-relative (resolving to the current directory), and "/" would become "".
        var normalizedRoot = Path.TrimEndingDirectorySeparator(fullRoot);

        var gitignoreFiles = new List<GitIgnoreRules.GitIgnoreFile>();
        var attributesFiles = new List<GitAttributesRules.AttributesFile>();
        var symlinkedDirectories = new List<string>();
        var filePaths = new List<string>();
        var reparsePointFilePaths = new HashSet<string>();
        var skipped = new List<SkippedEntry>();
        var visited = 0;
        var ancestors = new List<string> { normalizedRoot };
        var followedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedRoot };

        Collect(
            fullRoot, normalizedRoot, excludedDirectoryNames, recursive, followSymlinks,
            collectGitignore, collectGitattributes, collectFiles,
            gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, reparsePointFilePaths, skipped,
            onDirectoryVisited, ref visited, ancestors, followedTargets);

        return new ScanTreeWalkResult(
            gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, reparsePointFilePaths, skipped);
    }

    /// <summary>
    /// Whether <see cref="Walk"/> visits the directory at <paramref name="relativeDirectory"/>:
    /// the root always, and a directory below it only when the walk is recursive and no path
    /// segment is an excluded directory name. The single source of this policy, used by the
    /// walk itself and by callers that must apply it to paths not laid out on disk (e.g.
    /// <see cref="DirectoryScanner.ScanSnapshot"/> choosing which rule files count).
    /// </summary>
    /// <param name="relativeDirectory">The <c>/</c>-separated path relative to the walk root, or empty for the root.</param>
    /// <param name="excludedDirectoryNames">Directory names the walk never descends into.</param>
    /// <param name="recursive">Whether the walk descends into subdirectories at all.</param>
    internal static bool Visits(string relativeDirectory, IReadOnlySet<string> excludedDirectoryNames, bool recursive) =>
        relativeDirectory.Length == 0
        || (recursive && !relativeDirectory.Split('/').Any(excludedDirectoryNames.Contains));

    private static void Collect(
        string directory,
        string normalizedRoot,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        bool followSymlinks,
        bool collectGitignore,
        bool collectGitattributes,
        bool collectFiles,
        List<GitIgnoreRules.GitIgnoreFile> gitignoreFiles,
        List<GitAttributesRules.AttributesFile> attributesFiles,
        List<string> symlinkedDirectories,
        List<string> filePaths,
        HashSet<string> reparsePointFilePaths,
        List<SkippedEntry> skipped,
        Action<int, string>? onDirectoryVisited,
        ref int visited,
        List<string> ancestors,
        HashSet<string> followedTargets)
    {
        visited++;
        onDirectoryVisited?.Invoke(visited, directory);

        string[] subdirectories;
        try
        {
            if (collectGitignore)
            {
                var ignorePath = Path.Combine(directory, ".gitignore");
                if (File.Exists(ignorePath))
                {
                    var patterns = GitIgnoreRules.CompilePatterns(File.ReadAllLines(ignorePath));
                    if (patterns.Count > 0)
                    {
                        var baseDir = GitIgnoreRules.NormalizeBase(Path.GetRelativePath(normalizedRoot, directory));
                        gitignoreFiles.Add(new GitIgnoreRules.GitIgnoreFile(baseDir, patterns));
                    }
                }
            }

            if (collectGitattributes)
            {
                var attributesPath = Path.Combine(directory, ".gitattributes");
                if (File.Exists(attributesPath))
                {
                    var patterns = GitAttributesRules.CompilePatterns(File.ReadAllLines(attributesPath));
                    if (patterns.Count > 0)
                    {
                        var baseDir = GitAttributesRules.NormalizeBase(Path.GetRelativePath(normalizedRoot, directory));
                        attributesFiles.Add(new GitAttributesRules.AttributesFile(baseDir, patterns));
                    }
                }
            }

            if (collectFiles)
            {
                // Enumerating via DirectoryInfo yields FileInfo entries whose Attributes are
                // already populated from this same directory read, so the reparse-point check
                // below needs no separate per-file stat call.
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
                {
                    try
                    {
                        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            reparsePointFilePaths.Add(file.FullName);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Matches the granularity of the per-file try/catch this replaced:
                        // one file's attribute read failing skips only that file, not the
                        // whole directory (and its subdirectories, since a directory-level
                        // catch would abort the walk below this point too).
                        skipped.Add(new SkippedEntry(file.FullName, ex.Message));
                        continue;
                    }

                    filePaths.Add(file.FullName);
                }
            }

            subdirectories = recursive ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedEntry(directory, ex.Message));
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            // Each subdirectory is one segment below an already-visited directory.
            if (!Visits(Path.GetFileName(subdirectory), excludedDirectoryNames, recursive))
            {
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = new DirectoryInfo(subdirectory).Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The subdirectory vanished or became inaccessible between GetDirectories
                // and this check (a TOCTOU race with a concurrent delete/permission change).
                // Record it as skipped rather than letting the exception abort the whole walk.
                skipped.Add(new SkippedEntry(subdirectory, ex.Message));
                continue;
            }

            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                ancestors.Add(subdirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Collect(
                    subdirectory, normalizedRoot, excludedDirectoryNames, recursive, followSymlinks,
                    collectGitignore, collectGitattributes, collectFiles,
                    gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, reparsePointFilePaths, skipped,
                    onDirectoryVisited, ref visited, ancestors, followedTargets);
                ancestors.RemoveAt(ancestors.Count - 1);
                continue;
            }

            // A directory symlink/junction. Resolve its target and check whether following
            // it would loop back onto a directory already on the current path from the
            // scan root (directly, or transitively through an earlier symlink), or onto a
            // real directory already reached via a different symlink chain — not just the
            // scan root itself, so both ancestor loops and diamond-shaped chains are caught.
            // A target at or under the scan root is also excluded: the normal tree walk
            // already covers those files, so following the link would double-count them.
            var resolution = SymlinkGuard.Resolve(subdirectory, ancestors);
            if (!resolution.Resolved || resolution.IsLoop || !followSymlinks
                || SymlinkGuard.IsAncestorOrSelf(normalizedRoot, resolution.Target!)
                || !followedTargets.Add(resolution.Target!))
            {
                // Not followed: resolution failed, it would loop, the target is inside the
                // scan root, it was already reached via another symlink, or the caller opted
                // out of following symlinked directories entirely. Either way, exclude it.
                symlinkedDirectories.Add(subdirectory);
                continue;
            }

            ancestors.Add(resolution.Target!);
            Collect(
                subdirectory, normalizedRoot, excludedDirectoryNames, recursive, followSymlinks,
                collectGitignore, collectGitattributes, collectFiles,
                gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, reparsePointFilePaths, skipped,
                onDirectoryVisited, ref visited, ancestors, followedTargets);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }
}
