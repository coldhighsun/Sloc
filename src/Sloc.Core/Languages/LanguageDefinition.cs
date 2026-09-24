using System.Buffers;
using System.Text.RegularExpressions;

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
/// <param name="CloseDelimiter">
/// The token that closes the literal, when different from <paramref name="Delimiter"/>
/// (e.g. C# verbatim strings open with <c>@"</c> but close with <c>"</c>, and Rust
/// hash-delimited raw strings open with <c>r#"</c> but close with <c>"#</c>). Defaults to
/// <paramref name="Delimiter"/> when not set.
/// </param>
/// <param name="DoubledClosingEscape">
/// Whether a doubled closing delimiter inside the literal is an escaped literal
/// occurrence rather than the end of the string, as with C# verbatim strings
/// (<c>@"a""b"</c> is the single string <c>a"b</c>).
/// </param>
public sealed record StringLiteral(
    string Delimiter,
    bool Multiline = false,
    bool IsDocComment = false,
    bool AllowEscape = true,
    char EscapeChar = '\\',
    string? CloseDelimiter = null,
    bool DoubledClosingEscape = false)
{
    /// <summary>
    /// The token that closes the literal (<see cref="CloseDelimiter"/> if set, otherwise
    /// <see cref="Delimiter"/>).
    /// </summary>
    public string Closing => CloseDelimiter ?? Delimiter;
}

/// <summary>
/// Describes how a programming language expresses comments so that source
/// lines can be classified as code, comment, or blank.
/// </summary>
public sealed class LanguageDefinition
{
    private Dictionary<char, BlockComment[]>? _blockCommentsByFirstChar;

    private Dictionary<char, string[]>? _lineCommentsByFirstChar;

    private Dictionary<char, StringLiteral[]>? _stringLiteralsByFirstChar;

    private Regex? _complexityRegex;

    private SearchValues<char>? _tokenStartChars;

    /// <summary>
    /// The delimiter pairs that start and end block comments (e.g. <c>/*</c> … <c>*/</c>).
    /// </summary>
    public IReadOnlyList<BlockComment> BlockComments { get; init; } = [];

    /// <summary>
    /// Whether <see cref="LineCommentTokens"/> are matched case-insensitively, as with
    /// Batch's <c>REM</c>.
    /// </summary>
    public bool CaseInsensitiveLineComments
    {
        get; init;
    }

    /// <summary>
    /// The file extensions (including the leading dot, lower-case) that map to this language.
    /// </summary>
    public IReadOnlyList<string> Extensions
    {
        get; init;
    } = [];

