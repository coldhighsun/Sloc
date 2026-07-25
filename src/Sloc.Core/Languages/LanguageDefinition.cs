namespace Sloc.Core.Languages;

/// <summary>
/// A pair of tokens that delimit a (potentially multi-line) block comment,
/// for example <c>/*</c> and <c>*/</c>.
/// </summary>
/// <param name="Open">The token that opens the block comment.</param>
/// <param name="Close">The token that closes the block comment.</param>
/// <param name="AllowNested">
/// Whether the block comment can nest (e.g. Rust and F#), so that an inner
/// <paramref name="Open"/> must be matched by an inner <paramref name="Close"/>
/// before the block ends.
/// </param>
/// <param name="RequireLineStart">
/// Whether the tokens are only recognized at the start of a line (ignoring leading
/// whitespace is <em>not</em> allowed — the token must be at column 0), as with Ruby's
/// <c>=begin</c>/<c>=end</c>.
/// </param>
public sealed record BlockComment(string Open, string Close, bool AllowNested = false, bool RequireLineStart = false);

/// <summary>
/// Describes a string-literal delimiter so that comment tokens appearing inside
/// strings are not misclassified as comments.
/// </summary>
/// <param name="Delimiter">The token that both opens and closes the literal (e.g. <c>"</c>).</param>
/// <param name="Multiline">
/// Whether the literal may span multiple physical lines (e.g. Python triple-quoted
/// strings, JavaScript template literals, Go raw strings).
/// </param>
/// <param name="IsDocComment">
/// Whether an occurrence that begins a statement (nothing but whitespace precedes it on
/// the line) should be counted as a comment rather than code, as with Python docstrings.
/// An occurrence used as an expression (e.g. the right-hand side of an assignment) is
/// always treated as a string.
/// </param>
/// <param name="AllowEscape">
/// Whether <paramref name="EscapeChar"/> escapes the following character inside the
/// literal. Set to <see langword="false"/> for raw strings (e.g. Go backtick strings).
/// </param>
/// <param name="EscapeChar">The escape character used inside the literal.</param>
public sealed record StringLiteral(
    string Delimiter,
    bool Multiline = false,
    bool IsDocComment = false,
    bool AllowEscape = true,
    char EscapeChar = '\\');

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
    /// The string-literal delimiters recognized so that comment tokens inside strings
    /// are not misclassified. When several delimiters share a prefix, list the longer
    /// ones first (e.g. <c>"""</c> before <c>"</c>).
    /// </summary>
    public IReadOnlyList<StringLiteral> StringLiterals { get; init; } = [];

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

    /// <summary>
    /// Gets a value indicating whether comment-health analysis is meaningful for this
    /// language: the language opts in via <see cref="ShowHealth"/> and actually defines
    /// at least one comment token.
    /// </summary>
    public bool SupportsHealth =>
        ShowHealth && (LineCommentTokens.Count > 0 || BlockComments.Count > 0);
}