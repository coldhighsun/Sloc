using Sloc.Core.Languages;

namespace Sloc.Core.Models;

/// <summary>
/// A qualitative comment-density bucket derived from the ratio of comment lines
/// to non-blank (code + comment) lines.
/// </summary>
public enum CommentHealthLevel
{
    /// <summary>
    /// Comment health is not meaningful for the language, or there are no
    /// code/comment lines to measure.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// No comment lines at all.
    /// </summary>
    None,

    /// <summary>
    /// Very few comments (density below 5%).
    /// </summary>
    Low,

    /// <summary>
    /// A modest amount of comments (density below 10%).
    /// </summary>
    Fair,

    /// <summary>
    /// A healthy amount of comments (density below 25%).
    /// </summary>
    Good,

    /// <summary>
    /// A high amount of comments (density below 40%).
    /// </summary>
    High,

    /// <summary>
    /// A very high amount of comments (density at or above 40%).
    /// </summary>
    Dense
}

/// <summary>
/// Computes the <see cref="CommentHealthLevel"/> for a set of line counts. This is the
/// single source of truth for the comment-density formula shared by all renderers.
/// </summary>
public static class CommentHealth
{
    /// <summary>
    /// Classifies comment health for a language and its code/comment line counts.
    /// </summary>
    /// <param name="language">The language display name (e.g. "C#").</param>
    /// <param name="code">The number of code lines.</param>
    /// <param name="comment">The number of comment lines.</param>
    /// <returns>
    /// The corresponding <see cref="CommentHealthLevel"/>, or
    /// <see cref="CommentHealthLevel.NotApplicable"/> when the language does not support
    /// health analysis or there are no non-blank lines.
    /// </returns>
    public static CommentHealthLevel Classify(string language, int code, int comment) =>
        Classify(LanguageRegistry.SupportsHealth(language), code, comment);

    /// <summary>
    /// Classifies comment health from an explicit support flag and code/comment line
    /// counts. Use this overload for aggregates that span multiple languages, where the
    /// caller determines support (e.g. "any contributing language supports health").
    /// </summary>
    /// <param name="supported">Whether comment-health analysis is meaningful here.</param>
    /// <param name="code">The number of code lines.</param>
    /// <param name="comment">The number of comment lines.</param>
    /// <returns>
    /// The corresponding <see cref="CommentHealthLevel"/>, or
    /// <see cref="CommentHealthLevel.NotApplicable"/> when unsupported or there are no
    /// non-blank lines.
    /// </returns>
    public static CommentHealthLevel Classify(bool supported, int code, int comment)
    {
        if (!supported)
        {
            return CommentHealthLevel.NotApplicable;
        }

        var codeAndComment = code + comment;
        if (codeAndComment == 0)
        {
            return CommentHealthLevel.NotApplicable;
        }

        var density = (double)comment / codeAndComment;
        return density switch
        {
            <= 0.0 => CommentHealthLevel.None,
            < 0.05 => CommentHealthLevel.Low,
            < 0.10 => CommentHealthLevel.Fair,
            < 0.25 => CommentHealthLevel.Good,
            < 0.40 => CommentHealthLevel.High,
            _ => CommentHealthLevel.Dense
        };
    }
}
