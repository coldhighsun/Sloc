using Sloc.Core.Models;

namespace Sloc.Cli.Analysis;

/// <summary>
/// The parsed options for an analysis run.
/// </summary>
public sealed class AnalyzeOptions
{
    /// <summary>
    /// When set, a previously saved JSON report to compare the current run against; the
    /// output becomes a diff of line counts rather than the normal report.
    /// </summary>
    public string? BaselinePath
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, a per-file breakdown is shown.
    /// </summary>
    public bool ByFile
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, JSON and HTML output includes both the by-language
    /// summary and the per-file breakdown together (only meaningful for those formats).
    /// </summary>
    public bool Detailed
    {
        get; init;
    }

    /// <summary>
    /// Language display names to exclude (e.g. <c>"Markdown"</c>).
    /// </summary>
    public IReadOnlyList<string> ExcludeLangs { get; init; } = [];

    /// <summary>
    /// Glob patterns of files to exclude.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// Whether to descend into symlinked/junctioned directories rather than skip them.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool FollowSymlinks
    {
        get; init;
    }

    /// <summary>
    /// The output format.
    /// </summary>
    public OutputFormat Format
    {
        get; init;
    }

    /// <summary>
    /// When set, a commit/tree-ish to analyze the repository tree of as it existed at
    /// that commit, without checking it out. <see cref="Path"/> is used as the repo root
    /// to query. Mutually exclusive with <see cref="ListFile"/>.
    /// </summary>
    public string? GitHash
    {
        get; init;
    }

    /// <summary>
    /// Language display names to include (e.g. <c>"C#"</c>). When empty, all languages
    /// are considered.
    /// </summary>
    public IReadOnlyList<string> IncludeLangs { get; init; } = [];

    /// <summary>
    /// Glob patterns of files to include.
    /// </summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, files with unknown extensions are included.
    /// </summary>
    public bool IncludeUnknown
    {
        get; init;
    }

    /// <summary>
    /// The maximum number of files to analyze in parallel. When <see langword="null"/>
    /// or non-positive, <see cref="Environment.ProcessorCount"/> is used. Set to 1 for
    /// fully sequential analysis.
    /// </summary>
    public int? Jobs
    {
        get; init;
    }

    /// <summary>
    /// When set, a file listing paths (one per line) to analyze directly instead of
    /// scanning <see cref="Path"/>. <c>-</c> reads the list from stdin.
    /// </summary>
    public string? ListFile
    {
        get; init;
    }

    /// <summary>
    /// When set, the run fails (returns <see cref="ExitCode.ThresholdNotMet"/>) if the
    /// overall comment percentage (comment lines / total lines) is below this value.
    /// </summary>
    public double? MinCommentPct
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, the Complexity column/field is hidden.
    /// </summary>
    public bool NoComplexity
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, the Comment Health column and percentage
    /// breakdowns are hidden.
    /// </summary>
    public bool NoHealth
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, suppresses the live table and progress bar while
    /// still printing the banner and result.
    /// </summary>
    public bool NoProgress
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, subdirectories are not scanned.
    /// </summary>
    public bool NoRecursive
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, skips the GitHub check for a newer release.
    /// </summary>
    public bool NoUpdateCheck
    {
        get; init;
    }

    /// <summary>
    /// The output file path for Json and Html formats.
    /// When <see langword="null"/>, a default name is used.
    /// </summary>
    public string? OutputFile
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, the per-file console output is paginated.
    /// </summary>
    public bool Paged
    {
        get; init;
    }

    /// <summary>
    /// The file or directory to analyze.
    /// </summary>
    public required string Path
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, suppresses the version banner, progress UI, and the
    /// "Saved to" message, leaving only the result output.
    /// </summary>
    public bool Quiet
    {
        get; init;
    }

    /// <summary>
    /// Whether to exclude files marked <c>linguist-vendored</c> or <c>linguist-generated</c>
    /// in <c>.gitattributes</c> files discovered under the scan root. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool RespectGitAttributes { get; init; } = true;

    /// <summary>
    /// Whether to honor <c>.gitignore</c> files discovered under the scan root.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool RespectGitignore { get; init; } = true;

    /// <summary>
    /// The key by which the per-language summary is ordered.
    /// </summary>
    public LanguageSort Sort { get; init; } = LanguageSort.Total;

    /// <summary>
    /// When set, keeps only the first this-many languages in the summary after sorting.
    /// </summary>
    public int? Top
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, files with identical content are only counted once;
    /// later duplicates are reported as skipped rather than double-counted.
    /// </summary>
    public bool Unique
    {
        get; init;
    }
}