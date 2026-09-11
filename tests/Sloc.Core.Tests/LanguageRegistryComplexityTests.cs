using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="LanguageRegistry.SupportsComplexity(string?)"/> and
/// <see cref="LanguageDefinition.SupportsComplexity"/>.
/// </summary>
public class LanguageRegistryComplexityTests
{
    /// <summary>
    /// Verifies that mainstream C-style languages with a populated
    /// <see cref="LanguageDefinition.ComplexityKeywords"/> list are reported as supported.
    /// </summary>
    /// <param name="name">The language display name.</param>
    [Theory]
    [InlineData("C#")]
    [InlineData("Java")]
    [InlineData("JavaScript")]
    [InlineData("TypeScript")]
    [InlineData("Python")]
    [InlineData("Go")]
    [InlineData("Rust")]
    public void SupportsComplexity_MainstreamLanguage_ReturnsTrue(string name)
    {
        Assert.True(LanguageRegistry.SupportsComplexity(name));
    }

    /// <summary>
    /// Verifies that a language with no <see cref="LanguageDefinition.ComplexityKeywords"/>
    /// entries (e.g. a markup/data language) is reported as unsupported.
    /// </summary>
    [Fact]
    public void SupportsComplexity_UnsupportedLanguage_ReturnsFalse()
    {
        Assert.False(LanguageRegistry.SupportsComplexity("YAML"));
        Assert.False(LanguageRegistry.SupportsComplexity("JSON"));
    }

    /// <summary>
    /// Verifies that an unknown or <see langword="null"/> language name is treated as
    /// unsupported rather than throwing.
    /// </summary>
    [Fact]
    public void SupportsComplexity_UnknownOrNullName_ReturnsFalse()
    {
        Assert.False(LanguageRegistry.SupportsComplexity("Nonexistent"));
        Assert.False(LanguageRegistry.SupportsComplexity(null));
    }

    /// <summary>
    /// Verifies that <see cref="LanguageDefinition.SupportsComplexity"/> matches the
    /// presence of at least one keyword directly on the definition.
    /// </summary>
    [Fact]
    public void SupportsComplexity_Definition_MatchesKeywordPresence()
    {
        LanguageRegistry.TryGetByExtension(".cs", out var csharp);
        LanguageRegistry.TryGetByExtension(".json", out var json);

        Assert.True(csharp!.SupportsComplexity);
        Assert.False(json!.SupportsComplexity);
    }
}
