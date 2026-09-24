using Sloc.Core.Languages;
using System.Text;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="FileAnalyzer"/>.
/// </summary>
public class FileAnalyzerTests
{
    private static readonly LanguageDefinition CSharp = Resolve(".cs");

    /// <summary>
    /// Verifies that analyzing a file containing NUL bytes throws
    /// <see cref="BinaryFileException"/> so the caller can skip it.
    /// </summary>
    [Fact]
    public void Analyze_BinaryFile_ThrowsBinaryFileException()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-bin-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllBytes(path, [0x01, 0x02, 0x00, 0x03, 0x04]);
        try
        {
            Assert.Throws<BinaryFileException>(() => new FileAnalyzer().Analyze(path, CSharp));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a NUL byte located after the first 8000 bytes of the file is still
    /// detected as binary, not just one in a leading window.
    /// </summary>
    [Fact]
    public void Analyze_BinaryFileWithNulPastLeadingWindow_ThrowsBinaryFileException()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-bin-late-" + Guid.NewGuid().ToString("N") + ".cs");
        var bytes = new byte[10_000];
        Array.Fill(bytes, (byte)'a');
        bytes[9000] = 0x00;
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Throws<BinaryFileException>(() => new FileAnalyzer().Analyze(path, CSharp));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that <c>Hash</c> is left <see langword="null"/> by default, is populated
    /// when requested, and is identical for byte-identical files.
    /// </summary>
    [Fact]
    public void Analyze_ComputeHash_PopulatesHashForIdenticalContentOnly()
    {
        var pathA = Path.Combine(Path.GetTempPath(), "sloc-hash-a-" + Guid.NewGuid().ToString("N") + ".cs");
        var pathB = Path.Combine(Path.GetTempPath(), "sloc-hash-b-" + Guid.NewGuid().ToString("N") + ".cs");
        var pathC = Path.Combine(Path.GetTempPath(), "sloc-hash-c-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(pathA, "int x = 1;\n");
        File.WriteAllText(pathB, "int x = 1;\n");
        File.WriteAllText(pathC, "int y = 2;\n");
        try
        {
            var analyzer = new FileAnalyzer();

            var withoutHash = analyzer.Analyze(pathA, CSharp);
            Assert.Null(withoutHash.Hash);

            var a = analyzer.Analyze(pathA, CSharp, computeHash: true);
            var b = analyzer.Analyze(pathB, CSharp, computeHash: true);
            var c = analyzer.Analyze(pathC, CSharp, computeHash: true);

            Assert.NotNull(a.Hash);
            Assert.Equal(a.Hash, b.Hash);
            Assert.NotEqual(a.Hash, c.Hash);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
            File.Delete(pathC);
        }
    }

    /// <summary>
    /// Same as <see cref="Analyze_NonUtf8FileWithoutBom_DoesNotProduceReplacementCharacters"/>,
    /// but the invalid byte appears well past the first 8000 bytes of the file, to guard
    /// against a fallback-encoding detector that only sniffs a leading window.
    /// </summary>
    [Fact]
    public void Analyze_NonUtf8ByteAfterLeadingWindow_DoesNotProduceReplacementCharacters()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-latin1-late-" + Guid.NewGuid().ToString("N") + ".cs");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            for (var i = 0; i < 1000; i++)
            {
                writer.WriteLine("// padding line to push past the sniff window");
            }
        }

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            // 0xE9 is 'é' in Latin-1/Windows-1252, but is an invalid standalone UTF-8 byte.
            stream.Write([(byte)'/', (byte)'/', (byte)' ', 0xE9, (byte)'\n', (byte)'x', (byte)';', (byte)'\n']);
        }

