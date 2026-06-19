using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="FileAnalyzer"/>.
/// </summary>
public class FileAnalyzerTests
{
    private static readonly LanguageDefinition CSharp = Resolve(".cs");

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