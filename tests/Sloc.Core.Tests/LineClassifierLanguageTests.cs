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

    /// <summary>
    /// Verifies that a C# verbatim string containing a doubled quote (an escaped literal
    /// quote) does not end the string early, so a trailing <c>//</c> after it is still
    /// inside code, not treated as a real comment token outside a string.
    /// </summary>
    [Fact]
    public void Classify_CSharpVerbatimStringWithDoubledQuote_StaysOneString()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = @\"a\"\"b // not a comment\";"));
        Assert.False(classifier.InMultilineString);
    }

    /// <summary>
    /// Verifies that a C# verbatim string can span multiple physical lines.
    /// </summary>
    [Fact]
    public void Classify_CSharpVerbatimString_SpansLines()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = @\"line one"));
        Assert.True(classifier.InMultilineString);
        Assert.Equal(LineKind.Code, classifier.Classify("line two\";"));
        Assert.False(classifier.InMultilineString);
    }

    /// <summary>
    /// Verifies that a Rust raw string (<c>r"…"</c>) hides a <c>//</c> token from being
    /// treated as a line comment.
    /// </summary>
    [Fact]
    public void Classify_RustRawString_HidesCommentToken()
    {
        var classifier = new LineClassifier(Resolve(".rs"));

        Assert.Equal(LineKind.Code, classifier.Classify("let s = r\"not // a comment\";"));
    }

    /// <summary>
    /// Verifies that a hash-delimited Rust raw string (<c>r#"…"#</c>) is recognized, and a
    /// bare <c>"#</c> that only partially matches the opening does not close it early.
    /// </summary>
    [Fact]
    public void Classify_RustHashRawString_HidesCommentToken()
    {
        var classifier = new LineClassifier(Resolve(".rs"));

        Assert.Equal(LineKind.Code, classifier.Classify("let s = r#\"not // \"a comment\"#;"));
    }

    /// <summary>
    /// Verifies that BASIC's <c>REM</c> line comment is matched case-insensitively and
    /// that the <c>'</c> shorthand comment is recognized too.
    /// </summary>
    [Fact]
    public void Classify_BasicRemComment_IsCaseInsensitive()
    {
        var classifier = new LineClassifier(Resolve(".bas"));

        Assert.Equal(LineKind.Comment, classifier.Classify("rem a basic comment"));
        Assert.Equal(LineKind.Comment, classifier.Classify("' a basic comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("PRINT \"hello\""));
    }

    /// <summary>
    /// Verifies that Pascal's <c>{ }</c> and <c>(* *)</c> block comments are both
    /// recognized, alongside its <c>//</c> line comment.
    /// </summary>
    [Fact]
    public void Classify_PascalBlockComments_BothDelimitersRecognized()
    {
        var classifier = new LineClassifier(Resolve(".pas"));

        Assert.Equal(LineKind.Comment, classifier.Classify("{ a brace comment }"));
        Assert.Equal(LineKind.Comment, classifier.Classify("(* a paren comment *)"));
        Assert.Equal(LineKind.Comment, classifier.Classify("// a line comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("writeln('hello');"));
    }

    /// <summary>
    /// Verifies that a Zig <c>//</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_ZigLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".zig"));

        Assert.Equal(LineKind.Comment, classifier.Classify("// a zig comment"));
    }

    /// <summary>
    /// Verifies that a Nim <c>#[ ]#</c> block comment nests.
    /// </summary>
    [Fact]
    public void Classify_NimNestedBlockComment_RequiresMatchingCloses()
    {
        var classifier = new LineClassifier(Resolve(".nim"));

        Assert.Equal(LineKind.Comment, classifier.Classify("#[ outer #[ inner ]#"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment ]#"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that an OCaml <c>(* *)</c> block comment spanning multiple lines keeps the
    /// block open across lines.
    /// </summary>
    [Fact]
    public void Classify_OCamlMultiLineBlock_KeepsBlockOpen()
    {
        var classifier = new LineClassifier(Resolve(".ml"));

        Assert.Equal(LineKind.Comment, classifier.Classify("(* start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("end *)"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that an Erlang <c>%</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_ErlangLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".erl"));

        Assert.Equal(LineKind.Comment, classifier.Classify("% an erlang comment"));
    }

    /// <summary>
    /// Verifies that an Elm <c>{- -}</c> block comment nests.
    /// </summary>
    [Fact]
    public void Classify_ElmNestedBlockComment_RequiresMatchingCloses()
    {
        var classifier = new LineClassifier(Resolve(".elm"));

        Assert.Equal(LineKind.Comment, classifier.Classify("{- outer {- inner -}"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment -}"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a Solidity <c>//</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_SolidityLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".sol"));

        Assert.Equal(LineKind.Comment, classifier.Classify("// a solidity comment"));
    }

    /// <summary>
    /// Verifies that a GraphQL <c>#</c> line comment is classified as a comment while a
    /// field line is classified as code.
    /// </summary>
    [Fact]
    public void Classify_GraphQl_DistinguishesCommentFromCode()
    {
        var classifier = new LineClassifier(Resolve(".graphql"));

        Assert.Equal(LineKind.Comment, classifier.Classify("# a graphql comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("field: String"));
    }

    /// <summary>
    /// Verifies that Bazel/Starlark <c>BUILD</c> and <c>WORKSPACE</c> files (identified by
    /// filename, not extension) resolve to the same language as <c>.bzl</c> files.
    /// </summary>
    [Theory]
    [InlineData("BUILD")]
    [InlineData("BUILD.bazel")]
    [InlineData("WORKSPACE")]
    public void TryGetByPath_BazelFilenames_ResolveToStarlark(string fileName)
    {
        Assert.True(LanguageRegistry.TryGetByPath(fileName, out var language));
        Assert.Equal("Bazel/Starlark", language.Name);
    }

    /// <summary>
    /// Verifies that a Nix <c>#</c> line comment is classified as a comment.
    /// </summary>
    [Fact]
    public void Classify_NixLineComment_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".nix"));

        Assert.Equal(LineKind.Comment, classifier.Classify("# a nix comment"));
    }

    private static LanguageDefinition Resolve(string extension)
    {
        LanguageRegistry.TryGetByExtension(extension, out var language);
        Assert.NotNull(language);
        return language;
    }
}
