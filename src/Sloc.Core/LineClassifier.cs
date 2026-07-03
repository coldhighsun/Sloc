using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.Diagnostics.CodeAnalysis;

namespace Sloc.Core;

/// <summary>
/// Classifies physical source lines as <see cref="LineKind.Code"/>,
/// <see cref="LineKind.Comment"/>, or <see cref="LineKind.Blank"/>.
/// </summary>
/// <remarks>
/// A classifier is stateful: it tracks an open block comment across calls to
/// <see cref="Classify"/>, so a single instance must be used for the lines of a
/// single file, in order. String literals are not parsed, so comment tokens that
/// appear inside strings may be misclassified (a documented limitation).
/// </remarks>
public sealed class LineClassifier
{
    private readonly LanguageDefinition _language;
    private BlockComment? _activeBlock;

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
    /// Classifies a single physical line, advancing any block-comment state.
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

        while (index < line.Length)
        {
            if (_activeBlock is not null)
            {
                var close = _activeBlock.Close;
                if (MatchesAt(line, index, close))
                {
                    sawComment = true;
                    index += close.Length;
                    _activeBlock = null;
                    continue;
                }

                if (!char.IsWhiteSpace(line[index]))
                {
                    sawComment = true;
                }

                index++;
                continue;
            }

            if (TryMatchBlockOpen(line, index, out var block))
            {
                sawComment = true;
                index += block.Open.Length;
                _activeBlock = block;
                continue;
            }

            if (MatchesLineComment(line, index))
            {
                sawComment = true;
                break;
            }

            if (!char.IsWhiteSpace(line[index]))
            {
                sawCode = true;
            }

            index++;
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

    private static bool MatchesAt(string line, int index, string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (index + token.Length > line.Length)
        {
            return false;
        }

        return string.CompareOrdinal(line, index, token, 0, token.Length) == 0;
    }

    private bool MatchesLineComment(string line, int index)
    {
        foreach (var token in _language.LineCommentTokens)
        {
            if (MatchesAt(line, index, token))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMatchBlockOpen(string line, int index, [NotNullWhen(true)] out BlockComment? block)
    {
        foreach (var candidate in _language.BlockComments)
        {
            if (MatchesAt(line, index, candidate.Open))
            {
                block = candidate;
                return true;
            }
        }

        block = null;
        return false;
    }
}