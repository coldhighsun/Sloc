using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="LineClassifier"/>.
/// </summary>
public class LineClassifierTests
{
    private static readonly LanguageDefinition CSharp = Resolve(".cs");
    private static readonly LanguageDefinition Python = Resolve(".py");
    private static readonly LanguageDefinition Batch = Resolve(".bat");

    /// <summary>
    /// Verifies that a single-line block comment is classified as
    /// <see cref="LineKind.Comment"/> and that the block-comment state is closed afterward.
    /// </summary>
    [Fact]
    public void Classify_BlockCommentSingleLine_ReturnsCommentAndClosesBlock()
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Comment, classifier.Classify("/* comment */"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that typical code lines are classified as <see cref="LineKind.Code"/>.
    /// </summary>
    /// <param name="line">
    /// A line of source code to classify.
    /// </param>
    [Theory]
    [InlineData("int x = 1;")]
    [InlineData("    return value;")]
    [InlineData("int x = 1; // trailing comment")]
    public void Classify_Code_ReturnsCode(string line)
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Code, classifier.Classify(line));
    }

    /// <summary>
    /// Verifies that code following an inline block-comment close is classified as
    /// <see cref="LineKind.Code"/> and that the block state is closed.
    /// </summary>
    [Fact]
    public void Classify_CodeAfterBlockClose_ReturnsCode()
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Code, classifier.Classify("/* note */ DoWork();"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that code preceding an unterminated block-comment opener is classified as
    /// <see cref="LineKind.Code"/> and that the block state remains open.
    /// </summary>
    [Fact]
    public void Classify_CodeBeforeUnterminatedBlock_ReturnsCodeAndStaysInBlock()
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Code, classifier.Classify("DoWork(); /* trailing"));
        Assert.True(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that line-comment tokens cause the line to be classified as
    /// <see cref="LineKind.Comment"/>.
    /// </summary>
    /// <param name="line">
    /// A line beginning with a line-comment token.
    /// </param>
    [Theory]
    [InlineData("// comment")]
    [InlineData("    // indented comment")]
    public void Classify_LineComment_ReturnsComment(string line)
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Comment, classifier.Classify(line));
    }

    /// <summary>
    /// Verifies that each line inside a multi-line block comment is classified correctly,
    /// including blank lines within the block and the code line that follows.
    /// </summary>
    [Fact]
    public void Classify_MultiLineBlockComment_ClassifiesEachLine()
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Comment, classifier.Classify("/* start"));
        Assert.Equal(LineKind.Comment, classifier.Classify(" middle"));
        Assert.Equal(LineKind.Blank, classifier.Classify("   "));
        Assert.Equal(LineKind.Comment, classifier.Classify(" end */"));
        Assert.Equal(LineKind.Code, classifier.Classify("AfterComment();"));
    }

    /// <summary>
    /// Verifies that a Python hash-style line comment is classified as
    /// <see cref="LineKind.Comment"/>.
    /// </summary>
    [Fact]
    public void Classify_PythonHashComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Python);

        Assert.Equal(LineKind.Comment, classifier.Classify("# a comment"));
    }

    /// <summary>
    /// Verifies that each line within a Python triple-quoted multi-line docstring is
    /// classified as <see cref="LineKind.Comment"/> and that code after the closing
    /// delimiter is classified as <see cref="LineKind.Code"/>.
    /// </summary>
    [Fact]
    public void Classify_PythonMultiLineDocstring_ClassifiesEachLine()
    {
        var classifier = new LineClassifier(Python);

        Assert.Equal(LineKind.Comment, classifier.Classify("\"\"\""));
        Assert.Equal(LineKind.Comment, classifier.Classify("module description"));
        Assert.Equal(LineKind.Comment, classifier.Classify("\"\"\""));
        Assert.Equal(LineKind.Code, classifier.Classify("x = 1  # set"));
    }

    /// <summary>
    /// Verifies that a Python triple-quoted string on a single line is classified as
    /// <see cref="LineKind.Comment"/> and that the block state is closed afterward.
    /// </summary>
    [Fact]
    public void Classify_PythonTripleQuoteSingleLine_ReturnsComment()
    {
        var classifier = new LineClassifier(Python);

        Assert.Equal(LineKind.Comment, classifier.Classify("\"\"\"docstring\"\"\""));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that empty or whitespace-only lines are classified as
    /// <see cref="LineKind.Blank"/>.
    /// </summary>
    /// <param name="line">
    /// An empty or whitespace-only line.
    /// </param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Classify_WhitespaceOnly_ReturnsBlank(string line)
    {
        var classifier = new LineClassifier(CSharp);

        Assert.Equal(LineKind.Blank, classifier.Classify(line));
    }

    /// <summary>
    /// Verifies that Batch's <c>REM</c> line comment is recognized on its own (no trailing
    /// text), regardless of case, but does not match inside a longer word.
    /// </summary>
    /// <param name="line">A line of Batch source to classify.</param>
    /// <param name="expected">The expected classification.</param>
    [Theory]
    [InlineData("REM", LineKind.Comment)]
    [InlineData("rem", LineKind.Comment)]
    [InlineData("Rem this is a comment", LineKind.Comment)]
    [InlineData("REMOVE.exe", LineKind.Code)]
    [InlineData("XREM foo", LineKind.Code)]
    public void Classify_BatchRem_MatchesWholeWordCaseInsensitive(string line, LineKind expected)
    {
        var classifier = new LineClassifier(Batch);

        Assert.Equal(expected, classifier.Classify(line));
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