    /// <summary>
    /// The exact file names that map to this language, for files identified by name rather
    /// than extension (e.g. <c>Makefile</c>, <c>Dockerfile</c>, <c>CMakeLists.txt</c>).
    /// Matched case-insensitively unless <see cref="CaseSensitiveFilenames"/> is set.
    /// Checked before <see cref="FilenameSuffixes"/> and <see cref="Extensions"/>.
    /// </summary>
    public IReadOnlyList<string> Filenames { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Filenames"/> must match exactly, including case, as with Bazel's
    /// <c>BUILD</c>, whose lower-case spelling is a common name for unrelated build scripts.
    /// </summary>
    public bool CaseSensitiveFilenames
    {
        get; init;
    }

    /// <summary>
    /// The file name suffixes (case-insensitive, with leading dot, e.g. <c>.designer.cs</c>)
    /// that map to this language, for files identified by a compound suffix that is more
    /// specific than their plain extension (e.g. <c>Form1.Designer.cs</c>, which would
    /// otherwise resolve to C# via its <c>.cs</c> extension). Checked before
    /// <see cref="Extensions"/> when resolving a file path.
    /// </summary>
    public IReadOnlyList<string> FilenameSuffixes { get; init; } = [];

    /// <summary>
    /// The tokens that count as a branch point for the simplified cyclomatic-complexity
    /// metric (e.g. <c>if</c>, <c>for</c>, <c>&amp;&amp;</c>, <c>?:</c>). Tokens consisting
    /// entirely of letters are matched as whole words; other tokens are matched literally.
    /// Empty for languages where this metric is not meaningful (see
    /// <see cref="SupportsComplexity"/>).
    /// </summary>
    public IReadOnlyList<string> ComplexityKeywords { get; init; } = [];

    /// <summary>
    /// The tokens that start a single-line comment (e.g. <c>//</c> or <c>#</c>).
    /// A token consisting entirely of letters/digits (e.g. <c>REM</c>) is only recognized
    /// as a whole word: it must be followed by whitespace or the end of the line, so it
    /// does not match inside a longer identifier.
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

    /// <summary>
    /// The string-literal delimiters recognized so that comment tokens inside strings
    /// are not misclassified. When several delimiters share a prefix, list the longer
    /// ones first (e.g. <c>"""</c> before <c>"</c>).
    /// </summary>
    public IReadOnlyList<StringLiteral> StringLiterals { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether comment-health analysis is meaningful for this
    /// language: the language opts in via <see cref="ShowHealth"/> and actually defines
    /// at least one comment token.
    /// </summary>
    public bool SupportsHealth =>
        ShowHealth && (LineCommentTokens.Count > 0 || BlockComments.Count > 0);

    /// <summary>
    /// Gets a value indicating whether the simplified cyclomatic-complexity metric is
    /// meaningful for this language: it defines at least one <see cref="ComplexityKeywords"/>
    /// token.
    /// </summary>
    public bool SupportsComplexity => ComplexityKeywords.Count > 0;

    /// <summary>
    /// A compiled regex matching any <see cref="ComplexityKeywords"/> token, used to count
    /// branch points in a code line. Alphabetic tokens are matched as whole words; other
    /// tokens (e.g. <c>&amp;&amp;</c>, <c>?:</c>) are matched literally. Built lazily and
    /// cached for the lifetime of this (shared, immutable) definition.
    /// </summary>
    internal Regex ComplexityRegex => _complexityRegex ??= BuildComplexityRegex(ComplexityKeywords);

    /// <summary>
    /// <see cref="BlockComments"/> grouped by the first character of <see cref="BlockComment.Open"/>,
    /// so <see cref="LineClassifier"/> can skip straight to the candidates that could possibly
    /// match at a given position instead of scanning every block-comment pair. Built lazily on
    /// first use and cached for the lifetime of this (shared, immutable) definition.
    /// </summary>
    internal Dictionary<char, BlockComment[]> BlockCommentsByFirstChar =>
        _blockCommentsByFirstChar ??= GroupByFirstChar(BlockComments, b => b.Open, c => c);

    /// <summary>
    /// <see cref="LineCommentTokens"/> grouped by their first character (case-folded when
    /// <see cref="CaseInsensitiveLineComments"/> is set), for the same reason as
    /// <see cref="BlockCommentsByFirstChar"/>.
    /// </summary>
    internal Dictionary<char, string[]> LineCommentsByFirstChar =>
        _lineCommentsByFirstChar ??= GroupByFirstChar(
            LineCommentTokens,
            token => token,
            c => CaseInsensitiveLineComments ? char.ToUpperInvariant(c) : c);

    /// <summary>
    /// <see cref="StringLiterals"/> grouped by the first character of <see cref="StringLiteral.Delimiter"/>,
    /// for the same reason as <see cref="BlockCommentsByFirstChar"/>.
    /// </summary>
    internal Dictionary<char, StringLiteral[]> StringLiteralsByFirstChar =>
        _stringLiteralsByFirstChar ??= GroupByFirstChar(StringLiterals, s => s.Delimiter, c => c);

    /// <summary>
    /// Every character that could begin a block-comment opener, line-comment token, or
    /// string-literal delimiter, so <see cref="LineClassifier"/> can skip (vectorized) over
    /// runs of ordinary code characters instead of probing each one. For
    /// <see cref="CaseInsensitiveLineComments"/> this includes every character that
    /// case-folds to a line-comment key; a superset is always safe, since candidates are
    /// still verified by an exact token match.
    /// </summary>
    internal SearchValues<char> TokenStartChars => _tokenStartChars ??= BuildTokenStartChars();

    private SearchValues<char> BuildTokenStartChars()
    {
        var chars = new HashSet<char>();
        chars.UnionWith(BlockCommentsByFirstChar.Keys);
        chars.UnionWith(StringLiteralsByFirstChar.Keys);

        if (CaseInsensitiveLineComments)
        {
            var keys = LineCommentsByFirstChar;
            for (var i = 0; i <= char.MaxValue; i++)
            {
                var c = (char)i;
                if (keys.ContainsKey(char.ToUpperInvariant(c)))
                {
                    chars.Add(c);
                }
            }
        }
        else
        {
            chars.UnionWith(LineCommentsByFirstChar.Keys);
        }

        return SearchValues.Create([.. chars]);
    }

    private static Regex BuildComplexityRegex(IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            // Never matched; kept simple rather than special-cased since SupportsComplexity
            // guards every call site that would otherwise use this regex.
            return new Regex("(?!)", RegexOptions.Compiled);
        }

        var alternatives = keywords
            .OrderByDescending(keyword => keyword.Length)
            .Select(keyword => keyword.All(char.IsLetter) ? $@"\b{Regex.Escape(keyword)}\b" : Regex.Escape(keyword));

        return new Regex(string.Join('|', alternatives), RegexOptions.Compiled);
    }

    private static Dictionary<char, T[]> GroupByFirstChar<T>(
        IReadOnlyList<T> items,
        Func<T, string> tokenSelector,
        Func<char, char> keySelector)
    {
        var groups = new Dictionary<char, List<T>>();
        foreach (var item in items)
        {
            var token = tokenSelector(item);
            if (token.Length == 0)
            {
                continue;
            }

            var key = keySelector(token[0]);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(item);
        }

        return groups.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}