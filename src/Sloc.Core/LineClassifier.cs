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
/// e.g. C# <c>@"…""…"</c>). For languages with <see cref="LanguageDefinition.RegexLiterals"/>,
/// comment tokens inside a regex literal (e.g. <c>/\/*$/</c>) are likewise skipped.
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

    // For RegexLiterals languages: the last significant code character seen (possibly on an
    // earlier line; '\0' before any), and its index when it is on the current line (else -1),
    // used to tell a regex-literal "/" from a division "/".
    private readonly bool _trackRegex;
    private char _lastSignificant;
    private int _lastSignificantIndex = -1;

    // Whether the previous line ended with a keyword like "return" (see RegexPrecedingKeywords),
    // so a regex literal may start at the beginning of this line.
    private bool _previousLineEndedWithRegexKeyword;

    // Stands in for _lastSignificant after a string or regex literal: a value, so a "/" after it is division.
    private const char ValueMarker = '"';

    // Keywords after which a "/" starts a regex literal rather than dividing.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> RegexPrecedingKeywords =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw",
            "case", "do", "else", "yield", "await"
        }.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Creates a classifier for the supplied language.
    /// </summary>
    /// <param name="language">The language whose comment rules drive classification.</param>
    public LineClassifier(LanguageDefinition language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _language = language;
        _trackCodeText = language.SupportsComplexity;
        _trackRegex = language.RegexLiterals;
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
        _lastSignificantIndex = -1;
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

            if (_trackRegex)
            {
                var trimmed = run.TrimEnd();
                if (!trimmed.IsEmpty)
                {
                    _lastSignificant = trimmed[^1];
                    _lastSignificantIndex = index + trimmed.Length - 1;
                }
            }

            if (next < 0)
            {
                break;
            }

            index += next;

            if (_trackRegex && line[index] == '/' && TryMatchRegexLiteral(line, index, out var regexEnd))
            {
                // The literal is code; its content is blanked from CodeText like a string's.
                sawCode = true;
                _lastSignificant = ValueMarker;
                _lastSignificantIndex = -1;
                index = regexEnd;
                continue;
            }

            if (MatchBlockCommentException(line, index) is var exceptionLength and > 0)
            {
                sawCode = true;
                if (_trackCodeText)
                {
                    line.Slice(index, exceptionLength).CopyTo(codeChars[index..]);
                }

                if (_trackRegex)
                {
                    _lastSignificant = line[index + exceptionLength - 1];
                    _lastSignificantIndex = index + exceptionLength - 1;
                }

                index += exceptionLength;
                continue;
            }

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
                if (_trackRegex)
                {
                    _lastSignificant = ValueMarker;
                    _lastSignificantIndex = -1;
                }

                continue;
            }

            if (!char.IsWhiteSpace(line[index]))
            {
                sawCode = true;
                if (_trackRegex)
                {
                    _lastSignificant = line[index];
                    _lastSignificantIndex = index;
                }
            }

            if (_trackCodeText)
            {
                codeChars[index] = line[index];
            }

            index++;
        }

        if (_trackRegex && _lastSignificantIndex >= 0)
        {
            _previousLineEndedWithRegexKeyword = EndsWithRegexKeyword(line, _lastSignificantIndex);
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

        if (MatchBlockCommentException(line, index) is var exceptionLength and > 0)
        {
            sawComment = true;
            return index + exceptionLength;
        }

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

    /// <summary>
    /// Whether a <c>/</c> at this point starts a regex literal rather than dividing: true
    /// where an operand is expected, i.e. at the start of the file, after an operator or
    /// opening bracket, or after a keyword like <c>return</c>; false after an operand (an
    /// identifier, number, <c>)</c>, <c>]</c>, or string/regex literal).
    /// </summary>
    private bool RegexMayStartHere(ReadOnlySpan<char> line)
    {
        var previous = _lastSignificant;
        if (previous is ')' or ']' or ValueMarker or '\'' or '`' or '<')
        {
            // '<' is not an operand, but excluding it keeps markup such as "</div>" (Vue,
            // Svelte, Astro, JSX) from being read as the start of a regex.
            return false;
        }

        if (!IsIdentifierChar(previous) && previous != '$')
        {
            return true;
        }

        // An identifier or number divides, unless it is a keyword (on this line, or ending
        // the previous one).
        return _lastSignificantIndex < 0
            ? _previousLineEndedWithRegexKeyword
            : EndsWithRegexKeyword(line, _lastSignificantIndex);
    }

    /// <summary>
    /// Whether the identifier ending at <paramref name="last"/> in <paramref name="line"/> is
    /// one of <see cref="RegexPrecedingKeywords"/> (and not a property of that name).
    /// </summary>
    private static bool EndsWithRegexKeyword(ReadOnlySpan<char> line, int last)
    {
        var end = last + 1;
        var start = end;
        while (start > 0 && IsIdentifierChar(line[start - 1]))
        {
            start--;
        }

        // A property named like a keyword (e.g. "obj.return") is still an operand.
        return (start == 0 || line[start - 1] is not ('.' or '$'))
            && RegexPrecedingKeywords.Contains(line[start..end]);
    }

    /// <summary>
    /// Matches a regex literal (<c>/…/flags</c>) opening at <paramref name="index"/>, skipping
    /// escapes and <c>[…]</c> classes (inside which <c>/</c> doesn't close it). Not a match
    /// (so the <c>/</c> is division or a comment) when an operand precedes it, when it is
    /// <c>//</c> or <c>/*</c>, when it doesn't close on this line, or when its closing
    /// <c>/</c> is followed by another <c>/</c> or <c>*</c>: that is far more likely a
    /// division followed by a comment than a regex, so the comment is kept.
    /// </summary>
    private bool TryMatchRegexLiteral(ReadOnlySpan<char> line, int index, out int end)
    {
        end = index;
        if (index + 1 >= line.Length || line[index + 1] is '/' or '*' || !RegexMayStartHere(line))
        {
            return false;
        }

        var inClass = false;
        for (var i = index + 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                i++;
            }
            else if (inClass)
            {
                inClass = c != ']';
            }
            else if (c == '[')
            {
                inClass = true;
            }
            else if (c == '/')
            {
                if (i + 1 < line.Length && line[i + 1] is '/' or '*')
                {
                    return false;
                }

                end = i + 1;
                while (end < line.Length && char.IsAsciiLetter(line[end]))
                {
                    end++;
                }

                return true;
            }
        }

        return false;
    }

    private bool MatchesLineComment(ReadOnlySpan<char> line, int index)
    {
        var exceptions = _language.LineCommentExceptions;
        for (var i = 0; i < exceptions.Count; i++)
        {
            if (MatchesAt(line, index, exceptions[i]))
            {
                return false;
            }
        }

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

    /// <summary>
    /// Returns the length of the <see cref="LanguageDefinition.BlockCommentExceptions">block-comment
    /// exception</see> token at <paramref name="index"/>, or 0 when none matches there.
    /// </summary>
    private int MatchBlockCommentException(ReadOnlySpan<char> line, int index)
    {
        var exceptions = _language.BlockCommentExceptions;
        for (var i = 0; i < exceptions.Count; i++)
        {
            if (MatchesAt(line, index, exceptions[i]))
            {
                return exceptions[i].Length;
            }
        }

        return 0;
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

    /// <summary>
    /// Whether the <c>'</c> at <paramref name="index"/> is a digit separator: it directly
    /// follows a token (letters, digits, <c>_</c>, <c>.</c>, and earlier separators) that
    /// starts with a digit, as in <c>1'000</c> or <c>0xFF'FF</c>, unlike the
    /// character-literal prefix in <c>u8'a'</c>.
    /// </summary>
    /// <param name="line">The line being classified.</param>
    /// <param name="index">The index of the <c>'</c>.</param>
    /// <returns>
    /// <see langword="true"/> if the quote separates digits; otherwise <see langword="false"/>.
    /// </returns>
    private static bool IsInNumericLiteral(ReadOnlySpan<char> line, int index)
    {
        var start = index;
        while (start > 0 && (IsIdentifierChar(line[start - 1]) || line[start - 1] is '\'' or '.'))
        {
            start--;
        }

        return start < index && char.IsAsciiDigit(line[start]);
    }

    private bool TryMatchStringOpen(ReadOnlySpan<char> line, int index, [NotNullWhen(true)] out StringLiteral? literal)
    {
        if (_language.QuoteDigitSeparators && line[index] == '\'' && IsInNumericLiteral(line, index))
        {
            literal = null;
            return false;
        }

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
