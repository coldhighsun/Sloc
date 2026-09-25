using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitAttributesRules"/> attribute matching.
/// </summary>
public class GitAttributesRulesTests
{
    /// <summary>
    /// Verifies that <c>linguist-vendored=false</c> is treated the same as unsetting the
    /// attribute, and that an unrelated attribute on the same line is ignored.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_ExplicitFalseValue_IsNotVendored()
    {
        var rules = GitAttributesRules.FromLines(string.Empty, ["*.min.js linguist-vendored=false text=auto"]);

        Assert.False(rules.IsVendoredOrGenerated("app.min.js"));
    }

    /// <summary>
    /// Verifies that a path matching a <c>linguist-generated</c> pattern is reported as
    /// generated.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_LinguistGenerated_MatchesPattern()
    {
        var rules = GitAttributesRules.FromLines(string.Empty, ["*.g.cs linguist-generated"]);

        Assert.True(rules.IsVendoredOrGenerated("Models/Widget.g.cs"));
        Assert.False(rules.IsVendoredOrGenerated("Models/Widget.cs"));
    }

    /// <summary>
    /// Verifies that a path matching a <c>linguist-vendored</c> pattern is reported as
    /// vendored, and a non-matching path is not.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_LinguistVendored_MatchesPattern()
    {
        var rules = GitAttributesRules.FromLines(string.Empty, ["vendor/* linguist-vendored"]);

        Assert.True(rules.IsVendoredOrGenerated("vendor/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("src/app.js"));
    }

    /// <summary>
    /// Verifies that a nested <c>.gitattributes</c> file's patterns are scoped to its own
    /// subtree and do not match sibling paths outside it.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_NestedFile_IsScopedToItsSubtree()
    {
        var rules = GitAttributesRules.FromLines("third_party", ["**/*.js linguist-vendored"]);

        Assert.True(rules.IsVendoredOrGenerated("third_party/lib/dep.js"));
        Assert.False(rules.IsVendoredOrGenerated("src/app.js"));
    }

    /// <summary>
    /// Verifies that a line with no recognized linguist attribute (e.g. only <c>text=auto</c>)
    /// does not affect the result.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_UnrelatedAttribute_IsIgnored()
    {
        var rules = GitAttributesRules.FromLines(string.Empty, ["*.sh text=auto eol=lf"]);

        Assert.False(rules.IsVendoredOrGenerated("run.sh"));
        Assert.True(rules.IsEmpty);
    }

    /// <summary>
    /// Verifies that a bare directory pattern (trailing slash, no wildcard) applies to
    /// every file nested under that directory, not just the directory path itself.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_DirectoryOnlyPattern_MatchesNestedFiles()
    {
        var rules = GitAttributesRules.FromLines(string.Empty, ["vendor/ linguist-vendored"]);

        Assert.True(rules.IsVendoredOrGenerated("vendor/lib/thing.js"));
        Assert.True(rules.IsVendoredOrGenerated("vendor/thing.js"));
        Assert.False(rules.IsVendoredOrGenerated("src/vendor-adjacent.js"));
    }

    /// <summary>
    /// Verifies that unsetting the attribute with a leading <c>-</c> is recognized as
    /// "not vendored", overriding an earlier matching pattern that set it.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_UnsetAttribute_OverridesEarlierSet()
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "vendor/** linguist-vendored",
            "vendor/keep.js -linguist-vendored"
        ]);

