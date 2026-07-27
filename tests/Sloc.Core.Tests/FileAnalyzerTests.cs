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