namespace Sloc.Cli;

/// <summary>
/// Validates numeric CLI arguments that <c>System.CommandLine</c>'s parsing alone cannot
/// range-check (e.g. a plausible but out-of-range double or int).
/// </summary>
internal static class CliArgumentValidation
{
    /// <summary>
    /// Determines whether a <c>--min-comment-pct</c> value is within the valid 0–100 range.
    /// A <see langword="null"/> value (the option was not supplied) is always valid.
    /// </summary>
    /// <param name="value">The parsed <c>--min-comment-pct</c> value, if any.</param>
    /// <returns><see langword="true"/> if the value is valid; otherwise <see langword="false"/>.</returns>
    public static bool IsValidMinCommentPct(double? value) =>
        value is null || (value >= 0 && value <= 100);

    /// <summary>
    /// Determines whether a <c>--top</c> value is 1 or greater. A <see langword="null"/>
    /// value (the option was not supplied) is always valid.
    /// </summary>
    /// <param name="value">The parsed <c>--top</c> value, if any.</param>
    /// <returns><see langword="true"/> if the value is valid; otherwise <see langword="false"/>.</returns>
    public static bool IsValidTop(int? value) =>
        value is null || value >= 1;
}
