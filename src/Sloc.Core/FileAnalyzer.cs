using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace Sloc.Core;

/// <summary>
/// Analyzes a single source file (or in-memory content) and produces a
/// <see cref="FileAnalysis"/> with code, comment, and blank line counts.
/// </summary>
public sealed class FileAnalyzer
{
    /// <summary>
    /// Files up to this many bytes are read into memory in one pass; larger files are
    /// streamed. Settable so tests can exercise the streaming path with small files.
    /// </summary>
    internal int InMemoryThreshold { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Read size for the <see cref="StreamReader"/> on the streaming path. The underlying
    /// <see cref="FileStream"/> is unbuffered (the in-memory path reads it in one go), so
    /// without this each <see cref="StreamReader"/> refill (1 KB by default) would be a
    /// separate OS read.
    /// </summary>
    private const int StreamingBufferSize = 64 * 1024;

    /// <summary>
    /// Reads and analyzes the file at <paramref name="path"/> using the supplied language.
    /// </summary>
    /// <param name="path">The path of the file to analyze.</param>
    /// <param name="language">The language whose comment rules drive classification.</param>
    /// <param name="computeHash">
    /// When <see langword="true"/>, populates <see cref="FileAnalysis.Hash"/> with a
    /// content hash of the file (used for <c>--unique</c> duplicate detection). Computed
    /// from the bytes already read for classification, but still opt-in since hashing
    /// isn't free.
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

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);

