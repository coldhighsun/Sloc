namespace Sloc.Core.Scanning;

/// <summary>
/// The single source of truth for turning a resolved <c>ignoreCase</c> flag (git's
/// <c>core.ignoreCase</c>) into the ordinal <see cref="StringComparison"/>/<see cref="StringComparer"/>
/// the rest of the scanning code compares paths and names with.
/// </summary>
internal static class IgnoreCaseComparer
{
    /// <summary>
    /// The <see cref="StringComparison"/> matching <paramref name="ignoreCase"/>.
    /// </summary>
    /// <param name="ignoreCase">Whether comparisons should be case-insensitive.</param>
    public static StringComparison Comparison(bool ignoreCase) =>
        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The <see cref="StringComparer"/> matching <paramref name="ignoreCase"/>.
    /// </summary>
    /// <param name="ignoreCase">Whether comparisons should be case-insensitive.</param>
    public static StringComparer Comparer(bool ignoreCase) =>
        ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
