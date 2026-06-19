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
    /// The total number of physical lines in the file.
    /// </summary>
    public int Total => Code + Comment + Blank;
}