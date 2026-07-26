using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.Security.Cryptography;
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
    /// <param name="computeHash">
    /// When <see langword="true"/>, populates <see cref="FileAnalysis.Hash"/> with a
    /// content hash of the file (used for <c>--unique</c> duplicate detection). Requires a
    /// second read of the file, so it is opt-in.
    /// </param>
    /// <returns>
    /// The line statistics for the file.
    /// </returns>
    /// <exception cref="BinaryFileException">
    /// The file appears to be binary (contains NUL bytes).
    /// </exception>
    public FileAnalysis Analyze(string path, LanguageDefinition language, bool computeHash = false)
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
        var analysis = Count(path, language, reader);

        if (!computeHash)
        {
            return analysis;
        }

        using var hashStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = Convert.ToHexString(SHA256.HashData(hashStream));
        return new FileAnalysis
        {
            Path = analysis.Path,
            Language = analysis.Language,
            Code = analysis.Code,
            Comment = analysis.Comment,
            Blank = analysis.Blank,
            Hash = hash
        };
    }

    private static bool LooksBinary(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[BinarySniffLength];
        var read = stream.Read(buffer);
        var window = buffer[..read];

        // A recognized Unicode BOM means the file is text whose encoding legitimately
        // contains NUL bytes (UTF-16/UTF-32), so the NUL heuristic below does not apply.
        if (HasTextBom(window))
        {
            return false;
        }

        foreach (var b in window)
        {
            if (b == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTextBom(ReadOnlySpan<byte> bytes)
    {
        // UTF-8, UTF-16 LE/BE, UTF-32 LE/BE byte-order marks. (UTF-32 LE shares its first
        // two bytes with UTF-16 LE, which is fine: both are text.)
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return true;
        }

        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return true;
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