        // Source files are almost always small: read them into memory once and do binary
        // detection, encoding detection, hashing, and decoding on that buffer, instead of
        // reading the file twice through the stream path below.
        if (TryReadAll(stream, InMemoryThreshold, out var rented, out var length))
        {
            try
            {
                return AnalyzeBytes(path, language, rented.AsSpan(0, length), computeHash);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        stream.Position = 0;
        var (isBinary, fallbackEncoding) = DetectBinaryAndEncoding(stream);
        if (isBinary)
        {
            throw new BinaryFileException();
        }

        stream.Position = 0;

        if (!computeHash)
        {
            using var reader = new StreamReader(stream, fallbackEncoding, detectEncodingFromByteOrderMarks: true, StreamingBufferSize);
            return Count(path, language, reader);
        }

        // Hash the bytes as they're read for line classification, rather than reading the
        // file a second time from scratch.
        using var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashingStream = new HashingStream(stream, hashing);
        using var hashingReader = new StreamReader(hashingStream, fallbackEncoding, detectEncodingFromByteOrderMarks: true, StreamingBufferSize);
        var analysis = Count(path, language, hashingReader);
        var hash = Convert.ToHexString(hashing.GetHashAndReset());

        return new FileAnalysis
        {
            Path = analysis.Path,
            Language = analysis.Language,
            Code = analysis.Code,
            Comment = analysis.Comment,
            Blank = analysis.Blank,
            Complexity = analysis.Complexity,
            Hash = hash
        };
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

        return Count(path, language, content);
    }

    private static FileAnalysis AnalyzeBytes(string path, LanguageDefinition language, ReadOnlySpan<byte> bytes, bool computeHash)
    {
        var hasTextBom = HasTextBom(bytes);
        if (!hasTextBom && bytes.Contains((byte)0))
        {
            throw new BinaryFileException();
        }

        // Mirrors StreamReader(detectEncodingFromByteOrderMarks: true): a BOM wins; otherwise
        // fall back to UTF-8 if the bytes are valid UTF-8, else Latin-1 (see DetectBinaryAndEncoding).
        var encoding = DetectBomEncoding(bytes, out var bomLength)
            ?? (Utf8.IsValid(bytes) ? Encoding.UTF8 : Encoding.Latin1);
        var body = bytes[bomLength..];

        var charCount = encoding.GetMaxCharCount(body.Length);
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(charCount, 1));
        try
        {
            var decoded = encoding.GetChars(body, chars);
            var analysis = Count(path, language, chars.AsSpan(0, decoded));
            if (!computeHash)
            {
                return analysis;
            }

            return new FileAnalysis
            {
                Path = analysis.Path,
                Language = analysis.Language,
                Code = analysis.Code,
                Comment = analysis.Comment,
                Blank = analysis.Blank,
                Complexity = analysis.Complexity,
                Hash = Convert.ToHexString(SHA256.HashData(bytes))
            };
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>
    /// Splits <paramref name="text"/> into lines exactly as <see cref="TextReader.ReadLine"/>
    /// would (on <c>\r\n</c>, <c>\r</c>, or <c>\n</c>, with no trailing empty line after a
    /// final terminator), without allocating a string per line.
    /// </summary>
    private static FileAnalysis Count(string path, LanguageDefinition language, ReadOnlySpan<char> text)
    {
        var counter = new LineCounter(language);

        while (!text.IsEmpty)
        {
            var end = text.IndexOfAny('\r', '\n');
            if (end < 0)
            {
                counter.Add(text);
                break;
            }

            counter.Add(text[..end]);
            var terminatorLength = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            text = text[(end + terminatorLength)..];
        }

        return counter.ToAnalysis(path);
    }

    private static FileAnalysis Count(string path, LanguageDefinition language, TextReader reader)
    {
        var counter = new LineCounter(language);

        while (reader.ReadLine() is { } line)
        {
            counter.Add(line);
        }

        return counter.ToAnalysis(path);
    }

    /// <summary>
    /// Reads the rest of <paramref name="stream"/> into a pooled buffer, as long as it
    /// holds at most <paramref name="maxBytes"/> bytes. On success the caller owns
    /// <paramref name="rented"/> and must return it to <see cref="ArrayPool{T}.Shared"/>;
    /// on failure nothing is rented and the stream position is unspecified.
    /// </summary>
    private static bool TryReadAll(Stream stream, int maxBytes, out byte[] rented, out int length)
    {
        rented = [];
        length = 0;

        if (stream.Length > maxBytes)
        {
            return false;
        }

        // One spare byte so a file that grew since Length was read is detected rather
        // than silently truncated.
        var buffer = ArrayPool<byte>.Shared.Rent((int)stream.Length + 1);
        var owns = false;
        try
        {
            var total = 0;
            int read;
            while ((read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
                if (total == buffer.Length)
                {
                    if (total > maxBytes)
                    {
                        return false;
                    }

                    var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, maxBytes + 1));
                    buffer.AsSpan(0, total).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }

            rented = buffer;
            length = total;
            owns = true;
            return true;
        }
        finally
        {
            if (!owns)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static Encoding? DetectBomEncoding(ReadOnlySpan<byte> bytes, out int bomLength)
    {
        // Same BOMs, in the same precedence, as StreamReader's detection.
        if (bytes is [0xFE, 0xFF, ..])
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            if (bytes is [_, _, 0x00, 0x00, ..])
            {
                bomLength = 4;
                return Encoding.UTF32;
            }

            bomLength = 2;
            return Encoding.Unicode;
        }

        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bomLength = 3;
            return Encoding.UTF8;
        }

        if (bytes is [0x00, 0x00, 0xFE, 0xFF, ..])
        {
            bomLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        bomLength = 0;
        return null;
    }

    /// <summary>
    /// Accumulates per-line classification (and complexity) counts for one file.
    /// </summary>
    private struct LineCounter
    {
        private readonly LanguageDefinition _language;
        private readonly LineClassifier _classifier;
        private readonly bool _supportsComplexity;
        private int _code;
        private int _comment;
        private int _blank;
        private int _complexity;

        public LineCounter(LanguageDefinition language)
        {
            _language = language;
            _classifier = new LineClassifier(language);
            _supportsComplexity = language.SupportsComplexity;
            _complexity = _supportsComplexity ? 1 : 0;
        }

        public void Add(ReadOnlySpan<char> line)
        {
            switch (_classifier.Classify(line))
            {
                case LineKind.Code:
                    _code++;
                    if (_supportsComplexity)
                    {
                        // Match against the code-only portion of the line (string/comment
                        // content blanked out) so a branch keyword inside a string literal
                        // or trailing comment doesn't inflate the complexity count.
                        _complexity += _language.ComplexityRegex.Count(_classifier.CodeText);
                    }
                    break;

                case LineKind.Comment:
                    _comment++;
                    break;

                default:
                    _blank++;
                    break;
            }
        }

        public readonly FileAnalysis ToAnalysis(string path) => new()
        {
            Path = path,
            Language = _language.Name,
            Code = _code,
            Comment = _comment,
            Blank = _blank,
            Complexity = _supportsComplexity ? _complexity : null
        };
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

        public override void Flush()
        {
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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}