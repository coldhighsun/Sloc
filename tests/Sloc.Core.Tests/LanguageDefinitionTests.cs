using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="LanguageDefinition"/> and <see cref="StringLiteral"/>.
/// </summary>
public class LanguageDefinitionTests
{
    /// <summary>
    /// Verifies that <see cref="StringLiteral.Closing"/> falls back to <see cref="StringLiteral.Delimiter"/>
    /// when no distinct <see cref="StringLiteral.CloseDelimiter"/> is set.
    /// </summary>
    [Fact]
    public void StringLiteral_Closing_DefaultsToDelimiter()
    {
        var literal = new StringLiteral("\"");

        Assert.Equal("\"", literal.Closing);
    }

    /// <summary>
    /// Verifies that <see cref="StringLiteral.Closing"/> returns the explicit
    /// <see cref="StringLiteral.CloseDelimiter"/> when one is set, as with C# verbatim
    /// strings (<c>@"…"</c>) and Rust hash-delimited raw strings (<c>r#"…"#</c>).
    /// </summary>
    [Fact]
    public void StringLiteral_Closing_UsesExplicitCloseDelimiter()
    {
        var literal = new StringLiteral("@\"", CloseDelimiter: "\"");

        Assert.Equal("\"", literal.Closing);
    }

    /// <summary>
    /// Verifies that a language with at least one line-comment token and
    /// <see cref="LanguageDefinition.ShowHealth"/> left at its default (<see langword="true"/>)
    /// supports comment-health analysis.
    /// </summary>
    [Fact]
    public void SupportsHealth_ShowHealthTrueWithLineComment_ReturnsTrue()
    {
        var language = new LanguageDefinition { Name = "Test", LineCommentTokens = ["#"] };

        Assert.True(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that a language with only block comments (no line-comment tokens) still
    /// supports comment-health analysis.
    /// </summary>
    [Fact]
    public void SupportsHealth_OnlyBlockComments_ReturnsTrue()
    {
        var language = new LanguageDefinition
        {
            Name = "Test",
            BlockComments = [new BlockComment("/*", "*/")]
        };

        Assert.True(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that <see cref="LanguageDefinition.ShowHealth"/> set to <see langword="false"/>
    /// suppresses health support even when comment tokens are defined, as with data/markup
    /// languages (YAML, JSON, XML).
    /// </summary>
    [Fact]
    public void SupportsHealth_ShowHealthFalse_ReturnsFalseEvenWithCommentTokens()
    {
        var language = new LanguageDefinition
        {
            Name = "Test",
            LineCommentTokens = ["#"],
            ShowHealth = false
        };

        Assert.False(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that a language with no comment tokens at all (e.g. a pure data format)
    /// does not support health analysis even if <see cref="LanguageDefinition.ShowHealth"/>
    /// is left at its default.
    /// </summary>
    [Fact]
    public void SupportsHealth_NoCommentTokensOrBlocks_ReturnsFalse()
    {
        var language = new LanguageDefinition { Name = "Test" };

        Assert.False(language.SupportsHealth);
    }
}
