using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.Text;

namespace Sloc.Core;

/// <summary>
/// Analyzes a single source file (or in-memory content) and produces a
/// <see cref="FileAnalysis"/> with code, comment, and blank line counts.
/// </summary>
public sealed class FileAnalyzer
{
    private const int BinarySniffLength = 8000;

    /// <summary>
    /// Reads and analyzes the file at <paramref name="path"/> using the supplied language.
    /// </summary>
    /// <param name="path">The path of the file to analyze.</param>
    /// <param name="language">The language whose comment rules drive classification.</param>
    /// <returns>
    /// The line statistics for the file.
    /// </returns>
    /// <exception cref="BinaryFileException">
    /// The file appears to be binary (contains NUL bytes).
    /// </exception>
    public FileAnalysis Analyze(string path, LanguageDefinition language)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(language);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (LooksBinary(stream))
        {
            throw new BinaryFileException();
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Count(path, language, reader);
    }

    private static bool LooksBinary(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[BinarySniffLength];
        var read = stream.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Analyzes in-memory <paramref name="content"/> using the supplied language.
    /// Primarily intended for testing.
    /// </summary>
    /// <param name="content">The source text to analyze.</param>
    /// <param name="language">The language whose comment rules drive classification.</param>
    /// <param name="path">An optional display path to record on the result.</param>
    /// <returns>
    /// The line statistics for the content.
    /// </returns>
    public FileAnalysis AnalyzeText(string content, LanguageDefinition language, string path = "(memory)")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(language);

        using var reader = new StringReader(content);
        return Count(path, language, reader);
    }

    private static FileAnalysis Count(string path, LanguageDefinition language, TextReader reader)
    {
        var classifier = new LineClassifier(language);
        var code = 0;
        var comment = 0;
        var blank = 0;

        while (reader.ReadLine() is { } line)
        {
            switch (classifier.Classify(line))
            {
                case LineKind.Code:
                    code++;
                    break;

                case LineKind.Comment:
                    comment++;
                    break;

                default:
                    blank++;
                    break;
            }
        }

        return new FileAnalysis
        {
            Path = path,
            Language = language.Name,
            Code = code,
            Comment = comment,
            Blank = blank
        };
    }
}