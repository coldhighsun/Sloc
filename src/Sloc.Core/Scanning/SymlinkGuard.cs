namespace Sloc.Core.Scanning;

/// <summary>
/// Shared directory-symlink/junction loop detection, used by both
/// <see cref="DirectoryScanner"/> and <see cref="GitIgnoreRules"/> while walking a directory
/// tree, so a symlink chain that loops back onto one of its own ancestors is recognized the
/// same way regardless of which of the two callers encounters it first.
/// </summary>
internal static class SymlinkGuard
{
    /// <summary>
    /// The outcome of resolving a directory symlink/junction's target for loop detection.
    /// </summary>
    /// <param name="Resolved">
    /// <see langword="true"/> if the target could be resolved; <see langword="false"/> if
    /// resolution failed or the target is unknown, in which case the symlink should always
    /// be excluded from traversal.
    /// </param>
    /// <param name="Target">The resolved target's full path, when <paramref name="Resolved"/>.</param>
    /// <param name="IsLoop">
    /// <see langword="true"/> if following the target would loop back onto a directory
    /// already on the current path from the scan root (directly, or transitively through an
    /// earlier followed symlink).
    /// </param>
    public readonly record struct Resolution(bool Resolved, string? Target, bool IsLoop);

    /// <summary>
    /// Determines whether <paramref name="entry"/> is a symbolic link or junction, as opposed
    /// to a plain file/directory or one carrying some other kind of reparse point.
    /// </summary>
    /// <param name="entry">The file or directory to inspect.</param>
    /// <exception cref="IOException">The entry's attributes or link target could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the entry was denied.</exception>
    public static bool IsLink(FileSystemInfo entry) =>
        IsLink(entry.Attributes, () => entry.LinkTarget);

    /// <summary>
    /// Determines whether an entry with <paramref name="attributes"/> is a symbolic link or
    /// junction. The reparse-point attribute alone is not enough: Windows also sets it on
    /// ordinary files and folders managed by a filesystem filter, such as OneDrive
    /// Files-On-Demand placeholders (cloud files) or deduplicated files, which have no link
    /// target and must be scanned like any other file or directory. Only a reparse point
    /// with a link target (<see cref="FileSystemInfo.LinkTarget"/>) is a link.
    /// </summary>
    /// <param name="attributes">The entry's attributes.</param>
    /// <param name="getLinkTarget">
    /// Reads the entry's link target; only called for reparse points, since reading it costs
    /// an extra filesystem call.
    /// </param>
    public static bool IsLink(FileAttributes attributes, Func<string?> getLinkTarget) =>
        attributes.HasFlag(FileAttributes.ReparsePoint) && getLinkTarget() is not null;

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, which the
    // FileAttributes enum doesn't name.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    /// Determines whether a file with <paramref name="attributes"/> is an online-only
    /// placeholder (e.g. OneDrive Files-On-Demand) whose content isn't stored locally, so
    /// reading it would make the sync client download it.
    /// </summary>
    /// <param name="attributes">The file's attributes.</param>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>
    /// Whether <paramref name="directory"/> can be listed, used to tell a reparse-point
    /// directory the scan can walk (e.g. a OneDrive folder) from one it can't (e.g. a WSL
    /// LX symlink).
    /// </summary>
    /// <param name="directory">The directory to probe.</param>
    public static bool CanEnumerate(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves <paramref name="subdirectory"/>'s symlink/junction target and checks it
    /// against <paramref name="ancestors"/> for a loop.
    /// </summary>
    /// <param name="subdirectory">The directory symlink/junction to resolve.</param>
    /// <param name="ancestors">
    /// The full paths of directories already on the current path from the scan root
    /// (including any earlier followed symlinks' targets).
    /// </param>
    public static Resolution Resolve(string subdirectory, IReadOnlyList<string> ancestors)
    {
        string? target;
        try
        {
            target = Directory.ResolveLinkTarget(subdirectory, returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Resolution(Resolved: false, Target: null, IsLoop: false);
        }

        if (target is null)
        {
            return new Resolution(Resolved: false, Target: null, IsLoop: false);
        }

        target = Path.TrimEndingDirectorySeparator(target);
        var isLoop = ancestors.Any(ancestor => IsAncestorOrSelf(ancestor, target));
        return new Resolution(Resolved: true, Target: target, IsLoop: isLoop);
    }

    /// <summary>
    /// Determines whether <paramref name="path"/> is <paramref name="ancestor"/> itself, or
    /// nested under it.
    /// </summary>
    public static bool IsAncestorOrSelf(string ancestor, string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedAncestor = Path.TrimEndingDirectorySeparator(ancestor);

        // A filesystem root (e.g. "C:\" or "/") keeps its trailing separator, so it
        // already ends with the separator a nested path continues with.
        var ancestorPrefix = Path.EndsInDirectorySeparator(normalizedAncestor)
            ? normalizedAncestor
            : normalizedAncestor + Path.DirectorySeparatorChar;

        return normalizedPath.Equals(normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(ancestorPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
