using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.Diagnostics.CodeAnalysis;

namespace Sloc.Core;

/// <summary>
/// Classifies physical source lines as <see cref="LineKind.Code"/>,
/// <see cref="LineKind.Comment"/>, or <see cref="LineKind.Blank"/>.
/// </summary>
/// <remarks>
/// A classifier is stateful: it tracks open block comments and multi-line string
/// literals across calls to <see cref="Classify(ReadOnlySpan{char})"/>, so a single instance
/// must be used for the lines of a single file, in order. Comment tokens appearing inside
/// string literals are skipped for languages that declare their string delimiters, including
/// literals whose open and close tokens differ (see <see cref="StringLiteral.CloseDelimiter"/>)
/// and doubled-closing-delimiter escaping (see <see cref="StringLiteral.DoubledClosingEscape"/>,
/// e.g. C# <c>@"…""…"</c>).
/// </remarks>
public sealed class LineClassifier
{
    private readonly LanguageDefinition _language;
    private BlockComment? _activeBlock;
    private StringLiteral? _activeString;
    private bool _activeStringIsDoc;
    private int _blockDepth;

    // Reused across lines so tracking code text doesn't allocate per line. Only
    // maintained for languages that compute complexity (its sole consumer).
    private readonly bool _trackCodeText;
    private char[] _codeBuffer = [];
    private int _codeLength;

    /// <summary>
    /// Creates a classifier for the supplied language.
    /// </summary>
    /// <param name="language">The language whose comment rules drive classification.</param>
    public LineClassifier(LanguageDefinition language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _language = language;
        _trackCodeText = language.SupportsComplexity;
    }

    /// <summary>
    /// Gets a value indicating whether the classifier is currently inside an
    /// unterminated block comment.
    /// </summary>
    public bool InBlockComment => _activeBlock is not null;

    /// <summary>
    /// Gets a value indicating whether the classifier is currently inside an
    /// unterminated multi-line string literal.
    /// </summary>
    public bool InMultilineString => _activeString is not null;

    /// <summary>
    /// Gets the portion of the most recently classified line that is actual source code —
    /// i.e. with block comments, line comments, and string-literal content (delimiters
    /// included) blanked out to spaces so token boundaries and positions are preserved.
    /// Used to compute cyclomatic complexity without matching keywords that merely appear
    /// inside a string or comment. Only populated for languages that
    /// <see cref="LanguageDefinition.SupportsComplexity">support complexity</see>, and only
    /// valid until the next call to <see cref="Classify(ReadOnlySpan{char})"/>.
    /// </summary>
    internal ReadOnlySpan<char> CodeText => _codeBuffer.AsSpan(0, _codeLength);

