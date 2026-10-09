using Sloc.Core.Models;

namespace Sloc.Cli.Output;

/// <summary>
/// Shared column-header and optional-column helpers for the plain tabular renderers
/// (<see cref="CsvRenderer"/> and <see cref="MarkdownRenderer"/>), whose "Health" and
/// "Complexity" columns are both toggled by the same <c>noHealth</c>/<c>noComplexity</c>
/// flags and otherwise share an identical base column set.
/// </summary>
internal static class ColumnLayout
{
    /// <summary>
    /// The per-file table header: <c>Path, Language, Code, Comment, Blank, Total</c>, plus
    /// <paramref name="healthLabel"/>/<paramref name="complexityLabel"/> when enabled.
    /// </summary>
    public static List<string> FileHeader(bool noHealth, bool noComplexity, string healthLabel = "Health", string complexityLabel = "Complexity")
    {
        var header = new List<string> { "Path", "Language", "Code", "Comment", "Blank", "Total" };
        AddOptional(header, noHealth, noComplexity, healthLabel, complexityLabel);
        return header;
    }

    /// <summary>
    /// The per-language table header: <c>Language, Files, Code, Comment, Blank, Total</c>, plus
    /// <paramref name="healthLabel"/>/<paramref name="complexityLabel"/> when enabled.
    /// </summary>
    public static List<string> LanguageHeader(bool noHealth, bool noComplexity, string healthLabel = "Health", string complexityLabel = "Complexity")
    {
        var header = new List<string> { "Language", "Files", "Code", "Comment", "Blank", "Total" };
        AddOptional(header, noHealth, noComplexity, healthLabel, complexityLabel);
        return header;
    }

    /// <summary>
    /// Appends <paramref name="healthCell"/>/<paramref name="complexityCell"/> to
    /// <paramref name="row"/> when the corresponding column is enabled, mirroring the
    /// header built by <see cref="FileHeader"/>/<see cref="LanguageHeader"/>.
    /// </summary>
    public static void AddOptional(List<string> row, bool noHealth, bool noComplexity, string healthCell, string complexityCell)
    {
        if (!noHealth)
        {
            row.Add(healthCell);
        }

        if (!noComplexity)
        {
            row.Add(complexityCell);
        }
    }

    /// <summary>
    /// Describes a <c>--top</c>-truncated language table, whose Total row still covers every
    /// language, so a reader does not take the listed rows for the whole.
    /// </summary>
    /// <param name="summary">The summary whose language table is rendered.</param>
    /// <returns>The note, or <see langword="null"/> when every language is listed.</returns>
    public static string? TruncationNote(AnalysisSummary summary) =>
        summary.ByLanguageTruncated
            ? $"Showing top {summary.ByLanguage.Count} of {summary.LanguageCount} languages; totals cover all languages."
            : null;

    /// <summary>
    /// Renders a comment-health bucket as plain text, or an empty cell when the language
    /// does not support comment-health classification.
    /// </summary>
    public static string HealthCell(CommentHealthLevel health) =>
        health == CommentHealthLevel.NotApplicable ? string.Empty : health.ToString();
}
