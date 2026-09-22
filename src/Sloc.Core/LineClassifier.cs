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
/// literals across calls to <see cref="Classify"/>, so a single instance must be used
/// for the lines of a single file, in order. Comment tokens appearing inside string
/// literals are skipped for languages that declare their string delimiters, including
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
    private string _codeText = string.Empty;

    /// <summary>
    /// Creates a classifier for the supplied language.
    /// </summary>
    /// <param name="language">The language whose comment rules drive classification.</param>
    public LineClassifier(LanguageDefinition language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _language = language;
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
    /// inside a string or comment.
    /// </summary>
    internal string CodeText => _codeText;

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

        var sawCode = false;
        var sawComment = false;
        var index = 0;
        var codeChars = new char[line.Length];
        Array.Fill(codeChars, ' ');

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

            codeChars[index] = line[index];
            index++;
        }

        _codeText = new string(codeChars);

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
        string line,
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

        return line.AsSpan(index, token.Length).Equals(token, comparison);
    }

    private static bool MatchesBlockToken(string line, int index, string token, BlockComment block)
    {
        if (block.RequireLineStart && index != 0)
        {
            return false;
        }

        return MatchesAt(line, index, token);
    }

    private int ConsumeBlock(string line, int index, ref bool sawComment)
    {
        var block = _activeBlock!;

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

    private int ConsumeString(string line, int index, ref bool sawCode, ref bool sawComment)
    {
        var literal = _activeString!;

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

    private bool MatchesLineComment(string line, int index)
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

    private bool TryMatchBlockOpen(string line, int index, [NotNullWhen(true)] out BlockComment? block)
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

    private bool TryMatchStringOpen(string line, int index, [NotNullWhen(true)] out StringLiteral? literal)
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