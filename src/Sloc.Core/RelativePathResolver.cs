namespace Sloc.Core;

/// <summary>
/// Shared "make a scan-root-relative path relative to a rule file's own base directory"
/// logic, used identically by <see cref="GitIgnoreRules"/> and <see cref="GitAttributesRules"/>
/// for their nested <c>.gitignore</c>/<c>.gitattributes</c> files.
/// </summary>
internal static class RelativePathResolver
{
    /// <summary>
    /// Rebases <paramref name="path"/> (already relative to the scan root, <c>/</c>-separated)
    /// onto <paramref name="baseDirectory"/> (also scan-root-relative, <c>/</c>-separated, with
    /// no leading/trailing slash), the directory a rule file was discovered in.
    /// </summary>
    /// <param name="baseDirectory">The rule file's base directory, or <see cref="string.Empty"/> for the scan root.</param>
    /// <param name="path">The scan-root-relative path to rebase.</param>
    /// <param name="relativeToBase">The path relative to <paramref name="baseDirectory"/>, if it is nested under it.</param>
    /// <returns><see langword="true"/> if <paramref name="path"/> is <paramref name="baseDirectory"/> or nested under it.</returns>
    public static bool TryGetRelativePath(string baseDirectory, string path, out string relativeToBase)
    {
        if (baseDirectory.Length == 0)
        {
            relativeToBase = path;
            return true;
        }

        if (path.Length > baseDirectory.Length
            && path.StartsWith(baseDirectory, StringComparison.Ordinal)
            && path[baseDirectory.Length] == '/')
        {
            relativeToBase = path[(baseDirectory.Length + 1)..];
            return true;
        }

        relativeToBase = string.Empty;
        return false;
    }
}
