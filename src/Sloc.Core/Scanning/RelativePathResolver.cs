namespace Sloc.Core.Scanning;

/// <summary>
/// Shared "make a repository-relative path relative to a rule file's own base directory"
/// logic, used identically by <see cref="GitIgnoreRules"/> and <see cref="GitAttributesRules"/>
/// for their nested <c>.gitignore</c>/<c>.gitattributes</c> files.
/// </summary>
internal static class RelativePathResolver
{
    /// <summary>
    /// Rebases <paramref name="path"/> (relative to the repository root, or the scan root
    /// outside a repository, <c>/</c>-separated) onto <paramref name="baseDirectory"/> (relative
    /// to the same root, <c>/</c>-separated, with no leading/trailing slash), the directory a
    /// rule file was discovered in.
    /// </summary>
    /// <param name="baseDirectory">The rule file's base directory, or <see cref="string.Empty"/> for the root.</param>
    /// <param name="path">The path to rebase.</param>
    /// <param name="ignoreCase">
    /// Whether the nesting check matches case-insensitively (git's <c>core.ignoreCase</c>),
    /// matching the case-sensitivity the rule file's own patterns are compiled with.
    /// </param>
    /// <param name="relativeToBase">The path relative to <paramref name="baseDirectory"/>, if it is nested under it.</param>
    /// <returns><see langword="true"/> if <paramref name="path"/> is <paramref name="baseDirectory"/> or nested under it.</returns>
    public static bool TryGetRelativePath(string baseDirectory, string path, bool ignoreCase, out string relativeToBase)
    {
        if (baseDirectory.Length == 0)
        {
            relativeToBase = path;
            return true;
        }

        if (path.Length > baseDirectory.Length
            && path.StartsWith(baseDirectory, IgnoreCaseComparer.Comparison(ignoreCase))
            && path[baseDirectory.Length] == '/')
        {
            relativeToBase = path[(baseDirectory.Length + 1)..];
            return true;
        }

        relativeToBase = string.Empty;
        return false;
    }

    /// <summary>
    /// Joins two <c>/</c>-separated relative paths, either of which may be empty.
    /// </summary>
    /// <param name="directory">The leading directory, empty for none.</param>
    /// <param name="path">The path below <paramref name="directory"/>, empty for the directory itself.</param>
    public static string Combine(string directory, string path) =>
        directory.Length == 0 ? path
        : path.Length == 0 ? directory
        : directory + "/" + path;
}
