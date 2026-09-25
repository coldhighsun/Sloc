using Sloc.Core.Models;

namespace Sloc.Core.Scanning;

/// <summary>
/// The kind of a git rule file.
/// </summary>
internal enum RuleFileKind
{
    /// <summary>
    /// A <c>.gitignore</c> file.
    /// </summary>
    Gitignore,

    /// <summary>
    /// A <c>.gitattributes</c> file.
    /// </summary>
    Gitattributes,
}

/// <summary>
/// Collects and compiles the <c>.gitignore</c> and <c>.gitattributes</c> files a scan applies
/// that a <see cref="ScanTreeWalker"/> pass does not find: the ones in the directories above
/// the scan root up to its repository's root (read from disk, or from a git commit's files),
/// and the ones among a snapshot's own entries. Every scan gathers such files here, so the
/// directory, snapshot, and single-file scans agree on which ones apply.
/// </summary>
/// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
/// <param name="collectGitignore">Whether to collect <c>.gitignore</c> files.</param>
/// <param name="collectGitattributes">Whether to collect <c>.gitattributes</c> files.</param>
internal sealed class GitRuleFiles(bool ignoreCase, bool collectGitignore, bool collectGitattributes)
{
    /// <summary>
    /// The file name of a <c>.gitignore</c> file.
    /// </summary>
    private const string GitignoreFileName = ".gitignore";

    /// <summary>
    /// The file name of a <c>.gitattributes</c> file.
    /// </summary>
    private const string GitattributesFileName = ".gitattributes";

    /// <summary>
    /// Gets the <c>.gitignore</c> files collected, in the order read.
    /// </summary>
    public List<GitIgnoreRules.GitIgnoreFile> GitignoreFiles { get; } = [];

    /// <summary>
    /// Gets the <c>.gitattributes</c> files collected, in the order read.
    /// </summary>
    public List<GitAttributesRules.AttributesFile> AttributesFiles { get; } = [];

    /// <summary>
    /// Gets an entry for every rule file that could not be read.
    /// </summary>
    public List<SkippedEntry> Skipped { get; } = [];

    /// <summary>
    /// Whether <paramref name="relativePath"/> names a rule file of a kind this instance collects.
    /// </summary>
    /// <param name="relativePath">A <c>/</c>-separated relative path with no leading/trailing slash.</param>
    /// <param name="directory">The directory holding the file, empty for the root.</param>
    /// <param name="kind">The kind of rule file.</param>
    /// <returns><see langword="true"/> if the file is a rule file to read.</returns>
    public bool TryGetRuleFile(string relativePath, out string directory, out RuleFileKind kind)
    {
        var slash = relativePath.LastIndexOf('/');
        var fileName = relativePath[(slash + 1)..];
        directory = slash < 0 ? string.Empty : relativePath[..slash];
        kind = fileName == GitignoreFileName ? RuleFileKind.Gitignore : RuleFileKind.Gitattributes;
        return (collectGitignore && fileName == GitignoreFileName)
            || (collectGitattributes && fileName == GitattributesFileName);
    }

    /// <summary>
    /// Reads and compiles the rule file stored at <paramref name="fullPath"/>, adding it to
    /// <see cref="GitignoreFiles"/> or <see cref="AttributesFiles"/>, or to
    /// <see cref="Skipped"/> if it cannot be read.
    /// </summary>
    /// <param name="fullPath">Where the file's content is stored.</param>
    /// <param name="directory">The directory holding the file, which its patterns are relative to.</param>
    /// <param name="kind">The kind of rule file.</param>
    public void Read(string fullPath, string directory, RuleFileKind kind)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skipped.Add(new SkippedEntry(fullPath, ex.Message));
            return;
        }

        if (kind == RuleFileKind.Gitignore)
        {
            if (GitIgnoreRules.CompilePatterns(lines, ignoreCase) is { Count: > 0 } ignorePatterns)
            {
                GitignoreFiles.Add(new GitIgnoreRules.GitIgnoreFile(GitIgnoreRules.NormalizeBase(directory), ignorePatterns, ignoreCase));
            }
        }
        else if (GitAttributesRules.ParseLines(lines) is { Count: > 0 } attributeLines)
        {
            AttributesFiles.Add(new GitAttributesRules.AttributesFile(GitAttributesRules.NormalizeBase(directory), attributeLines));
        }
    }

    /// <summary>
    /// Reads the rule files in each of <paramref name="directories"/> under
    /// <paramref name="root"/> on disk, skipping any that does not exist.
    /// </summary>
    /// <param name="root">The directory the <paramref name="directories"/> are relative to.</param>
    /// <param name="directories">The <c>/</c>-separated relative directories, empty for <paramref name="root"/> itself.</param>
    public void ReadFromDisk(string root, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            if (collectGitignore)
            {
                ReadIfPresent(Path.Combine(root, directory, GitignoreFileName), directory, RuleFileKind.Gitignore);
            }

            if (collectGitattributes)
            {
                ReadIfPresent(Path.Combine(root, directory, GitattributesFileName), directory, RuleFileKind.Gitattributes);
            }
        }
    }

    /// <summary>
    /// Reads the rule files in the directories above <paramref name="scanPrefix"/> in
    /// <paramref name="workTree"/> from disk (see <see cref="GitWorkTree.AncestorDirectories"/>),
    /// or nothing when the scan root is not inside a working tree.
    /// </summary>
    /// <param name="workTree">The working tree the scan root belongs to, if any.</param>
    /// <param name="scanPrefix">The scan root's <c>/</c>-separated repository-relative directory.</param>
    public void ReadAncestorsFromDisk(GitWorkTree? workTree, string scanPrefix)
    {
        if (workTree is not null)
        {
            ReadFromDisk(workTree.Root, GitWorkTree.AncestorDirectories(scanPrefix));
        }
    }

    /// <summary>
    /// Reads the rule files among <paramref name="files"/> that are held by one of
    /// <paramref name="directories"/>.
    /// </summary>
    /// <param name="files">The snapshot's files, with <c>/</c>-separated repository-relative paths.</param>
    /// <param name="directories">The repository-relative directories whose rule files to read.</param>
    public void ReadFromSnapshot(IEnumerable<SnapshotEntry> files, IReadOnlySet<string> directories)
    {
        foreach (var file in files)
        {
            var repositoryPath = file.RelativePath.Replace('\\', '/').Trim('/');
            if (TryGetRuleFile(repositoryPath, out var directory, out var kind) && directories.Contains(directory))
            {
                Read(file.FullPath, directory, kind);
            }
        }
    }

    /// <summary>
    /// Reads the rule file at <paramref name="path"/> if it exists.
    /// </summary>
    /// <param name="path">The rule file's path on disk.</param>
    /// <param name="directory">The directory holding the file, which its patterns are relative to.</param>
    /// <param name="kind">The kind of rule file.</param>
    private void ReadIfPresent(string path, string directory, RuleFileKind kind)
    {
        if (File.Exists(path))
        {
            Read(path, directory, kind);
        }
    }
}
