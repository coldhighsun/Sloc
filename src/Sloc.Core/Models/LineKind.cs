namespace Sloc.Core.Models;

/// <summary>
/// The classification of a single physical line of source code.
/// </summary>
public enum LineKind
{
    /// <summary>
    /// A line that is empty or contains only whitespace.
    /// </summary>
    Blank,

    /// <summary>
    /// A line that contains only a comment (and optional whitespace).
    /// </summary>
    Comment,

    /// <summary>
    /// A line that contains executable code (optionally with a trailing comment).
    /// </summary>
    Code
}
