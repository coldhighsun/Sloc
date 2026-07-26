using Sloc.Core;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitIgnoreRules"/> pattern matching.
/// </summary>
public class GitIgnoreRulesTests
{
    /// <summary>
    /// Verifies that a bare name matches at any depth, and a non-matching path does not.
    /// </summary>
    [Theory]
    [InlineData("node_modules", "node_modules/pkg/index.js", true)]
    [InlineData("node_modules", "src/node_modules/x.js", true)]
    [InlineData("node_modules", "src/app.js", false)]
    public void IsIgnored_BareName_MatchesAtAnyDepth(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that an extension glob matches files with that extension anywhere.
    /// </summary>
    [Theory]
    [InlineData("*.log", "app.log", true)]
    [InlineData("*.log", "logs/app.log", true)]
    [InlineData("*.log", "app.txt", false)]
    public void IsIgnored_ExtensionGlob_Matches(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a leading slash anchors the pattern to the base directory root.
    /// </summary>
    [Theory]
    [InlineData("/build", "build/out.js", true)]
    [InlineData("/build", "src/build/out.js", false)]
    public void IsIgnored_AnchoredPattern_MatchesOnlyAtRoot(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a trailing-slash pattern only ignores paths under a directory of
    /// that name, not a file of that name.
    /// </summary>
    [Fact]
    public void IsIgnored_DirectoryOnly_IgnoresContentsNotSameNamedFile()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, ["dist/"]);

        Assert.True(rules.IsIgnored("dist/bundle.js"));
        Assert.False(rules.IsIgnored("dist"));
    }

    /// <summary>
    /// Verifies that a later negation re-includes a path excluded by an earlier pattern.
    /// </summary>
    [Fact]
    public void IsIgnored_Negation_ReincludesPath()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, ["*.log", "!keep.log"]);

        Assert.True(rules.IsIgnored("app.log"));
        Assert.False(rules.IsIgnored("keep.log"));
    }

    /// <summary>
    /// Verifies that comments, blank lines, and surrounding whitespace are ignored.
    /// </summary>
    [Fact]
    public void FromLines_IgnoresCommentsAndBlankLines()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, ["", "# a comment", "  ", "*.tmp"]);

        Assert.True(rules.IsIgnored("x.tmp"));
        Assert.False(rules.IsIgnored("x.cs"));
    }

    /// <summary>
    /// Verifies that a leading <c>**/</c> matches the trailing pattern at any depth.
    /// </summary>
    [Theory]
    [InlineData("**/temp", "temp", true)]
    [InlineData("**/temp", "a/b/temp", true)]
    public void IsIgnored_LeadingDoubleStar_MatchesAnyDepth(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a middle <c>**</c> matches zero or more intermediate directories.
    /// </summary>
    [Theory]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/x", false)]
    public void IsIgnored_MiddleDoubleStar_MatchesNestedDirs(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a self-referential directory symlink does not make the search for
    /// <c>.gitignore</c> files recurse forever.
    /// </summary>
    [Fact]
    public async Task Load_SymlinkedDirectoryLoop_DoesNotRecurseForever()
    {
        var root = Path.Combine(Path.GetTempPath(), "sloc-gitignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");

            var linkPath = Path.Combine(root, "loop");
            try
            {
                Directory.CreateSymbolicLink(linkPath, root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory symlinks require a privilege this environment may not grant.
                return;
            }

            GitIgnoreRules rules;
            try
            {
                rules = await Task.Run(() => GitIgnoreRules.Load(root, new HashSet<string>()))
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail("Load did not complete; likely stuck in a symlink loop.");
                return;
            }

            Assert.True(rules.IsIgnored("app.log"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
