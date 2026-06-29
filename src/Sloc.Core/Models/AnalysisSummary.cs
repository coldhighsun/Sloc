namespace Sloc.Core.Models;

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
    public AnalysisSummary(IReadOnlyList<FileAnalysis> files, IReadOnlyList<SkippedEntry>? skipped = null)
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

        ByLanguage = files
            .GroupBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LanguageStatistics
            {
                Language = group.Key,
                Files = group.Count(),
                Code = group.Sum(file => file.Code),
                Comment = group.Sum(file => file.Comment),
                Blank = group.Sum(file => file.Blank)
            })
            .OrderByDescending(stats => stats.Total)
            .ThenBy(stats => stats.Language, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
}