namespace Sloc.Cli.Analysis;

/// <summary>
/// The supported output formats.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// A human-readable, colored table.
    /// </summary>
    Table,

    /// <summary>
    /// Machine-readable JSON.
    /// </summary>
    Json,

    /// <summary>
    /// A human-readable HTML report.
    /// </summary>
    Html,

    /// <summary>
    /// Comma-separated values (spreadsheet-friendly).
    /// </summary>
    Csv,

    /// <summary>
    /// GitHub-Flavored Markdown tables (documentation-friendly).
    /// </summary>
    Markdown
}