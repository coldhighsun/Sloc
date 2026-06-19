namespace Sloc.Core.Languages;

/// <summary>
/// A pair of tokens that delimit a (potentially multi-line) block comment,
/// for example <c>/*</c> and <c>*/</c>.
/// </summary>
/// <param name="Open">The token that opens the block comment.</param>
/// <param name="Close">The token that closes the block comment.</param>
public sealed record BlockComment(string Open, string Close);

/// <summary>
/// Describes how a programming language expresses comments so that source
/// lines can be classified as code, comment, or blank.
/// </summary>
public sealed class LanguageDefinition
{
    /// <summary>
    /// The delimiter pairs that start and end block comments (e.g. <c>/*</c> … <c>*/</c>).
    /// </summary>
    public IReadOnlyList<BlockComment> BlockComments { get; init; } = [];

    /// <summary>
    /// The file extensions (including the leading dot, lower-case) that map to this language.
    /// </summary>
    public IReadOnlyList<string> Extensions
    {
        get; init;
    } = [];

    /// <summary>
    /// The tokens that start a single-line comment (e.g. <c>//</c> or <c>#</c>).
    /// </summary>
    public IReadOnlyList<string> LineCommentTokens { get; init; } = [];

    /// <summary>
    /// The human-readable name of the language (e.g. "C#").
    /// </summary>
    public required string Name
    {
        get; init;
    }

    /// <summary>
    /// Gets a value indicating whether the Health metric should be displayed for this language.
    /// Set to <see langword="false"/> for markup and data languages (e.g. YAML, JSON, XML)
    /// where comment-density analysis is not meaningful.
    /// </summary>
    public bool ShowHealth { get; init; } = true;
}