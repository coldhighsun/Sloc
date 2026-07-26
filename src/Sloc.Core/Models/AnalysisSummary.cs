namespace Sloc.Core.Models;

/// <summary>
/// The key by which per-language statistics can be ordered.
/// </summary>
public enum LanguageSort
{
    /// <summary>
    /// Order by total physical lines.
    /// </summary>
    Total,

    /// <summary>
    /// Order by code lines.
    /// </summary>
    Code,

    /// <summary>
    /// Order by comment lines.
    /// </summary>
    Comment,

    /// <summary>
    /// Order by blank lines.
    /// </summary>
    Blank,

    /// <summary>
    /// Order by file count.
    /// </summary>
    Files,

    /// <summary>
    /// Order alphabetically by language name.
    /// </summary>
    Name
}

/// <summary>
/// Aggregated line statistics for a single language across many files.
/// </summary>
public sealed class LanguageStatistics
{
    /// <summary>
    /// The total number of blank lines for this language.
    /// </summary>
    public int Blank
    {
        get; init;
    }

    /// <summary>
    /// The total number of code lines for this language.
    /// </summary>
    public int Code
    {
        get; init;
    }

    /// <summary>
    /// The total number of comment lines for this language.
    /// </summary>
    public int Comment
    {
        get; init;
    }

    /// <summary>
    /// The number of files counted for this language.
    /// </summary>
    public int Files
    {
        get; init;
    }

    /// <summary>
    /// The display name of the language.
    /// </summary>
    public required string Language
    {
        get; init;
    }

    /// <summary>
    /// The total number of physical lines for this language.
    /// </summary>
    public int Total => Code + Comment + Blank;

    /// <summary>
    /// The percentage (0-100) of physical lines that are code.
    /// </summary>
    public double CodePct => Total == 0 ? 0.0 : (double)Code / Total * 100;

    /// <summary>
    /// The percentage (0-100) of physical lines that are comments.
    /// </summary>
    public double CommentPct => Total == 0 ? 0.0 : (double)Comment / Total * 100;

    /// <summary>
    /// The percentage (0-100) of physical lines that are blank.
    /// </summary>
    public double BlankPct => Total == 0 ? 0.0 : (double)Blank / Total * 100;

    /// <summary>
    /// The comment-health bucket for this language, based on its aggregated line counts.
    /// </summary>
    public CommentHealthLevel Health => CommentHealth.Classify(Language, Code, Comment);
}

/// <summary>
/// The complete result of an analysis run: per-file results, per-language
/// aggregates, and overall totals.
/// </summary>
public sealed class AnalysisSummary
{
    /// <summary>
    /// Builds a summary by aggregating the supplied per-file results.
    /// </summary>
    /// <param name="files">The per-file analysis results to aggregate.</param>
    /// <param name="skipped">Entries that were skipped due to read errors.</param>
    /// <param name="sortBy">The key by which to order the per-language statistics.</param>
    /// <param name="descending">Whether to order the sort key in descending order.</param>
    /// <param name="top">When set, keeps only the first this-many languages after sorting.</param>
    public AnalysisSummary(
        IReadOnlyList<FileAnalysis> files,
        IReadOnlyList<SkippedEntry>? skipped = null,
        LanguageSort sortBy = LanguageSort.Total,
        bool descending = true,
        int? top = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        Files = files;
        Skipped = skipped ?? [];
        FileCount = files.Count;

        var code = 0;
        var comment = 0;
        var blank = 0;
        foreach (var file in files)
        {
            code += file.Code;
            comment += file.Comment;
            blank += file.Blank;
        }

        Code = code;
        Comment = comment;
        Blank = blank;

        var grouped = files
            .GroupBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LanguageStatistics
            {
                Language = group.Key,
                Files = group.Count(),
                Code = group.Sum(file => file.Code),
                Comment = group.Sum(file => file.Comment),
                Blank = group.Sum(file => file.Blank)
            });

