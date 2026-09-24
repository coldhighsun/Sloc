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
    /// Verifies that branch-point tokens appearing inside a string literal or a trailing
    /// line comment are not counted, since they aren't real branches — only tokens in the
    /// actual code portion of the line should contribute.
    /// </summary>
    [Fact]
    public void AnalyzeText_BranchTokenInStringLiteralOrTrailingComment_IsNotCounted()
    {
        const string content = "Console.WriteLine(\"if or while\"); // for testing\n";

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(1, result.Complexity);
    }

    /// <summary>
    /// Verifies that branch-point tokens inside a JavaScript regex literal are not counted.
    /// </summary>
    [Fact]
    public void AnalyzeText_BranchTokenInRegexLiteral_IsNotCounted()
    {
        var result = new FileAnalyzer().AnalyzeText("const re = /if|for|&&|while/;\n", Resolve(".js"));

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

    /// <summary>
    /// Verifies that C#'s <c>foreach</c> counts as a branch point: the whole-word match
    /// for <c>for</c> alone does not match inside it.
    /// </summary>
    [Fact]
    public void AnalyzeText_CSharpForeach_CountsAsBranch()
    {
        const string content = "foreach (var x in xs) { }\n";

        var result = new FileAnalyzer().AnalyzeText(content, CSharp);

        Assert.Equal(2, result.Complexity);
    }

    /// <summary>
    /// Verifies that PHP's <c>foreach</c> and <c>elseif</c> count as branch points: the
    /// whole-word matches for <c>for</c>/<c>if</c> alone do not match inside them.
    /// </summary>
    [Fact]
    public void AnalyzeText_PhpForeachAndElseif_CountAsBranches()
    {
        const string content =
            "foreach ($xs as $x) {\n" +   // +1 (foreach)
            "if ($a) {\n" +               // +1 (if)
            "} elseif ($b) {\n" +         // +1 (elseif)
            "}\n" +
            "}\n";

        var result = new FileAnalyzer().AnalyzeText(content, Resolve(".php"));

        Assert.Equal(4, result.Complexity);
    }
}
