using Sloc.Core.Models;

namespace Sloc.Core;

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
        var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var gitignoreFiles = new List<GitIgnoreRules.GitIgnoreFile>();
        var attributesFiles = new List<GitAttributesRules.AttributesFile>();
        var symlinkedDirectories = new List<string>();
        var filePaths = new List<string>();
        var skipped = new List<SkippedEntry>();
        var visited = 0;
        var ancestors = new List<string> { normalizedRoot };
        var followedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedRoot };

        Collect(
            fullRoot, normalizedRoot, excludedDirectoryNames, recursive, followSymlinks,
            collectGitignore, collectGitattributes, collectFiles,
            gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, skipped,
            onDirectoryVisited, ref visited, ancestors, followedTargets);

        return new ScanTreeWalkResult(gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, skipped);
    }

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
                filePaths.AddRange(Directory.GetFiles(directory));
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
            var name = Path.GetFileName(subdirectory);
            if (excludedDirectoryNames.Contains(name))
            {
                continue;
            }

            if (!new DirectoryInfo(subdirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                ancestors.Add(subdirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Collect(
                    subdirectory, normalizedRoot, excludedDirectoryNames, recursive, followSymlinks,
                    collectGitignore, collectGitattributes, collectFiles,
                    gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, skipped,
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
                gitignoreFiles, attributesFiles, symlinkedDirectories, filePaths, skipped,
                onDirectoryVisited, ref visited, ancestors, followedTargets);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }
}
