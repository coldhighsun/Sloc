using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains classification tests spanning several languages beyond C# and Python, to
/// exercise their distinct comment tokens and block-comment delimiters.
/// </summary>
public class LineClassifierLanguageTests
{
    /// <summary>
    /// Verifies that a Ruby <c>#</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_RubyLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".rb"));

        Assert.Equal(LineKind.Comment, classifier.Classify("# a ruby comment"));
    }

    /// <summary>
    /// Verifies that a SQL <c>--</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_SqlLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".sql"));

        Assert.Equal(LineKind.Comment, classifier.Classify("-- select everything"));
    }

    /// <summary>
    /// Verifies that a YAML <c>#</c> line comment is classified as a comment while a
    /// key/value line is classified as code.
    /// </summary>
    [Fact]
    public void Classify_Yaml_DistinguishesCommentFromCode()
    {
        var classifier = new LineClassifier(Resolve(".yml"));

        Assert.Equal(LineKind.Comment, classifier.Classify("# a yaml comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("key: value"));
    }

    /// <summary>
    /// Verifies that a single-line HTML comment is classified as a comment and the block
    /// state is closed afterward.
    /// </summary>
    [Fact]
    public void Classify_HtmlBlockComment_ReturnsCommentAndClosesBlock()
    {
        var classifier = new LineClassifier(Resolve(".html"));

        Assert.Equal(LineKind.Comment, classifier.Classify("<!-- a comment -->"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that an F# <c>(* *)</c> block comment spanning multiple lines keeps the
    /// block open across lines.
    /// </summary>
    [Fact]
    public void Classify_FSharpMultiLineBlock_KeepsBlockOpen()
    {
        var classifier = new LineClassifier(Resolve(".fs"));

        Assert.Equal(LineKind.Comment, classifier.Classify("(* start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still inside"));
        Assert.Equal(LineKind.Comment, classifier.Classify("end *)"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a Lua <c>--[[ ]]</c> block comment spanning lines is classified as
    /// comment and closes correctly.
    /// </summary>
    [Fact]
    public void Classify_LuaBlockComment_SpansLines()
    {
        var classifier = new LineClassifier(Resolve(".lua"));

        Assert.Equal(LineKind.Comment, classifier.Classify("--[[ start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment"));
        Assert.Equal(LineKind.Comment, classifier.Classify("end ]]"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that Haskell <c>{- -}</c> block comments nest.
    /// </summary>
    [Fact]
    public void Classify_HaskellNestedBlockComment_RequiresMatchingCloses()
    {
        var classifier = new LineClassifier(Resolve(".hs"));

        Assert.Equal(LineKind.Comment, classifier.Classify("{- outer {- inner -}"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment -}"));
        Assert.False(classifier.InBlockComment);
    }

    private static LanguageDefinition Resolve(string extension)
    {
        LanguageRegistry.TryGetByExtension(extension, out var language);
        Assert.NotNull(language);
        return language;
    }
}
