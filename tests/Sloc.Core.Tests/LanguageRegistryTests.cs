using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="LanguageRegistry"/>.
/// </summary>
public class LanguageRegistryTests
{
    /// <summary>
    /// Verifies that the registry exposes at least one language definition.
    /// </summary>
    [Fact]
    public void Languages_AreNotEmpty()
    {
        Assert.NotEmpty(LanguageRegistry.Languages);
    }

    /// <summary>
    /// Verifies that known C# extensions, with or without a leading dot and
    /// regardless of casing, resolve to the C# language definition.
    /// </summary>
    /// <param name="extension">
    /// An extension string to look up.
    /// </param>
    [Theory]
    [InlineData(".cs")]
    [InlineData(".CS")]
    [InlineData("cs")]
    public void TryGetByExtension_KnownExtension_ResolvesCSharp(string extension)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal("C#", language!.Name);
    }

    /// <summary>
    /// Verifies that known JSON extensions, with or without a leading dot and
    /// regardless of casing, resolve to the JSON language definition.
    /// </summary>
    /// <param name="extension">An extension string to look up.</param>
    [Theory]
    [InlineData(".json")]
    [InlineData(".jsonc")]
    [InlineData(".JSON")]
    [InlineData("json")]
    public void TryGetByExtension_KnownExtension_ResolvesJson(string extension)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal("JSON", language!.Name);
    }

    /// <summary>
    /// Verifies that an unknown, empty, or null extension causes the lookup to return
    /// <see langword="false"/> with a null output.
    /// </summary>
    /// <param name="extension">
    /// An extension string that should not resolve to any language.
    /// </param>
    [Theory]
    [InlineData(".zzz")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetByExtension_UnknownOrEmpty_ReturnsFalse(string? extension)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.False(found);
        Assert.Null(language);
    }

    /// <summary>
    /// Verifies that a full file path with a known extension resolves to the correct
    /// language definition.
    /// </summary>
    [Fact]
    public void TryGetByPath_KnownExtension_ResolvesFromPath()
    {
        var found = LanguageRegistry.TryGetByPath("/some/dir/Program.cs", out var language);

        Assert.True(found);
        Assert.Equal("C#", language!.Name);
    }

    /// <summary>
    /// Verifies that a file path with an unrecognized extension causes the lookup to return
    /// <see langword="false"/> with a null output.
    /// </summary>
    [Fact]
    public void TryGetByPath_UnknownExtension_ReturnsFalse()
    {
        var found = LanguageRegistry.TryGetByPath("notes.unknownext", out var language);

        Assert.False(found);
        Assert.Null(language);
    }

    /// <summary>
    /// Verifies that the newly added languages resolve from their extensions.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".dart", "Dart")]
    [InlineData(".scala", "Scala")]
    [InlineData(".lua", "Lua")]
    [InlineData(".hs", "Haskell")]
    [InlineData(".m", "Objective-C")]
    [InlineData(".toml", "TOML")]
    [InlineData(".md", "Markdown")]
    [InlineData(".tf", "Terraform")]
    [InlineData(".ex", "Elixir")]
    [InlineData(".pl", "Perl")]
    [InlineData(".r", "R")]
    public void TryGetByExtension_NewLanguages_Resolve(string extension, string expectedName)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that languages with comment tokens and <c>ShowHealth</c> support health
    /// analysis, while data/markup languages and unknown names do not.
    /// </summary>
    /// <param name="name">The language display name to check.</param>
    /// <param name="expected">Whether health analysis is expected to be supported.</param>
    [Theory]
    [InlineData("C#", true)]
    [InlineData("Python", true)]
    [InlineData("JSON", false)]
    [InlineData("YAML", false)]
    [InlineData("Nonexistent", false)]
    public void SupportsHealth_MatchesLanguageMetadata(string name, bool expected)
    {
        Assert.Equal(expected, LanguageRegistry.SupportsHealth(name));
    }
}