        Assert.True(rules.IsVendoredOrGenerated("vendor/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("vendor/keep.js"));
    }

    /// <summary>
    /// Verifies that a pattern whose character class can't be translated to a valid regex
    /// is skipped rather than throwing, and the file's other patterns still apply.
    /// </summary>
    [Fact]
    public void FromLines_InvalidCharacterClass_SkipsPatternInsteadOfThrowing()
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "gen[z-a].cs linguist-generated",
            "vendor/** linguist-vendored"
        ]);

        Assert.True(rules.IsVendoredOrGenerated("vendor/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("src/app.cs"));
    }

    /// <summary>
    /// Verifies that <c>!attr</c> resets the attribute to unspecified and, being decided by a
    /// later line, blocks an earlier line that set it.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_ResetAttribute_BlocksEarlierSet()
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "*.js linguist-vendored",
            "keep.js !linguist-vendored"
        ]);

        Assert.True(rules.IsVendoredOrGenerated("lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("keep.js"));
    }

    /// <summary>
    /// Verifies that a macro defined in the top-level file expands (transitively) only when
    /// it is set, not when it is unset, reset, or given a value.
    /// </summary>
    /// <param name="assignment">The macro assignment on the pattern line.</param>
    /// <param name="expected">Whether the file is expected to be vendored.</param>
    [Theory]
    [InlineData("thirdparty", true)]
    [InlineData("external", true)]
    [InlineData("-thirdparty", false)]
    [InlineData("!thirdparty", false)]
    [InlineData("thirdparty=yes", false)]
    public void IsVendoredOrGenerated_TopLevelMacro_ExpandsWhenSet(string assignment, bool expected)
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "[attr]thirdparty linguist-vendored",
            "[attr]external thirdparty",
            $"v/** {assignment}"
        ]);

        Assert.Equal(expected, rules.IsVendoredOrGenerated("v/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("src/app.js"));
    }

    /// <summary>
    /// Verifies that a later macro definition replaces an earlier one.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_RedefinedMacro_LastDefinitionWins()
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "[attr]thirdparty linguist-vendored",
            "[attr]thirdparty text",
            "v/** thirdparty"
        ]);

        Assert.False(rules.IsVendoredOrGenerated("v/lib.js"));
    }

    /// <summary>
    /// Verifies that a macro definition in a nested <c>.gitattributes</c> file is ignored, as
    /// git only allows macros in the top-level file, so it neither defines nor overrides one.
    /// </summary>
    [Fact]
    public void FromFiles_MacroInNestedFile_IsIgnored()
    {
        var rules = GitAttributesRules.FromFiles(
        [
            new GitAttributesRules.AttributesFile(string.Empty, GitAttributesRules.ParseLines(
            [
                "[attr]thirdparty linguist-vendored",
                "*.js thirdparty"
            ])),
            new GitAttributesRules.AttributesFile("sub", GitAttributesRules.ParseLines(
            [
                "[attr]thirdparty -linguist-vendored",
                "[attr]nested linguist-generated",
                "*.cs nested"
            ]))
        ], ignoreCase: true);

        Assert.True(rules.IsVendoredOrGenerated("sub/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("sub/app.cs"));
    }

    /// <summary>
    /// Verifies that a line in a deeper <c>.gitattributes</c> file takes precedence over one in
    /// a shallower file, regardless of the order the files are supplied in.
    /// </summary>
    [Fact]
    public void FromFiles_DeeperFile_TakesPrecedence()
    {
        var rules = GitAttributesRules.FromFiles(
        [
            new GitAttributesRules.AttributesFile("vendor", GitAttributesRules.ParseLines(["keep.js !linguist-vendored"])),
            new GitAttributesRules.AttributesFile(string.Empty, GitAttributesRules.ParseLines(["vendor/** linguist-vendored"]))
        ], ignoreCase: true);

        Assert.True(rules.IsVendoredOrGenerated("vendor/lib.js"));
        Assert.False(rules.IsVendoredOrGenerated("vendor/keep.js"));
    }

    /// <summary>
    /// Verifies that a C-style quoted pattern is unquoted, including octal-escaped UTF-8 bytes.
    /// </summary>
    /// <param name="line">The attributes line.</param>
    /// <param name="path">The path expected to match.</param>
    [Theory]
    [InlineData("\"my file.cs\" linguist-vendored", "my file.cs")]
    [InlineData("\"tab\\there.cs\" linguist-vendored", "tab\there.cs")]
    [InlineData("\"caf\\303\\251.cs\" linguist-generated", "caf\u00e9.cs")]
    [InlineData("\"docs/a b\"linguist-vendored", "docs/a b")]
    public void IsVendoredOrGenerated_QuotedPattern_IsUnquoted(string line, string path)
    {
        var rules = GitAttributesRules.FromLines(string.Empty, [line]);

        Assert.True(rules.IsVendoredOrGenerated(path));
    }

    /// <summary>
    /// Verifies that a line with a negative pattern is ignored (git rejects negative patterns
    /// in attributes files), while an escaped <c>\!</c> matches a literal <c>!</c>.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_NegativePattern_IgnoresLine()
    {
        var rules = GitAttributesRules.FromLines(string.Empty,
        [
            "*.js linguist-vendored",
            "!a.js -linguist-vendored",
            "\\!b.js -linguist-vendored"
        ]);

        Assert.True(rules.IsVendoredOrGenerated("!a.js"));
        Assert.False(rules.IsVendoredOrGenerated("!b.js"));
    }

    /// <summary>
    /// Verifies that an invalid or reserved attribute name voids the whole line, including its
    /// otherwise valid assignments, as in git.
    /// </summary>
    /// <param name="line">The attributes line.</param>
    [Theory]
    [InlineData("*.js linguist-vendored # third-party")]
    [InlineData("*.js linguist-vendored builtin_objectmode")]
    [InlineData("*.js linguist-vendored --text")]
    [InlineData("*.js linguist-vendored !")]
    [InlineData("*.js linguist-vendored caf\u00e9")]
    public void IsVendoredOrGenerated_InvalidAttributeName_VoidsLine(string line)
    {
        var rules = GitAttributesRules.FromLines(string.Empty, [line]);

        Assert.False(rules.IsVendoredOrGenerated("lib.js"));
        Assert.True(rules.IsEmpty);
    }

    /// <summary>
    /// Verifies that a line of 2048 bytes or more is ignored, matching git's
    /// <c>ATTR_MAX_LINE_LENGTH</c>.
    /// </summary>
    [Fact]
    public void IsVendoredOrGenerated_OverlyLongLine_IsIgnored()
    {
        var padding = new string(' ', 2048);
        var rules = GitAttributesRules.FromLines(string.Empty, ["*.js linguist-vendored" + padding]);

        Assert.False(rules.IsVendoredOrGenerated("lib.js"));
    }

    /// <summary>
    /// Verifies that C-style unquoting decodes git's escapes and rejects unterminated strings
    /// and unknown or truncated escapes.
    /// </summary>
    /// <param name="text">The quoted text.</param>
    /// <param name="expected">The decoded value, or <see langword="null"/> when decoding fails.</param>
    [Theory]
    [InlineData("\"plain\" rest", "plain")]
    [InlineData("\"a\\\\b\\\"c\"", "a\\b\"c")]
    [InlineData("\"\\a\\b\\f\\n\\r\\t\\v\"", "\a\b\f\n\r\t\v")]
    [InlineData("\"\\101\"", "A")]
    [InlineData("\"unterminated", null)]
    [InlineData("\"bad\\q\"", null)]
    [InlineData("\"bad\\4\"", null)]
    [InlineData("\"short\\12\"", null)]
    public void TryUnquoteCStyle_QuotedText_DecodesLikeGit(string text, string? expected)
    {
        var decoded = AttributeLine.TryUnquoteCStyle(text, 0, out var value, out _);

        Assert.Equal(expected is not null, decoded);
        if (expected is not null)
        {
            Assert.Equal(expected, value);
        }
    }
}