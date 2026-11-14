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
    /// Verifies that the languages added in the review pass resolve from their extensions.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".groovy", "Groovy")]
    [InlineData(".gradle", "Groovy")]
    [InlineData(".jl", "Julia")]
    [InlineData(".clj", "Clojure")]
    [InlineData(".cljs", "Clojure")]
    [InlineData(".vue", "Vue")]
    [InlineData(".svelte", "Svelte")]
    [InlineData(".proto", "Protobuf")]
    [InlineData(".bat", "Batch")]
    [InlineData(".cmd", "Batch")]
    [InlineData(".asm", "Assembly")]
    [InlineData(".s", "Assembly")]
    [InlineData(".ini", "INI")]
    [InlineData(".editorconfig", "INI")]
    [InlineData(".ipynb", "Jupyter Notebook")]
    public void TryGetByExtension_ReviewLanguages_Resolve(string extension, string expectedName)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that BASIC and Pascal extensions resolve to the correct language.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".bas", "BASIC")]
    [InlineData(".pas", "Pascal")]
    [InlineData(".pp", "Pascal")]
    [InlineData(".inc", "Pascal")]
    public void TryGetByExtension_BasicAndPascal_Resolve(string extension, string expectedName)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that the Astro, MDX, D, and Crystal extensions resolve to the correct language.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".astro", "Astro")]
    [InlineData(".mdx", "MDX")]
    [InlineData(".d", "D")]
    [InlineData(".cr", "Crystal")]
    public void TryGetByExtension_NewlyAddedLanguages_Resolve(string extension, string expectedName)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that files identified by name (e.g. Makefile, Dockerfile) resolve to the
    /// correct language regardless of directory, including the alternate spellings the
    /// owning tool itself accepts (e.g. make's <c>makefile</c>, Docker's <c>dockerfile</c>).
    /// </summary>
    /// <param name="path">A file path to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData("Makefile", "Makefile")]
    [InlineData("makefile", "Makefile")]
    [InlineData("src/GNUmakefile", "Makefile")]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("dockerfile", "Dockerfile")]
    [InlineData("deploy/Containerfile", "Dockerfile")]
    [InlineData("CMakeLists.txt", "CMake")]
    [InlineData("Rakefile", "Ruby")]
    [InlineData("rakefile", "Ruby")]
    [InlineData("Gemfile", "Ruby")]
    public void TryGetByPath_KnownFilename_ResolvesLanguage(string path, string expectedName)
    {
        var found = LanguageRegistry.TryGetByPath(path, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that file names match case-insensitively (tools find e.g. <c>DockerFile</c>
    /// on a case-insensitive filesystem), except Bazel's, which must match exactly: a
    /// <c>build</c> shell script is not a Bazel <c>BUILD</c> file.
    /// </summary>
    /// <param name="path">A file path to look up.</param>
    /// <param name="expectedName">The expected language name, or <see langword="null"/> if none.</param>
    [Theory]
    [InlineData("build", null)]
    [InlineData("scripts/Build", null)]
    [InlineData("workspace", null)]
    [InlineData("DockerFile", "Dockerfile")]
    [InlineData("gemfile", "Ruby")]
    [InlineData("CMakelists.txt", "CMake")]
    public void TryGetByPath_FilenameCasing_OnlyBazelNamesAreCaseSensitive(string path, string? expectedName)
    {
        var found = LanguageRegistry.TryGetByPath(path, out var language);

        Assert.Equal(expectedName is not null, found);
        Assert.Equal(expectedName, language?.Name);
    }

    /// <summary>
    /// Verifies that the MSBuild script and Text extensions resolve to the correct language.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".csproj", "MSBuild script")]
    [InlineData(".wixproj", "MSBuild script")]
    [InlineData(".msbuild", "MSBuild script")]
    [InlineData(".txt", "Text")]
    [InlineData(".text", "Text")]
    public void TryGetByExtension_MsBuildAndText_Resolve(string extension, string expectedName)
    {
        var found = LanguageRegistry.TryGetByExtension(extension, out var language);

        Assert.True(found);
        Assert.Equal(expectedName, language!.Name);
    }

    /// <summary>
    /// Verifies that <c>*.Designer.cs</c> files resolve to "C# Designer" by filename
    /// suffix, case-insensitively, while other <c>.cs</c> files still resolve to "C#".
    /// </summary>
    /// <param name="path">A file path to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData("Form1.Designer.cs", "C# Designer")]
    [InlineData("Form1.DESIGNER.cs", "C# Designer")]
    [InlineData("src/Form1.designer.cs", "C# Designer")]
    [InlineData("Form1.cs", "C#")]
    public void TryGetByPath_DesignerSuffix_ResolvesCSharpDesigner(string path, string expectedName)
    {
        var found = LanguageRegistry.TryGetByPath(path, out var language);

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

    /// <summary>
    /// Verifies that every MSBuild project file extension resolves to the same
    /// "MSBuild script" language, including Visual Basic, F#, and VC++ projects.
    /// </summary>
    /// <param name="extension">The project file extension to resolve.</param>
    [Theory]
    [InlineData(".csproj")]
    [InlineData(".vbproj")]
    [InlineData(".fsproj")]
    [InlineData(".vcxproj")]
    public void TryGetByExtension_ProjectFile_ResolvesMsBuildScript(string extension)
    {
        Assert.True(LanguageRegistry.TryGetByExtension(extension, out var language));
        Assert.Equal("MSBuild script", language.Name);
    }

    /// <summary>
    /// Verifies that the extensions of the newer languages resolve to the expected language.
    /// </summary>
    /// <param name="extension">An extension to look up.</param>
    /// <param name="expectedName">The expected language name.</param>
    [Theory]
    [InlineData(".razor", "Razor")]
    [InlineData(".xaml", "XAML")]
    [InlineData(".axaml", "XAML")]
    [InlineData(".f90", "Fortran")]
    [InlineData(".for", "Fortran")]
    [InlineData(".cob", "COBOL")]
    [InlineData(".cbl", "COBOL")]
    [InlineData(".scm", "Scheme")]
    [InlineData(".rkt", "Racket")]
    [InlineData(".lisp", "Common Lisp")]
    [InlineData(".el", "Emacs Lisp")]
    [InlineData(".vh", "Verilog")]
    [InlineData(".sv", "SystemVerilog")]
    [InlineData(".vhd", "VHDL")]
    [InlineData(".tex", "TeX")]
    public void TryGetByExtension_NewerLanguages_Resolve(string extension, string expectedName)
    {
        Assert.True(LanguageRegistry.TryGetByExtension(extension, out var language));
        Assert.Equal(expectedName, language.Name);
    }

    /// <summary>
    /// Verifies that extensions shared with far more common languages (OpenCL, Apex, VB6, Coq)
    /// are deliberately not claimed by Common Lisp, TeX, or Verilog.
    /// </summary>
    /// <param name="extension">An ambiguous extension that must stay unresolved.</param>
    [Theory]
    [InlineData(".cl")]
    [InlineData(".cls")]
    [InlineData(".v")]
    public void TryGetByExtension_AmbiguousExtensions_AreNotClaimed(string extension)
    {
        Assert.False(LanguageRegistry.TryGetByExtension(extension, out _));
    }

    /// <summary>
    /// Verifies the number of built-in languages. The count is quoted in CLAUDE.md, the READMEs,
    /// the NuGet description, and the Homebrew formula, so update those when this changes.
    /// </summary>
    [Fact]
    public void Languages_BuiltInCount_MatchesDocumentedCount()
    {
        Assert.Equal(78, LanguageRegistry.Languages.Count);
    }

    /// <summary>
    /// Verifies that XAML files are reported as XAML and no longer fall under XML.
    /// </summary>
    [Fact]
    public void TryGetByExtension_Xaml_IsNotXml()
    {
        Assert.True(LanguageRegistry.TryGetByExtension(".xaml", out var language));
        Assert.Equal("XAML", language.Name);
        Assert.DoesNotContain(".xaml", LanguageRegistry.Languages.Single(l => l.Name == "XML").Extensions);
    }

    /// <summary>
    /// Verifies that Fortran and COBOL, whose fixed-form comments are not modeled, do not
    /// report comment health (which would be a misleading ~0%).
    /// </summary>
    /// <param name="extension">An extension of a Fortran or COBOL file.</param>
    [Theory]
    [InlineData(".f90")]
    [InlineData(".f")]
    [InlineData(".cbl")]
    public void SupportsHealth_FixedFormLanguages_IsFalse(string extension)
    {
        Assert.True(LanguageRegistry.TryGetByExtension(extension, out var language));
        Assert.False(language.SupportsHealth);
    }
}
