using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for the simplified cyclomatic-complexity metric computed by
/// <see cref="FileAnalyzer"/>.
/// </summary>
public class FileAnalyzerComplexityTests
{
    private static readonly LanguageDefinition CSharp = Resolve(".cs");
    private static readonly LanguageDefinition Yaml = Resolve(".yml");

    /// <summary>
    /// Verifies that a file with no branch points has the baseline complexity of 1.
    /// </summary>
    [Fact]
    public void AnalyzeText_NoBranches_ReturnsBaselineComplexityOfOne()
    {
        const string content = "int x = 1;\nint y = 2;\n";

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(1, result.Complexity);
    }

    /// <summary>
    /// Verifies that each branch-point token (if/for/while/&amp;&amp;) increments complexity
    /// by one on top of the baseline of 1.
    /// </summary>
    [Fact]
    public void AnalyzeText_MultipleBranches_CountsEachBranchToken()
    {
        const string content =
            "if (a) { }\n" +                 // +1 (if)
            "for (int i = 0; i < 10; i++) { }\n" + // +1 (for)
            "while (b) { }\n" +               // +1 (while)
            "if (a && b) { }\n";              // +1 (if) +1 (&&)

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(6, result.Complexity);
    }

    /// <summary>
    /// Verifies that branch tokens inside comments are not counted, since only code lines
    /// contribute to complexity.
    /// </summary>
    [Fact]
    public void AnalyzeText_BranchTokenInComment_IsNotCounted()
    {
        const string content =
            "// if (a) for (b) while (c)\n" +
            "int x = 1;\n";

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(1, result.Complexity);
    }

    /// <summary>
    /// Verifies that a language which does not support complexity analysis (e.g. YAML)
    /// always yields a <see langword="null"/> complexity, regardless of content.
    /// </summary>
    [Fact]
    public void AnalyzeText_UnsupportedLanguage_ReturnsNullComplexity()
    {
        const string content = "key: value\nother: 1\n";

        var result = new FileAnalyzer().AnalyzeText(content, Yaml);

        Assert.Null(result.Complexity);
    }

    private static LanguageDefinition Resolve(string extension)
    {
        LanguageRegistry.TryGetByExtension(extension, out var language);
        return language!;
    }
}
