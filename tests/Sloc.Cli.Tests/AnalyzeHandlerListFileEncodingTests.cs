using Sloc.Cli.Analysis;
using System.Text;

namespace Sloc.Cli.Tests;

/// <summary>
/// Tests for how <see cref="AnalyzeHandler.ReadListEntries(Stream)"/> decodes a piped
/// <c>--list-file -</c> list.
/// </summary>
public sealed class AnalyzeHandlerListFileEncodingTests
{
    /// <summary>
    /// Verifies that a list without a byte order mark is decoded as UTF-8, so non-ASCII paths
    /// survive regardless of the console code page, and that blank lines are dropped.
    /// </summary>
    [Fact]
    public void ReadListEntries_Utf8WithoutBom_DecodesNonAsciiPaths()
    {
        // Arrange
        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes("src/中文.cs\n\n  \nsrc/café.cs\r\n"));

        // Act
        var entries = AnalyzeHandler.ReadListEntries(stream).ToList();

        // Assert
        Assert.Equal(["src/中文.cs", "src/café.cs"], entries);
    }

    /// <summary>
    /// Verifies that a list starting with a UTF-16 byte order mark (as Windows PowerShell
    /// pipes to native programs) is decoded as UTF-16 instead of UTF-8.
    /// </summary>
    [Fact]
    public void ReadListEntries_Utf16WithBom_DecodesByBom()
    {
        // Arrange
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        using var stream = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes("src/中文.cs\n")]);

        // Act
        var entries = AnalyzeHandler.ReadListEntries(stream).ToList();

        // Assert
        Assert.Equal(["src/中文.cs"], entries);
    }
}
