using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains tests for the string-literal, nested-block-comment, and doc-comment
/// awareness added to <see cref="LineClassifier"/>.
/// </summary>
public class LineClassifierAccuracyTests
{
    /// <summary>
    /// Verifies that a <c>//</c> sequence inside a C# string literal is treated as code,
    /// not as a line comment.
    /// </summary>
    [Fact]
    public void Classify_LineCommentTokenInsideString_ReturnsCode()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = \"http://example.com\";"));
    }

    /// <summary>
    /// Verifies that a block-comment opener inside a C# string literal does not start a
    /// block comment, so the following line remains code.
    /// </summary>
    [Fact]
    public void Classify_BlockOpenInsideString_DoesNotStartBlock()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = \"/* not a comment */\";"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("var next = 1;"));
    }

    /// <summary>
    /// Verifies that an escaped quote inside a string does not terminate the string, so a
    /// trailing comment token stays inside the literal.
    /// </summary>
    [Fact]
    public void Classify_EscapedQuoteInString_KeepsStringOpen()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = \"a \\\" // b\";"));
    }

    /// <summary>
    /// Verifies that Rust nested block comments require matching closes before the block
    /// ends.
    /// </summary>
    [Fact]
    public void Classify_RustNestedBlockComment_RequiresMatchingCloses()
    {
        var classifier = new LineClassifier(Resolve(".rs"));

        Assert.Equal(LineKind.Comment, classifier.Classify("/* outer /* inner */"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment */"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("let x = 1;"));
    }

    /// <summary>
    /// Verifies that a C-style nested opener does not extend a non-nesting language's
    /// block comment (C# closes on the first close token).
    /// </summary>
    [Fact]
    public void Classify_NonNestingBlockComment_ClosesOnFirstClose()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Comment, classifier.Classify("/* outer /* inner */"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("DoWork();"));
    }

    /// <summary>
    /// Verifies that a Python triple-quoted string used as an assignment value is treated
    /// as code, not as a docstring comment.
    /// </summary>
    [Fact]
    public void Classify_PythonTripleQuoteAssignment_ReturnsCode()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Code, classifier.Classify("x = \"\"\"literal\"\"\""));
    }

    /// <summary>
    /// Verifies that an indented standalone Python triple-quoted string is still treated
    /// as a docstring comment.
    /// </summary>
    [Fact]
    public void Classify_PythonIndentedDocstring_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Comment, classifier.Classify("    \"\"\"an indented docstring\"\"\""));
    }

    /// <summary>
    /// Verifies that a multi-line Python assignment string keeps its continuation lines
    /// classified as code.
    /// </summary>
    [Fact]
    public void Classify_PythonMultiLineAssignmentString_ContinuationIsCode()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Code, classifier.Classify("sql = \"\"\"SELECT 1"));
        Assert.True(classifier.InMultilineString);
        Assert.Equal(LineKind.Code, classifier.Classify("FROM t"));
        Assert.Equal(LineKind.Code, classifier.Classify("\"\"\""));
        Assert.False(classifier.InMultilineString);
    }

    /// <summary>
    /// Verifies that a Ruby <c>=begin</c> token is only recognized as a block comment at
    /// column 0, not when indented mid-line.
    /// </summary>
    [Fact]
    public void Classify_RubyBeginNotAtLineStart_IsNotBlockComment()
    {
        var classifier = new LineClassifier(Resolve(".rb"));

        Assert.Equal(LineKind.Comment, classifier.Classify("=begin"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("commented out"));
        Assert.Equal(LineKind.Comment, classifier.Classify("=end"));
        Assert.False(classifier.InBlockComment);

        var indented = new LineClassifier(Resolve(".rb"));
        Assert.Equal(LineKind.Code, indented.Classify("  x = 1 =begin"));
        Assert.False(indented.InBlockComment);
    }

    private static LanguageDefinition Resolve(string extension)
    {
        LanguageRegistry.TryGetByExtension(extension, out var language);
        Assert.NotNull(language);
        return language;
    }
}