    /// <summary>
    /// Classifies a single physical line, advancing any block-comment or
    /// multi-line-string state.
    /// </summary>
    /// <param name="line">The raw line content, without its line terminator.</param>
    /// <returns>
    /// The classification of the line.
    /// </returns>
    public LineKind Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Classify(line.AsSpan());
    }

    /// <summary>
    /// Classifies a single physical line, advancing any block-comment or
    /// multi-line-string state.
    /// </summary>
    /// <param name="line">The raw line content, without its line terminator.</param>
    /// <returns>
    /// The classification of the line.
    /// </returns>
    public LineKind Classify(ReadOnlySpan<char> line)
    {
        var sawCode = false;
        var sawComment = false;
        var index = 0;
        var codeChars = Span<char>.Empty;
        if (_trackCodeText)
        {
            if (_codeBuffer.Length < line.Length)
            {
                _codeBuffer = new char[Math.Max(line.Length, _codeBuffer.Length * 2)];
            }

            _codeLength = line.Length;
            codeChars = _codeBuffer.AsSpan(0, line.Length);
            codeChars.Fill(' ');
        }

        while (index < line.Length)
        {
            if (_activeBlock is not null)
            {
                index = ConsumeBlock(line, index, ref sawComment);
                continue;
            }

            if (_activeString is not null)
            {
                index = ConsumeString(line, index, ref sawCode, ref sawComment);
                continue;
            }

            // Skip straight to the next character that could start a comment or string;
            // everything before it is plain code (or whitespace).
            var next = line[index..].IndexOfAny(_language.TokenStartChars);
            var run = next < 0 ? line[index..] : line.Slice(index, next);
            if (!sawCode && !run.IsWhiteSpace())
            {
                sawCode = true;
            }

            if (_trackCodeText)
            {
                run.CopyTo(codeChars[index..]);
            }

            if (next < 0)
            {
                break;
            }

            index += next;

            if (TryMatchBlockOpen(line, index, out var block))
            {
                sawComment = true;
                index += block.Open.Length;
                _activeBlock = block;
                _blockDepth = 1;
                continue;
            }

            if (MatchesLineComment(line, index))
            {
                sawComment = true;
                break;
            }

            if (TryMatchStringOpen(line, index, out var literal))
            {
                // A doc-comment literal (e.g. Python docstring) only counts as a comment
                // when it begins a statement; used as an expression it is a string.
                _activeStringIsDoc = literal.IsDocComment && !sawCode;
                MarkString(ref sawCode, ref sawComment);
                index += literal.Delimiter.Length;
                _activeString = literal;
                continue;
            }

            if (!char.IsWhiteSpace(line[index]))
            {
                sawCode = true;
            }

            if (_trackCodeText)
            {
                codeChars[index] = line[index];
            }

            index++;
        }

        // A single-line string that never closed does not carry over to the next line.
        if (_activeString is { Multiline: false })
        {
            _activeString = null;
        }

        if (sawCode)
        {
            return LineKind.Code;
        }

        if (sawComment)
        {
            return LineKind.Comment;
        }

        return LineKind.Blank;
    }

    private static bool IsWordToken(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool MatchesAt(
        ReadOnlySpan<char> line,
        int index,
        string token,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (index + token.Length > line.Length)
        {
            return false;
        }

        return line.Slice(index, token.Length).Equals(token, comparison);
    }

    private static bool MatchesBlockToken(ReadOnlySpan<char> line, int index, string token, BlockComment block)
    {
        if (block.RequireLineStart && index != 0)
        {
            return false;
        }

        return MatchesAt(line, index, token);
    }

    /// <summary>
    /// Returns the offset within <paramref name="rest"/> of the next occurrence of either
    /// <paramref name="first"/> or <paramref name="second"/> (a token's first character,
    /// or <see langword="null"/> when that token can't occur), or -1 if neither occurs.
    /// </summary>
    private static int IndexOfCandidate(ReadOnlySpan<char> rest, char? first, char? second) =>
        (first, second) switch
        {
            ({ } a, { } b) => rest.IndexOfAny(a, b),
            ({ } a, null) => rest.IndexOf(a),
            (null, { } b) => rest.IndexOf(b),
            _ => -1
        };

    private static char? FirstChar(string token) => token.Length > 0 ? token[0] : null;

    private int ConsumeBlock(ReadOnlySpan<char> line, int index, ref bool sawComment)
    {
        var block = _activeBlock!;

        // Only a block's own open (when nestable) or close token can change state, so skip
        // straight to the next character that could start one. Tokens restricted to the
        // start of a line can't occur anywhere past column 0.
        var next = block.RequireLineStart && index != 0
            ? -1
            : IndexOfCandidate(line[index..], block.AllowNested ? FirstChar(block.Open) : null, FirstChar(block.Close));
        var run = next < 0 ? line[index..] : line.Slice(index, next);
        if (!run.IsWhiteSpace())
        {
            sawComment = true;
        }

        if (next < 0)
        {
            return line.Length;
        }

        index += next;

        if (block.AllowNested && MatchesBlockToken(line, index, block.Open, block))
        {
            _blockDepth++;
            sawComment = true;
            return index + block.Open.Length;
        }

        if (MatchesBlockToken(line, index, block.Close, block))
        {
            _blockDepth--;
            sawComment = true;
            if (_blockDepth <= 0)
            {
                _activeBlock = null;
            }

            return index + block.Close.Length;
        }

        if (!char.IsWhiteSpace(line[index]))
        {
            sawComment = true;
        }

        return index + 1;
    }

    private int ConsumeString(ReadOnlySpan<char> line, int index, ref bool sawCode, ref bool sawComment)
    {
        var literal = _activeString!;

        // Only an escape character or the closing delimiter can change state, so skip
        // straight to the next character that could start one.
        var next = IndexOfCandidate(line[index..], literal.AllowEscape ? literal.EscapeChar : null, FirstChar(literal.Closing));
        var run = next < 0 ? line[index..] : line.Slice(index, next);
        if (!run.IsWhiteSpace())
        {
            MarkString(ref sawCode, ref sawComment);
        }

        if (next < 0)
        {
            return line.Length;
        }

        index += next;

        if (literal.AllowEscape && line[index] == literal.EscapeChar && index + 1 < line.Length)
        {
            MarkString(ref sawCode, ref sawComment);
            return index + 2;
        }

        if (literal.DoubledClosingEscape
            && MatchesAt(line, index, literal.Closing)
            && MatchesAt(line, index + literal.Closing.Length, literal.Closing))
        {
            MarkString(ref sawCode, ref sawComment);
            return index + (literal.Closing.Length * 2);
        }

        if (MatchesAt(line, index, literal.Closing))
        {
            MarkString(ref sawCode, ref sawComment);
            _activeString = null;
            return index + literal.Closing.Length;
        }

        if (!char.IsWhiteSpace(line[index]))
        {
            MarkString(ref sawCode, ref sawComment);
        }

        return index + 1;
    }

    private void MarkString(ref bool sawCode, ref bool sawComment)
    {
        if (_activeStringIsDoc)
        {
            sawComment = true;
        }
        else
        {
            sawCode = true;
        }
    }

    private bool MatchesLineComment(ReadOnlySpan<char> line, int index)
    {
        var key = _language.CaseInsensitiveLineComments
            ? char.ToUpperInvariant(line[index])
            : line[index];

        if (!_language.LineCommentsByFirstChar.TryGetValue(key, out var candidates))
        {
            return false;
        }

        var comparison = _language.CaseInsensitiveLineComments
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var token in candidates)
        {
            if (!MatchesAt(line, index, token, comparison))
            {
                continue;
            }

            // A token made entirely of letters/digits (e.g. "REM") must be a whole word,
            // so it doesn't match inside a longer identifier (e.g. "REMOVE", "XREM", or
            // "REM_VALUE" — '_' counts as an identifier character too).
            var end = index + token.Length;
            if (IsWordToken(token)
                && ((end < line.Length && IsIdentifierChar(line[end]))
                    || (index > 0 && IsIdentifierChar(line[index - 1]))))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryMatchBlockOpen(ReadOnlySpan<char> line, int index, [NotNullWhen(true)] out BlockComment? block)
    {
        if (_language.BlockCommentsByFirstChar.TryGetValue(line[index], out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (MatchesBlockToken(line, index, candidate.Open, candidate))
                {
                    block = candidate;
                    return true;
                }
            }
        }

        block = null;
        return false;
    }

    private bool TryMatchStringOpen(ReadOnlySpan<char> line, int index, [NotNullWhen(true)] out StringLiteral? literal)
    {
        if (_language.StringLiteralsByFirstChar.TryGetValue(line[index], out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (MatchesAt(line, index, candidate.Delimiter))
                {
                    literal = candidate;
                    return true;
                }
            }
        }

        literal = null;
        return false;
    }
}
