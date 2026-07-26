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
        var (isBinary, fallbackEncoding) = DetectBinaryAndEncoding(stream);
        if (isBinary)
        {
            throw new BinaryFileException();
        }

        stream.Position = 0;

        if (!computeHash)
        {
            using var reader = new StreamReader(stream, fallbackEncoding, detectEncodingFromByteOrderMarks: true);
            return Count(path, language, reader);
        }

        // Hash the bytes as they're read for line classification, rather than reading the
        // file a second time from scratch.
        using var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashingStream = new HashingStream(stream, hashing);
        using var hashingReader = new StreamReader(hashingStream, fallbackEncoding, detectEncodingFromByteOrderMarks: true);
        var analysis = Count(path, language, hashingReader);
        var hash = Convert.ToHexString(hashing.GetHashAndReset());

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

    /// <summary>
    /// A read-only stream wrapper that feeds every byte read from the inner stream into an
    /// <see cref="IncrementalHash"/>, so a caller can compute a content hash while reading
    /// without a second pass over the file.
    /// </summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
            {
                hash.AppendData(buffer[..read]);
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Scans <paramref name="stream"/> once to decide both whether it is binary (contains a
    /// NUL byte outside a recognized text BOM) and, if not, which encoding
    /// <see cref="StreamReader"/> should fall back to when the file has no byte-order mark.
    /// Combines what used to be two separate full-file passes (<c>LooksBinary</c> and
    /// <c>DetectFallbackEncoding</c>) into one, and in doing so checks the whole file for NUL
    /// bytes instead of only a leading window.
    /// </summary>
    /// <remarks>
    /// A BOM (if present) still takes precedence via <c>detectEncodingFromByteOrderMarks</c>
    /// on the caller's <see cref="StreamReader"/>; the returned encoding only decides what to
    /// assume otherwise. Files that don't decode as valid UTF-8 (e.g. Latin-1/Windows-1252
    /// source files) would otherwise be silently corrupted with U+FFFD replacement characters.
    /// </remarks>
    private static (bool IsBinary, Encoding FallbackEncoding) DetectBinaryAndEncoding(Stream stream)
    {
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetDecoder();
        Span<byte> buffer = stackalloc byte[4096];
        Span<char> chars = stackalloc char[4096];

        var isValidUtf8 = true;
        var first = true;
        var hasTextBom = false;

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            var window = buffer[..read];

            if (first)
            {
                // A recognized Unicode BOM means the file is text whose encoding legitimately
                // contains NUL bytes (UTF-16/UTF-32), so the NUL heuristic below does not apply.
                hasTextBom = HasTextBom(window);
                first = false;
            }

            if (!hasTextBom)
            {
                foreach (var b in window)
                {
                    if (b == 0)
                    {
                        // Binary: no need to keep reading or to finish UTF-8 validation.
                        return (IsBinary: true, FallbackEncoding: Encoding.UTF8);
                    }
                }
            }

            if (isValidUtf8)
            {
                try
                {
                    decoder.GetChars(window, chars, flush: false);
                }
                catch (DecoderFallbackException)
                {
                    isValidUtf8 = false;
                }
            }
        }

        if (isValidUtf8)
        {
            try
            {
                decoder.GetChars([], chars, flush: true);
            }
            catch (DecoderFallbackException)
            {
                isValidUtf8 = false;
            }
        }

        return (IsBinary: false, FallbackEncoding: isValidUtf8 ? Encoding.UTF8 : Encoding.Latin1);
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