        ByLanguage = OrderAndLimit(grouped, sortBy, descending, top);
    }

    /// <summary>
    /// Orders per-language statistics the same way the final summary does, so live/progress
    /// displays built from <see cref="LanguageStatistics"/> snapshots match the final output.
    /// </summary>
    /// <param name="languages">The per-language statistics to order.</param>
    /// <param name="sortBy">The key by which to order the statistics.</param>
    /// <param name="descending">Whether to order the sort key in descending order.</param>
    /// <param name="top">When set, keeps only the first this-many languages after sorting.</param>
    public static IReadOnlyList<LanguageStatistics> OrderAndLimit(
        IEnumerable<LanguageStatistics> languages,
        LanguageSort sortBy = LanguageSort.Total,
        bool descending = true,
        int? top = null)
    {
        var sorted = Order(languages, sortBy, descending);
        if (top is { } limit && limit >= 0)
        {
            sorted = sorted.Take(limit);
        }

        return sorted.ToList();
    }

    private static IEnumerable<LanguageStatistics> Order(
        IEnumerable<LanguageStatistics> languages,
        LanguageSort sortBy,
        bool descending)
    {
        if (sortBy == LanguageSort.Name)
        {
            return descending
                ? languages.OrderByDescending(stats => stats.Language, StringComparer.OrdinalIgnoreCase)
                : languages.OrderBy(stats => stats.Language, StringComparer.OrdinalIgnoreCase);
        }

        Func<LanguageStatistics, int> key = sortBy switch
        {
            LanguageSort.Code => stats => stats.Code,
            LanguageSort.Comment => stats => stats.Comment,
            LanguageSort.Blank => stats => stats.Blank,
            LanguageSort.Files => stats => stats.Files,
            _ => stats => stats.Total
        };

        var ordered = descending
            ? languages.OrderByDescending(key)
            : languages.OrderBy(key);
        return ordered.ThenBy(stats => stats.Language, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a summary from already-aggregated per-language statistics, without any
    /// per-file results. Intended for live/progress displays where re-aggregating every
    /// file on each refresh would be wasteful; <see cref="Files"/> is empty.
    /// </summary>
    /// <param name="byLanguage">The pre-aggregated per-language statistics.</param>
    /// <param name="fileCount">The number of files represented by the aggregates.</param>
    /// <param name="skipped">Entries that were skipped due to read errors.</param>
    public AnalysisSummary(IReadOnlyList<LanguageStatistics> byLanguage, int fileCount, IReadOnlyList<SkippedEntry>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(byLanguage);

        Files = [];
        Skipped = skipped ?? [];
        FileCount = fileCount;

        var code = 0;
        var comment = 0;
        var blank = 0;
        foreach (var language in byLanguage)
        {
            code += language.Code;
            comment += language.Comment;
            blank += language.Blank;
        }

        Code = code;
        Comment = comment;
        Blank = blank;
        ByLanguage = byLanguage;
    }

    /// <summary>
    /// The total number of blank lines across all files.
    /// </summary>
    public int Blank
    {
        get;
    }

    /// <summary>
    /// The aggregated statistics grouped by language, ordered by total lines descending.
    /// </summary>
    public IReadOnlyList<LanguageStatistics> ByLanguage
    {
        get;
    }

    /// <summary>
    /// The total number of code lines across all files.
    /// </summary>
    public int Code
    {
        get;
    }

    /// <summary>
    /// The total number of comment lines across all files.
    /// </summary>
    public int Comment
    {
        get;
    }

    /// <summary>
    /// The total number of files analyzed.
    /// </summary>
    public int FileCount
    {
        get;
    }

    /// <summary>
    /// The individual file results, in the order they were analyzed.
    /// </summary>
    public IReadOnlyList<FileAnalysis> Files
    {
        get;
    }

    /// <summary>
    /// Entries that were skipped due to read errors during scanning or analysis.
    /// </summary>
    public IReadOnlyList<SkippedEntry> Skipped
    {
        get;
    }

    /// <summary>
    /// The total number of physical lines across all files.
    /// </summary>
    public int Total => Code + Comment + Blank;

    /// <summary>
    /// The percentage (0-100) of physical lines that are code.
    /// </summary>
    public double CodePct => Total == 0 ? 0.0 : (double)Code / Total * 100;

    /// <summary>
    /// The percentage (0-100) of physical lines that are comments.
    /// </summary>
    public double CommentPct => Total == 0 ? 0.0 : (double)Comment / Total * 100;

    /// <summary>
    /// The percentage (0-100) of physical lines that are blank.
    /// </summary>
    public double BlankPct => Total == 0 ? 0.0 : (double)Blank / Total * 100;
}