        try
        {
            var result = new FileAnalyzer().Analyze(path, CSharp);

            Assert.Equal(1001, result.Comment);
            Assert.Equal(1, result.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a file with no BOM encoded in a non-UTF-8 codepage (Windows-1252, via
    /// high bytes that are not valid UTF-8 sequences) is decoded with a Latin-1 fallback
    /// rather than corrupting the content with U+FFFD replacement characters, which would
    /// otherwise risk misclassifying lines.
    /// </summary>
    [Fact]
    public void Analyze_NonUtf8FileWithoutBom_DoesNotProduceReplacementCharacters()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-latin1-" + Guid.NewGuid().ToString("N") + ".cs");
        // 0xE9 is 'é' in Latin-1/Windows-1252, but is an invalid standalone UTF-8 byte.
        var bytes = new byte[] { (byte)'/', (byte)'/', (byte)' ', 0xE9, (byte)'\n', (byte)'x', (byte)';', (byte)'\n' };
        File.WriteAllBytes(path, bytes);
        try
        {
            var result = new FileAnalyzer().Analyze(path, CSharp);

            Assert.Equal(1, result.Comment);
            Assert.Equal(1, result.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a normal text file is analyzed without being flagged as binary.
    /// </summary>
    [Fact]
    public void Analyze_TextFile_CountsLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-txt-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, "int x = 1;\n// comment\n\n");
        try
        {
            var result = new FileAnalyzer().Analyze(path, CSharp);

            Assert.Equal(1, result.Code);
            Assert.Equal(1, result.Comment);
            Assert.Equal(1, result.Blank);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a UTF-16 file (whose ASCII characters contain NUL bytes) is not
    /// misdetected as binary when it carries a byte-order mark, and its lines are counted.
    /// </summary>
    [Fact]
    public void Analyze_Utf16FileWithBom_IsCountedAsText()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-utf16-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, "// a comment\nvar x = 1;\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            var result = new FileAnalyzer().Analyze(path, CSharp);

            Assert.Equal(1, result.Code);
            Assert.Equal(1, result.Comment);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a zero-byte file on disk is analyzed as an empty file (all zero line
    /// counts) rather than being misdetected as binary — the NUL-byte scan over an empty
    /// buffer must not throw or produce a false positive.
    /// </summary>
    [Fact]
    public void Analyze_ZeroByteFile_ReturnsZerosAndIsNotBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-empty-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllBytes(path, []);
        try
        {
            var result = new FileAnalyzer().Analyze(path, CSharp);

            Assert.Equal(0, result.Code);
            Assert.Equal(0, result.Comment);
            Assert.Equal(0, result.Blank);
            Assert.Equal(0, result.Total);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Byte contents covering every line-terminator style, each supported byte-order mark,
    /// a non-UTF-8 fallback, and multi-line block comments/strings, for comparing the
    /// in-memory and streaming read paths.
    /// </summary>
    public static TheoryData<string, byte[]> ReadPathCases()
    {
        const string text = "using System;\r\n\r\n/* block\r   still */\nvar s = @\"a\"\"b\"; // c\n\n// last";
        byte[] Encode(Encoding encoding) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

        return new TheoryData<string, byte[]>
        {
            { "utf8 no bom", Encode(new UTF8Encoding(false)) },
            { "utf8 bom", Encode(new UTF8Encoding(true)) },
            { "utf16 le", Encode(new UnicodeEncoding(bigEndian: false, byteOrderMark: true)) },
            { "utf16 be", Encode(new UnicodeEncoding(bigEndian: true, byteOrderMark: true)) },
            { "utf32 le", Encode(new UTF32Encoding(bigEndian: false, byteOrderMark: true)) },
            { "utf32 be", Encode(new UTF32Encoding(bigEndian: true, byteOrderMark: true)) },
            { "latin1", [(byte)'/', (byte)'/', 0xE9, (byte)'\r', (byte)'x', (byte)';', (byte)'\r', (byte)'\n'] },
            { "trailing newline", Encoding.UTF8.GetBytes("a();\n\n") },
            { "lone cr at end", Encoding.UTF8.GetBytes("a();\r") },
            { "empty", [] },
        };
    }

    /// <summary>
    /// Verifies that the in-memory fast path (used for typical file sizes) and the streaming
    /// path (used for very large files) produce identical counts and hashes.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadPathCases))]
    public void Analyze_InMemoryAndStreamingPaths_Agree(string name, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-paths-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllBytes(path, bytes);
        try
        {
            var inMemory = new FileAnalyzer().Analyze(path, CSharp, computeHash: true);
            // -1 (not 0) so a zero-byte file is still forced onto the streaming path: TryReadAll
            // treats "at most 0 bytes" as satisfied by an empty file and would otherwise take the
            // in-memory path regardless of the threshold.
            var streamed = new FileAnalyzer { InMemoryThreshold = -1 }.Analyze(path, CSharp, computeHash: true);

            Assert.True(
                inMemory.Code == streamed.Code
                    && inMemory.Comment == streamed.Comment
                    && inMemory.Blank == streamed.Blank
                    && inMemory.Complexity == streamed.Complexity
                    && inMemory.Hash == streamed.Hash,
                $"{name}: in-memory {inMemory} vs streamed {streamed}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that the streaming path (forced via a zero in-memory threshold) still
    /// detects binary files.
    /// </summary>
    [Fact]
    public void Analyze_StreamingPath_BinaryFile_ThrowsBinaryFileException()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-bin-stream-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllBytes(path, [0x01, 0x02, 0x00, 0x03, 0x04]);
        try
        {
            Assert.Throws<BinaryFileException>(() => new FileAnalyzer { InMemoryThreshold = 0 }.Analyze(path, CSharp));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that analyzing an empty string returns zero for all line counts.
    /// </summary>
    [Fact]
    public void AnalyzeText_Empty_ReturnsZeros()
    {
        var result = new FileAnalyzer().AnalyzeText(string.Empty, CSharp);

        Assert.Equal(0, result.Code);
        Assert.Equal(0, result.Comment);
        Assert.Equal(0, result.Blank);
        Assert.Equal(0, result.Total);
    }

    /// <summary>
    /// Verifies that mixed content containing code, blank, and comment lines is counted
    /// correctly in each category.
    /// </summary>
    [Fact]
    public void AnalyzeText_MixedContent_CountsEachCategory()
    {
        const string content =
            "using System;\n" +        // code
            "\n" +                     // blank
            "// a comment\n" +         // comment
            "/* block\n" +             // comment (opens block)
            "   still block */\n" +    // comment (closes block)
            "int x = 1; // trailing\n"; // code

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(2, result.Code);
        Assert.Equal(3, result.Comment);
        Assert.Equal(1, result.Blank);
        Assert.Equal(6, result.Total);
        Assert.Equal("C#", result.Language);
    }

    /// <summary>
    /// Verifies that the <c>Total</c> count equals the sum of code, comment, and blank counts.
    /// </summary>
    [Fact]
    public void AnalyzeText_TotalEqualsSumOfCategories()
    {
        const string content = "a();\nb();\n// c\n\n";

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(result.Code + result.Comment + result.Blank, result.Total);
    }

    /// <summary>
    /// Looks up a <see cref="LanguageDefinition"/> by file extension and asserts it was found.
    /// </summary>
    /// <param name="extension">
    /// The file extension to resolve, e.g. <c>.cs</c>.
    /// </param>
    /// <returns>
    /// The <see cref="LanguageDefinition"/> registered for <paramref name="extension"/>.
    /// </returns>
    private static LanguageDefinition Resolve(string extension)
    {
        LanguageRegistry.TryGetByExtension(extension, out var language);
        Assert.NotNull(language);
        return language;
    }
}