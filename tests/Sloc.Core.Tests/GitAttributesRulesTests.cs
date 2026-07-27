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
}