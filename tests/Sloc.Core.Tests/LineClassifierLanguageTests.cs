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
    /// Verifies that F#'s <c>(*)</c> multiplication operator is code and does not open a
    /// block comment that would swallow the following lines.
    /// </summary>
    [Fact]
    public void Classify_FSharpMultiplicationOperator_IsCodeAndOpensNoBlock()
    {
        var classifier = new LineClassifier(Resolve(".fs"));

        Assert.Equal(LineKind.Code, classifier.Classify("let product = List.reduce (*) [1; 2; 3]"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("let x = 1"));
    }

    /// <summary>
    /// Verifies that F#'s <c>(*)</c> inside a block comment neither nests a new comment
    /// level nor closes the enclosing one with its trailing <c>*)</c>.
    /// </summary>
    [Fact]
    public void Classify_FSharpMultiplicationOperatorInsideBlock_NeitherNestsNorCloses()
    {
        var classifier = new LineClassifier(Resolve(".fs"));

        Assert.Equal(LineKind.Comment, classifier.Classify("(* fold with (*) here"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("end *)"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("let x = 1"));
    }

    /// <summary>
    /// Verifies that OCaml's <c>(*)</c>, unlike F#'s, still opens a block comment.
    /// </summary>
    [Fact]
    public void Classify_OCamlParenStarParen_OpensBlock()
    {
        var classifier = new LineClassifier(Resolve(".ml"));

        Assert.Equal(LineKind.Comment, classifier.Classify("(*) a comment"));
        Assert.True(classifier.InBlockComment);
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
    /// Verifies that an underscore right after a whole-word line-comment token (e.g.
    /// BASIC's <c>REM</c>) is treated as part of a longer identifier, not a word boundary,
    /// so <c>REM_VALUE</c> is code rather than a comment.
    /// </summary>
    [Fact]
    public void Classify_BasicRemFollowedByUnderscore_IsTreatedAsIdentifier()
    {
        var classifier = new LineClassifier(Resolve(".bas"));

        Assert.Equal(LineKind.Code, classifier.Classify("REM_VALUE = 1"));
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
    /// Verifies that a backslash is an ordinary character in a Pascal string, so
    /// <c>'C:\'</c> closes and a <c>{</c> after it still opens a block comment.
    /// </summary>
    [Fact]
    public void Classify_PascalStringEndingInBackslash_ClosesString()
    {
        var classifier = new LineClassifier(Resolve(".pas"));

        Assert.Equal(LineKind.Code, classifier.Classify(@"dir := 'C:\'; { start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment }"));
    }

    /// <summary>
    /// Verifies that a doubled quote inside a Pascal string is an embedded quote, not the
    /// end of one string and the start of another.
    /// </summary>
    [Fact]
    public void Classify_PascalDoubledQuote_StaysInsideString()
    {
        var classifier = new LineClassifier(Resolve(".pas"));

        Assert.Equal(LineKind.Code, classifier.Classify("s := 'it''s { not a comment';"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a PowerShell backtick-escaped quote (<c>"`""</c>) does not close the
    /// string, so a <c>&lt;#</c> inside it does not open a block comment.
    /// </summary>
    [Fact]
    public void Classify_PowerShellBacktickEscapedQuote_StaysInsideString()
    {
        var classifier = new LineClassifier(Resolve(".ps1"));

        Assert.Equal(LineKind.Code, classifier.Classify("$a = \"`\"<# not a comment\""));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("$b = 1"));
    }

    /// <summary>
    /// Verifies that a backslash is an ordinary character in a PowerShell string, so
    /// <c>"C:\"</c> closes and a <c>&lt;#</c> after it still opens a block comment.
    /// </summary>
    [Fact]
    public void Classify_PowerShellStringEndingInBackslash_ClosesString()
    {
        var classifier = new LineClassifier(Resolve(".ps1"));

        Assert.Equal(LineKind.Code, classifier.Classify(@"$dir = ""C:\"" <# start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment #>"));
    }

    /// <summary>
    /// Verifies that D's <c>/* */</c> comments do not nest (the first <c>*/</c> closes the
    /// comment), while its <c>/+ +/</c> comments do.
    /// </summary>
    [Fact]
    public void Classify_DBlockComments_OnlyPlusCommentsNest()
    {
        var classifier = new LineClassifier(Resolve(".d"));

        Assert.Equal(LineKind.Comment, classifier.Classify("/* outer /* inner */"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("int x = 1;"));

        Assert.Equal(LineKind.Comment, classifier.Classify("/+ outer /+ inner +/"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still comment +/"));
        Assert.False(classifier.InBlockComment);
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

    /// <summary>
    /// Verifies that a C# 11 raw string literal (<c>"""</c>) is tracked as a multi-line
    /// string: a <c>/*</c> or <c>//</c> inside it neither opens a block comment nor makes
    /// the line a comment, and the code after it is still counted as code.
    /// </summary>
    [Fact]
    public void Classify_CSharpRawStringLiteral_IgnoresCommentTokensInside()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var sql = \"\"\""));
        Assert.Equal(LineKind.Code, classifier.Classify("    /* not a comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("    // not a comment either"));
        Assert.Equal(LineKind.Code, classifier.Classify("    \"\"\";"));
        Assert.Equal(LineKind.Code, classifier.Classify("int y = 1;"));
        Assert.Equal(LineKind.Comment, classifier.Classify("// a real comment"));
    }

    /// <summary>
    /// Verifies that an interpolated verbatim string written with the <c>@$</c> prefix order
    /// and starting with an escaped quote (<c>@$"""…</c>) is a single-line verbatim string,
    /// not the opening of a multi-line raw string literal.
    /// </summary>
    [Fact]
    public void Classify_CSharpInterpolatedVerbatimStringStartingWithEscapedQuote_IsNotRawString()
    {
        var classifier = new LineClassifier(Resolve(".cs"));

        Assert.Equal(LineKind.Code, classifier.Classify("var s = @$\"\"\"quoted\"\" {x}\";"));
        Assert.Equal(LineKind.Comment, classifier.Classify("// a real comment"));
    }

    /// <summary>
    /// Verifies that a Java text block (<c>"""</c>) is tracked as a multi-line string, the
    /// same way as a C# raw string literal.
    /// </summary>
    [Fact]
    public void Classify_JavaTextBlock_IgnoresCommentTokensInside()
    {
        var classifier = new LineClassifier(Resolve(".java"));

        Assert.Equal(LineKind.Code, classifier.Classify("String sql = \"\"\""));
        Assert.Equal(LineKind.Code, classifier.Classify("    /* not a comment"));
        Assert.Equal(LineKind.Code, classifier.Classify("    \"\"\";"));
        Assert.Equal(LineKind.Code, classifier.Classify("int y = 1;"));
    }

    /// <summary>
    /// Verifies that a <c>/*</c> inside a regex literal (here, stripping trailing slashes)
    /// does not open a block comment that swallows the following lines.
    /// </summary>
    /// <param name="extension">A language with regex literals.</param>
    [Theory]
    [InlineData(".js")]
    [InlineData(".ts")]
    [InlineData(".vue")]
    [InlineData(".svelte")]
    [InlineData(".astro")]
    public void Classify_RegexLiteralContainingBlockCommentOpener_IsCode(string extension)
    {
        var classifier = new LineClassifier(Resolve(extension));

        Assert.Equal(LineKind.Code, classifier.Classify("const p = s.replace(/\\/*$/, \"\");"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("const a = 1;"));
        Assert.Equal(LineKind.Comment, classifier.Classify("// a real comment"));
    }

    /// <summary>
    /// Verifies the contexts in which a <c>/</c> starts a regex literal: at the start of the
    /// file, after an operator or opening bracket, and after a keyword like <c>return</c>.
    /// </summary>
    /// <param name="line">A line whose regex contains a <c>/*</c>.</param>
    [Theory]
    [InlineData("/\\/*/.test(s);")]
    [InlineData("return /\\/*/.test(s);")]
    [InlineData("x = cond ? /a\\/*/ : /b/;")]
    [InlineData("f(a, /[/*]/g);")]
    [InlineData("if (!/\\/*/.test(s)) {")]
    [InlineData("const f = s => /\\/*/.test(s);")]
    public void Classify_RegexLiteralAfterOperandPosition_DoesNotOpenBlockComment(string line)
    {
        var classifier = new LineClassifier(Resolve(".js"));

        Assert.Equal(LineKind.Code, classifier.Classify(line));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a <c>/</c> after an operand is division, so a block comment later on
    /// the same line still opens (including when a second division on the line could
    /// otherwise pair up with the first as a regex).
    /// </summary>
    /// <param name="line">A line with a division followed by a block-comment opener.</param>
    [Theory]
    [InlineData("const half = total / 2; /* start")]
    [InlineData("const r = (a + b) / c; /* start")]
    [InlineData("const r = arr[0] / 2; /* start")]
    [InlineData("const r = obj.return / 2; /* start")]
    [InlineData("const r = x++ / 2; /* start")]
    [InlineData("const r = \"s\".length / 2; /* start")]
    public void Classify_DivisionFollowedByBlockComment_OpensBlockComment(string line)
    {
        var classifier = new LineClassifier(Resolve(".js"));

        Assert.Equal(LineKind.Code, classifier.Classify(line));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("   still a comment */"));
    }

    /// <summary>
    /// Verifies that quotes and backticks inside a regex literal (or inside its character
    /// class) don't open a string that would hide a following comment.
    /// </summary>
    [Fact]
    public void Classify_RegexLiteralContainingQuotes_DoesNotOpenString()
    {
        var classifier = new LineClassifier(Resolve(".js"));

        Assert.Equal(LineKind.Code, classifier.Classify("const re = /[^`'\"]/;"));
        Assert.False(classifier.InMultilineString);
        Assert.Equal(LineKind.Comment, classifier.Classify("/* a real comment */"));
    }

    /// <summary>
    /// Verifies that a regex literal on a line after a keyword or operator ending the
    /// previous line is recognized, since the operand position carries across lines.
    /// </summary>
    [Fact]
    public void Classify_RegexLiteralAfterOperatorOnPreviousLine_IsCode()
    {
        var classifier = new LineClassifier(Resolve(".ts"));

        Assert.Equal(LineKind.Code, classifier.Classify("const re ="));
        Assert.Equal(LineKind.Code, classifier.Classify("    /\\/*$/;"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a regex literal starting a line is recognized when the previous line
    /// ended with a keyword such as <c>yield</c>, while one after an identifier ending the
    /// previous line is still division.
    /// </summary>
    [Fact]
    public void Classify_RegexLiteralAfterKeywordOnPreviousLine_IsCode()
    {
        var classifier = new LineClassifier(Resolve(".js"));

        Assert.Equal(LineKind.Code, classifier.Classify("yield"));
        Assert.Equal(LineKind.Code, classifier.Classify("  /\\/*/g;"));
        Assert.False(classifier.InBlockComment);

        Assert.Equal(LineKind.Code, classifier.Classify("const r = total"));
        Assert.Equal(LineKind.Code, classifier.Classify("  / 2; /* start"));
        Assert.True(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a PHP 8 attribute (<c>#[…]</c>) is code, while a <c>#</c> line comment
    /// is still a comment.
    /// </summary>
    [Fact]
    public void Classify_PhpAttribute_IsCodeNotComment()
    {
        var classifier = new LineClassifier(Resolve(".php"));

        Assert.Equal(LineKind.Code, classifier.Classify("#[Route(\"/home\")]"));
        Assert.Equal(LineKind.Code, classifier.Classify("    #[IsGranted('ROLE_USER')]"));
        Assert.Equal(LineKind.Comment, classifier.Classify("# a comment"));
        Assert.Equal(LineKind.Comment, classifier.Classify("#comment [not an attribute]"));
    }

    /// <summary>
    /// Verifies that a C/C++ digit separator (<c>1'000</c>) does not open a character
    /// literal that would hide a following block-comment opener.
    /// </summary>
    /// <param name="extension">The C or C++ file extension.</param>
    [Theory]
    [InlineData(".c")]
    [InlineData(".cpp")]
    public void Classify_DigitSeparator_DoesNotOpenCharLiteral(string extension)
    {
        var classifier = new LineClassifier(Resolve(extension));

        Assert.Equal(LineKind.Code, classifier.Classify("x = 1'000'000 + 0xFF'FF; /* start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("end */"));
    }

    /// <summary>
    /// Verifies that a character literal with an encoding prefix (<c>u8'a'</c>) is still a
    /// literal, not mistaken for a digit separator.
    /// </summary>
    [Fact]
    public void Classify_CppPrefixedCharLiteral_StillHidesCommentToken()
    {
        var classifier = new LineClassifier(Resolve(".cpp"));

        Assert.Equal(LineKind.Code, classifier.Classify("auto c = u8'/'; auto d = '*'; int e = 1;"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a C++ raw string literal hides comment tokens and spans lines.
    /// </summary>
    [Fact]
    public void Classify_CppRawString_HidesCommentTokensAcrossLines()
    {
        var classifier = new LineClassifier(Resolve(".cpp"));

        Assert.Equal(LineKind.Code, classifier.Classify("auto s = R\"(C:\\ /* not a comment"));
        Assert.True(classifier.InMultilineString);
        Assert.Equal(LineKind.Code, classifier.Classify("still \" string // */)\";"));
        Assert.False(classifier.InMultilineString);
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that a raw or unicode docstring (<c>r"""…"""</c>) beginning a statement is
    /// a comment, across lines too.
    /// </summary>
    [Fact]
    public void Classify_PythonPrefixedDocstring_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Comment, classifier.Classify("    r\"\"\"Raw \\d docstring.\"\"\""));
        Assert.Equal(LineKind.Comment, classifier.Classify("u'''Start"));
        Assert.Equal(LineKind.Comment, classifier.Classify("end'''"));
        Assert.Equal(LineKind.Code, classifier.Classify("x = r\"\"\"value\"\"\""));
    }

    /// <summary>
    /// Verifies that a triple-quoted string on a line that continues an expression inside
    /// open brackets is code, while a docstring after the brackets close is a comment.
    /// </summary>
    [Fact]
    public void Classify_PythonTripleQuoteInsideOpenBracket_IsCode()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Code, classifier.Classify("x = foo([1, 2],"));
        Assert.Equal(LineKind.Code, classifier.Classify("    \"\"\"argument\"\"\""));
        Assert.Equal(LineKind.Code, classifier.Classify(")"));
        Assert.Equal(LineKind.Comment, classifier.Classify("\"\"\"docstring\"\"\""));
    }

    /// <summary>
    /// Verifies that a triple-quoted string on a line continued with a trailing backslash is
    /// code, while the next statement's docstring is a comment.
    /// </summary>
    [Fact]
    public void Classify_PythonTripleQuoteAfterBackslashContinuation_IsCode()
    {
        var classifier = new LineClassifier(Resolve(".py"));

        Assert.Equal(LineKind.Code, classifier.Classify("x = \\"));
        Assert.Equal(LineKind.Code, classifier.Classify("    \"\"\"value\"\"\""));
        Assert.Equal(LineKind.Comment, classifier.Classify("\"\"\"docstring\"\"\""));
    }

    /// <summary>
    /// Verifies that Batch's <c>@REM</c> is a comment (case-insensitively), but not when it
    /// begins a longer word.
    /// </summary>
    [Fact]
    public void Classify_BatchAtRem_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".bat"));

        Assert.Equal(LineKind.Comment, classifier.Classify("@rem hi"));
        Assert.Equal(LineKind.Comment, classifier.Classify("  @REM"));
        Assert.Equal(LineKind.Code, classifier.Classify("@remove x"));
        Assert.Equal(LineKind.Code, classifier.Classify("@echo off"));
    }

    /// <summary>
    /// Verifies that a Lua long-bracket comment with <c>=</c> signs spans lines and is only
    /// closed by the matching level.
    /// </summary>
    [Fact]
    public void Classify_LuaLeveledBlockComment_ClosesOnMatchingLevel()
    {
        var classifier = new LineClassifier(Resolve(".lua"));

        Assert.Equal(LineKind.Comment, classifier.Classify("--[==[ start"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("t[a[1]] ]] ]=]"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("end ]==]"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("local x = 1"));
    }

    /// <summary>
    /// Verifies that a Nim <c>##[ ]##</c> doc comment spans lines.
    /// </summary>
    [Fact]
    public void Classify_NimDocBlockComment_SpansLines()
    {
        var classifier = new LineClassifier(Resolve(".nim"));

        Assert.Equal(LineKind.Comment, classifier.Classify("##[ doc"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("still doc"));
        Assert.Equal(LineKind.Comment, classifier.Classify("]##"));
        Assert.False(classifier.InBlockComment);
    }

    /// <summary>
    /// Verifies that Visual Basic's <c>REM</c> is a comment (case-insensitively), but not
    /// inside a longer identifier.
    /// </summary>
    [Fact]
    public void Classify_VisualBasicRem_ReturnsComment()
    {
        var classifier = new LineClassifier(Resolve(".vb"));

        Assert.Equal(LineKind.Comment, classifier.Classify("REM a comment"));
        Assert.Equal(LineKind.Comment, classifier.Classify("    rem lower case"));
        Assert.Equal(LineKind.Code, classifier.Classify("REMOVE(x)"));
        Assert.Equal(LineKind.Comment, classifier.Classify("' quote comment"));
    }

    /// <summary>
    /// Verifies that a Razor <c>@* *@</c> comment in a <c>.cshtml</c> file spans lines.
    /// </summary>
    [Fact]
    public void Classify_RazorComment_SpansLines()
    {
        var classifier = new LineClassifier(Resolve(".cshtml"));

        Assert.Equal(LineKind.Comment, classifier.Classify("@* razor"));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("comment *@"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("<p>@Model.Name</p>"));
    }

    /// <summary>
    /// Verifies that Perl POD, from a command paragraph at column 0 through <c>=cut</c>,
    /// counts as comment lines.
    /// </summary>
    /// <param name="command">The POD command that starts the block.</param>
    [Theory]
    [InlineData("=pod")]
    [InlineData("=head1 NAME")]
    public void Classify_PerlPod_CountsAsComment(string command)
    {
        var classifier = new LineClassifier(Resolve(".pl"));

        Assert.Equal(LineKind.Comment, classifier.Classify(command));
        Assert.True(classifier.InBlockComment);
        Assert.Equal(LineKind.Comment, classifier.Classify("my $x = 1; # documentation text"));
        Assert.Equal(LineKind.Comment, classifier.Classify("=cut"));
        Assert.False(classifier.InBlockComment);
        Assert.Equal(LineKind.Code, classifier.Classify("my $y = 2;"));
    }

    /// <summary>
    /// Verifies that a POD-like command that is not at column 0 is ordinary code.
    /// </summary>
    [Fact]
    public void Classify_PerlIndentedPodCommand_IsCode()
    {
        var classifier = new LineClassifier(Resolve(".pl"));

        Assert.Equal(LineKind.Code, classifier.Classify("  =pod"));
        Assert.False(classifier.InBlockComment);
    }
}
