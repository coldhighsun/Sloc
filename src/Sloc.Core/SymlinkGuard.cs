namespace Sloc.Core;

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

        target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isLoop = ancestors.Any(ancestor => IsAncestorOrSelf(target, ancestor));
        return new Resolution(Resolved: true, Target: target, IsLoop: isLoop);
    }

    /// <summary>
    /// Determines whether <paramref name="path"/> is <paramref name="ancestor"/> itself, or
    /// nested under it.
    /// </summary>
    public static bool IsAncestorOrSelf(string ancestor, string path)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
