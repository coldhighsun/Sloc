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
    IReadOnlySet<string> SymlinkedFilePaths,
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
    /// <summary>
    /// Walks the tree under <paramref name="root"/> once, collecting what the flags request.
    /// </summary>
    /// <param name="root">The directory to walk.</param>
    /// <param name="excludedDirectoryNames">Directory names the walk never descends into.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <param name="followSymlinks">Whether to follow symlinked/junctioned directories and files.</param>
    /// <param name="ignoreCase">Whether rule patterns match case-insensitively.</param>
    /// <param name="collectGitignore">Whether to collect <c>.gitignore</c> files.</param>
    /// <param name="collectGitattributes">Whether to collect <c>.gitattributes</c> files.</param>
    /// <param name="collectFiles">Whether to collect candidate file paths.</param>
    /// <param name="onDirectoryVisited">Optional progress callback.</param>
    /// <param name="skipNestedRepositories">
    /// Whether to skip, and report as skipped, every subdirectory that is the root of a
    /// nested repository (a submodule or other clone, see <see cref="GitWorkTree.IsRepositoryRoot"/>),
    /// as git does within a repository. An unfollowed directory link is reported as a link instead.
    /// </param>
    /// <returns>Everything the walk collected.</returns>
    public static ScanTreeWalkResult Walk(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        bool followSymlinks,
        bool ignoreCase,
        bool collectGitignore,
        bool collectGitattributes,
        bool collectFiles,
        Action<int, string>? onDirectoryVisited,
        bool skipNestedRepositories = false)
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
        var symlinkedFilePaths = new HashSet<string>();
        var skipped = new List<SkippedEntry>();
        var visited = 0;

        // Loop and double-count checks compare real paths, so a root given through a
        // symlink/junction is still recognized as the directory its links point back into.
        var realRoot = followSymlinks ? SymlinkGuard.GetRealPath(normalizedRoot) ?? normalizedRoot : normalizedRoot;
        var ancestors = new List<string> { realRoot };
        var follow = followSymlinks ? new FollowState(realRoot) : null;

        Collect(
            fullRoot, realRoot, normalizedRoot, excludedDirectoryNames, recursive, ignoreCase,
            collectGitignore, collectGitattributes, collectFiles,
            gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, symlinkedFilePaths, skipped,
            onDirectoryVisited, ref visited, ancestors, follow, skipNestedRepositories);

        return new ScanTreeWalkResult(
            gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, symlinkedFilePaths, skipped);
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

    /// <summary>
    /// Visits <paramref name="directory"/>: records its rule files and (when requested) its
    /// files, then recurses into its subdirectories, following symlinks/junctions only when
    /// <paramref name="follow"/> is set and the target is neither a loop nor already covered.
    /// </summary>
    /// <param name="directory">The directory as reached from the scan root (possibly through links).</param>
    /// <param name="realDirectory">The real path of <paramref name="directory"/>, with every link resolved.</param>
    /// <param name="normalizedRoot">The scan root, relative to which rule-file bases are computed.</param>
    /// <param name="excludedDirectoryNames">Directory names the walk never descends into.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    /// <param name="ignoreCase">Whether rule patterns match case-insensitively.</param>
    /// <param name="collectGitignore">Whether to collect <c>.gitignore</c> files.</param>
    /// <param name="collectGitattributes">Whether to collect <c>.gitattributes</c> files.</param>
    /// <param name="collectFiles">Whether to collect candidate file paths.</param>
    /// <param name="gitignoreFiles">Receives the <c>.gitignore</c> files found.</param>
    /// <param name="attributesFiles">Receives the <c>.gitattributes</c> files found.</param>
    /// <param name="symlinkedDirectories">Receives the directories excluded from the walk.</param>
    /// <param name="filePaths">Receives the candidate file paths.</param>
    /// <param name="symlinkedFilePaths">Receives the file paths that are symlinks.</param>
    /// <param name="skipped">Receives the entries that could not be read.</param>
    /// <param name="onDirectoryVisited">Optional progress callback.</param>
    /// <param name="visited">The running count of visited directories.</param>
    /// <param name="ancestors">The real paths of the directories on the current path from the scan root.</param>
    /// <param name="follow">The symlink-following state, or <see langword="null"/> when links are not followed.</param>
    /// <param name="skipNestedRepositories">Whether to skip subdirectories that are nested repositories.</param>
    private static void Collect(
        string directory,
        string realDirectory,
        string normalizedRoot,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        bool ignoreCase,
        bool collectGitignore,
        bool collectGitattributes,
        bool collectFiles,
        List<GitIgnoreRules.GitIgnoreFile> gitignoreFiles,
        List<GitAttributesRules.AttributesFile> attributesFiles,
        List<string> symlinkedDirectories,
        List<string> filePaths,
        HashSet<string> symlinkedFilePaths,
        List<SkippedEntry> skipped,
        Action<int, string>? onDirectoryVisited,
        ref int visited,
        List<string> ancestors,
        FollowState? follow,
        bool skipNestedRepositories)
    {
        visited++;
        onDirectoryVisited?.Invoke(visited, directory);

        // Rule files are read outside the directory-level try/catch below: one that can't be
        // read (e.g. locked by another process) skips just that file, as git warns and carries
        // on, rather than aborting the directory and silently dropping its whole subtree.
        if (collectGitignore
            && TryReadRuleFile(Path.Combine(directory, ".gitignore"), skipped) is { } ignoreLines
            && GitIgnoreRules.CompilePatterns(ignoreLines, ignoreCase) is { Count: > 0 } ignorePatterns)
        {
            var baseDir = GitIgnoreRules.NormalizeBase(Path.GetRelativePath(normalizedRoot, directory));
            gitignoreFiles.Add(new GitIgnoreRules.GitIgnoreFile(baseDir, ignorePatterns, ignoreCase));
        }

        if (collectGitattributes
            && TryReadRuleFile(Path.Combine(directory, ".gitattributes"), skipped) is { } attributeLines
            && GitAttributesRules.ParseLines(attributeLines) is { Count: > 0 } parsedAttributeLines)
        {
            var baseDir = GitAttributesRules.NormalizeBase(Path.GetRelativePath(normalizedRoot, directory));
            attributesFiles.Add(new GitAttributesRules.AttributesFile(baseDir, parsedAttributeLines));
        }

        // Outside the real scan root (i.e. below a followed link), the same real file or
        // directory can be reached again through another link with an overlapping target.
        var outsideRoot = follow is not null && !SymlinkGuard.IsAncestorOrSelf(follow.RealRoot, realDirectory);

        string[] subdirectories;
        try
        {
            if (collectFiles)
            {
                // Enumerating via DirectoryInfo yields FileInfo entries whose Attributes are
                // already populated from this same directory read, so the link check below
                // needs no separate per-file stat call except for reparse points.
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
                {
                    try
                    {
                        if (SymlinkGuard.IsLink(file))
                        {
                            symlinkedFilePaths.Add(file.FullName);
                            if (follow is not null && !follow.ShouldFollowFile(file.FullName))
                            {
                                continue;
                            }
                        }
                        else if (SymlinkGuard.IsCloudOnly(file.Attributes))
                        {
                            // Reading an online-only placeholder (e.g. OneDrive Files-On-Demand)
                            // would make the sync client download it; report it instead.
                            skipped.Add(new SkippedEntry(file.FullName, "cloud-only file (not downloaded)"));
                            continue;
                        }
                        else if (outsideRoot && !follow!.SeenFiles.Add(Path.Combine(realDirectory, file.Name)))
                        {
                            continue;
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

            bool isLink;
            bool isReparsePoint;
            try
            {
                var info = new DirectoryInfo(subdirectory);
                isReparsePoint = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                isLink = SymlinkGuard.IsLink(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The subdirectory vanished or became inaccessible between GetDirectories
                // and this check (a TOCTOU race with a concurrent delete/permission change).
                // Record it as skipped rather than letting the exception abort the whole walk.
                skipped.Add(new SkippedEntry(subdirectory, ex.Message));
                continue;
            }

            // A reparse point that isn't a link (e.g. a OneDrive cloud folder) is an
            // ordinary directory as far as the scan is concerned, provided it can actually be
            // listed. One that can't (e.g. a WSL LX symlink, which .NET reports without a link
            // target) is excluded like any other unfollowable link.
            if (!isLink && isReparsePoint && !SymlinkGuard.CanEnumerate(subdirectory))
            {
                symlinkedDirectories.Add(subdirectory);
                continue;
            }

            if (!isLink)
            {
                var realSubdirectory = Path.Combine(realDirectory, Path.GetFileName(subdirectory));
                if (outsideRoot && !follow!.VisitedDirectories.Add(realSubdirectory))
                {
                    // Already walked as (part of) another followed link's target.
                    symlinkedDirectories.Add(subdirectory);
                    continue;
                }

                if (skipNestedRepositories && IsNestedRepository(subdirectory, skipped))
                {
                    continue;
                }

                ancestors.Add(realSubdirectory);
                Collect(
                    subdirectory, realSubdirectory, normalizedRoot, excludedDirectoryNames, recursive, ignoreCase,
                    collectGitignore, collectGitattributes, collectFiles,
                    gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, symlinkedFilePaths, skipped,
                    onDirectoryVisited, ref visited, ancestors, follow, skipNestedRepositories);
                ancestors.RemoveAt(ancestors.Count - 1);
                continue;
            }

            if (follow is null)
            {
                // The caller opted out of following symlinked directories entirely.
                symlinkedDirectories.Add(subdirectory);
                continue;
            }

            // A directory symlink/junction. Resolve its target's real path and check whether
            // following it would loop: the target is at or under a directory already on the
            // current path from the scan root (directly, or transitively through an earlier
            // symlink), or contains one, so following it would walk that path again. The
            // first ancestor is the real scan root, so a target inside the scan root, which
            // the normal tree walk already covers, is excluded the same way. A target already
            // walked through another link (a diamond-shaped chain, or overlapping targets) is
            // excluded too, so its files are not double-counted.
            var resolution = SymlinkGuard.Resolve(subdirectory, ancestors);
            if (!resolution.Resolved || resolution.IsLoop || !follow.VisitedDirectories.Add(resolution.Target!))
            {
                symlinkedDirectories.Add(subdirectory);
                continue;
            }

            if (skipNestedRepositories && IsNestedRepository(subdirectory, skipped))
            {
                continue;
            }

            ancestors.Add(resolution.Target!);
            Collect(
                subdirectory, resolution.Target!, normalizedRoot, excludedDirectoryNames, recursive, ignoreCase,
                collectGitignore, collectGitattributes, collectFiles,
                gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, symlinkedFilePaths, skipped,
                onDirectoryVisited, ref visited, ancestors, follow, skipNestedRepositories);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    /// <summary>
    /// Tracks what a walk that follows symlinks has already covered, by real path, so no
    /// file is counted twice however many links lead to it.
    /// </summary>
    /// <param name="realRoot">The real path of the scan root.</param>
    private sealed class FollowState(string realRoot)
    {
        /// <summary>
        /// Gets the real path of the scan root, whose contents the plain tree walk covers.
        /// </summary>
        public string RealRoot { get; } = realRoot;

        /// <summary>
        /// Gets the real paths of directories outside <see cref="RealRoot"/> already walked.
        /// </summary>
        public HashSet<string> VisitedDirectories { get; } = new(SymlinkGuard.FileSystemPathComparer);

        /// <summary>
        /// Gets the real paths of files outside <see cref="RealRoot"/> already collected.
        /// </summary>
        public HashSet<string> SeenFiles { get; } = new(SymlinkGuard.FileSystemPathComparer);

        /// <summary>
        /// Whether the file symlink at <paramref name="linkPath"/> should be collected: not
        /// when its target is inside <see cref="RealRoot"/> (the tree walk already covers it)
        /// or was already collected. A dangling link is still collected, so reading it
        /// reports the failure as a skipped file.
        /// </summary>
        /// <param name="linkPath">The full path of the file symlink.</param>
        public bool ShouldFollowFile(string linkPath) =>
            SymlinkGuard.GetRealPath(linkPath) is not { } realPath
            || (!SymlinkGuard.IsAncestorOrSelf(RealRoot, realPath) && SeenFiles.Add(realPath));
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is the root of a nested repository (see
    /// <see cref="GitWorkTree.IsRepositoryRoot"/>), recording it in <paramref name="skipped"/> if so.
    /// </summary>
    /// <param name="directory">The subdirectory about to be walked.</param>
    /// <param name="skipped">The list a nested repository is recorded in.</param>
    private static bool IsNestedRepository(string directory, List<SkippedEntry> skipped)
    {
        if (!GitWorkTree.IsRepositoryRoot(directory))
        {
            return false;
        }

        skipped.Add(new SkippedEntry(directory, "nested git repository (submodule)"));
        return true;
    }

    /// <summary>
    /// Reads the lines of the rule file at <paramref name="path"/>, or returns
    /// <see langword="null"/> when it does not exist or cannot be read, recording the
    /// latter in <paramref name="skipped"/>.
    /// </summary>
    /// <param name="path">The path of the <c>.gitignore</c>/<c>.gitattributes</c> file.</param>
    /// <param name="skipped">The list a read failure is recorded in.</param>
    private static string[]? TryReadRuleFile(string path, List<SkippedEntry> skipped)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedEntry(path, ex.Message));
            return null;
        }
    }
}
