namespace Sloc.Core.Models;

/// <summary>
/// The line statistics produced by analyzing a single source file.
/// </summary>
public sealed class FileAnalysis
{
    /// <summary>
    /// The number of blank lines.
    /// </summary>
    public int Blank
    {
        get; init;
    }

    /// <summary>
    /// The number of lines containing code.
    /// </summary>
    public int Code
    {
        get; init;
    }

    /// <summary>
    /// The number of lines containing only comments.
    /// </summary>
    public int Comment
    {
        get; init;
    }

    /// <summary>
    /// The display name of the language the file was analyzed as.
    /// </summary>
    public required string Language
    {
        get; init;
    }

    /// <summary>
    /// The path of the analyzed file.
    /// </summary>
    public required string Path
    {
        get; init;
    }

    /// <summary>
    /// A content hash of the file, populated only when requested (e.g. for
    /// <c>--unique</c> duplicate detection). <see langword="null"/> otherwise.
    /// </summary>
    public string? Hash
    {
        get; init;
    }

    /// <summary>
    /// The total number of physical lines in the file.
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
    /// The comment-health bucket for this file, based on its language and line counts.
    /// </summary>
    public CommentHealthLevel Health => CommentHealth.Classify(Language, Code, Comment);
}