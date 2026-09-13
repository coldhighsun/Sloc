using Sloc.Cli.Analysis;

namespace Sloc.Cli.Parsing;

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
        value is null or >= 0 and <= 100;

    /// <summary>
    /// Determines whether a <c>--top</c> value is 1 or greater. A <see langword="null"/>
    /// value (the option was not supplied) is always valid.
    /// </summary>
    /// <param name="value">The parsed <c>--top</c> value, if any.</param>
    /// <returns><see langword="true"/> if the value is valid; otherwise <see langword="false"/>.</returns>
    public static bool IsValidTop(int? value) =>
        value is null or >= 1;

    /// <summary>
    /// Validates <c>--watch</c>'s mutual exclusivity with <c>--git-hash</c>, <c>--list-file</c>,
    /// non-Table formats, and <c>--baseline</c>.
    /// </summary>
    /// <returns>
    /// An error message describing the first violated constraint, or <see langword="null"/>
    /// when <paramref name="watch"/> is <see langword="false"/> or no constraint is violated.
    /// </returns>
    public static string? ValidateWatch(bool watch, string? listFile, string? gitHash, OutputFormat format, string? baselinePath)
    {
        if (!watch)
        {
            return null;
        }

        if (listFile is not null || gitHash is not null)
        {
            return "sloc: --watch cannot be used with --git-hash or --list-file.";
        }

        if (format != OutputFormat.Table)
        {
            return "sloc: --watch only supports Table format.";
        }

        return baselinePath is not null ? "sloc: --watch cannot be used with --baseline." : null;
    }
}