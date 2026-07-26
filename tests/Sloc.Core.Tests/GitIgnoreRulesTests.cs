using Sloc.Core;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitIgnoreRules"/> pattern matching.
/// </summary>
public class GitIgnoreRulesTests
{
    /// <summary>
    /// Verifies the precedence order documented on <see cref="GitIgnoreRules.Load"/>: the
    /// repo-local <c>.git/info/exclude</c> is lowest precedence among the tiers testable
    /// without touching the real user profile, so a later, more specific <c>.gitignore</c>
    /// pattern (negation) can override it. The global <c>core.excludesFile</c> tier sits
    /// below this and is covered separately by <see cref="TryParseExcludesFile_ExtractsCoreSectionSetting"/>
    /// at the parsing level — exercising it end-to-end would require pointing
    /// <c>~/.gitconfig</c> at a temp file, which is not safe to do from a test that may run
    /// concurrently with others on a developer's real machine.
    /// </summary>
    [Fact]
    public void Load_RepoExcludeIsOverriddenByLaterGitignoreNegation()
    {
        var root = Path.Combine(Path.GetTempPath(), "sloc-gitignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "info"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".git", "info", "exclude"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "!keep.log\n");

            var rules = GitIgnoreRules.Load(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git" });

            Assert.True(rules.IsIgnored("debug.log"));
            Assert.False(rules.IsIgnored("keep.log"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


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
    /// Verifies that pattern matching is case-insensitive, consistent with the
    /// <c>--include</c>/<c>--exclude</c> glob matching used elsewhere in the scanner.
    /// </summary>
    [Theory]
    [InlineData("Node_Modules", "node_modules/pkg/index.js", true)]
    [InlineData("*.LOG", "debug.log", true)]
    [InlineData("build", "SRC/BUILD/out.txt", true)]
    public void IsIgnored_Pattern_MatchesCaseInsensitively(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a character class matches any one of its listed characters, and that
    /// a leading <c>!</c> negates the class (matching any character not listed).
    /// </summary>
    [Theory]
    [InlineData("file[123].txt", "file1.txt", true)]
    [InlineData("file[123].txt", "file4.txt", false)]
    [InlineData("file[!123].txt", "file4.txt", true)]
    [InlineData("file[!123].txt", "file1.txt", false)]
    public void IsIgnored_CharacterClass_MatchesListedCharacters(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a literal <c>^</c> inside a character class matches the caret
    /// character itself rather than being interpreted as a regex negation, since
    /// gitignore character classes only support <c>!</c> for negation.
    /// </summary>
    [Theory]
    [InlineData("file[^12].txt", "file^.txt", true)]
    [InlineData("file[^12].txt", "file1.txt", true)]
    [InlineData("file[^12].txt", "file3.txt", false)]
    public void IsIgnored_CaretInCharacterClass_IsTreatedAsLiteral(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that an un-escaped backslash inside a character class does not form a
    /// regex escape sequence with the following character (e.g. <c>\b</c> inside a .NET
    /// regex character class means a literal backspace, not the letter "b"): the class
    /// still matches "b" as an ordinary literal member.
    /// </summary>
    [Fact]
    public void IsIgnored_BackslashInCharacterClass_DoesNotFormEscapeSequence()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [@"file[a\b].txt"]);

        Assert.True(rules.IsIgnored("filea.txt"));
        Assert.True(rules.IsIgnored("fileb.txt"));
        Assert.False(rules.IsIgnored("filec.txt"));
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
    /// Verifies that <see cref="GitIgnoreRules.TryParseExcludesFile"/> extracts
    /// <c>core.excludesFile</c> from a <c>[core]</c> section, expands a leading
    /// <c>~</c>, and ignores settings outside the <c>[core]</c> section.
    /// </summary>
    [Theory]
    [InlineData("[core]\nexcludesFile = /etc/gitignore\n", "/etc/gitignore")]
    [InlineData("[user]\nname = x\n[core]\n  excludesFile=/etc/gitignore\n", "/etc/gitignore")]
    [InlineData("[core]\nautocrlf = true\n", null)]
    [InlineData("[user]\nexcludesFile = /etc/gitignore\n", null)]
    [InlineData("", null)]
    public void TryParseExcludesFile_ExtractsCoreSectionSetting(string config, string? expected)
    {
        var found = GitIgnoreRules.TryParseExcludesFile(config, out var path);

        Assert.Equal(expected is not null, found);
        Assert.Equal(expected, path);
    }

    /// <summary>
    /// Verifies that a leading <c>~</c> in <c>core.excludesFile</c> expands to the user's
    /// home directory.
    /// </summary>
    [Fact]
    public void TryParseExcludesFile_ExpandsHomeDirectoryTilde()
    {
        var found = GitIgnoreRules.TryParseExcludesFile("[core]\nexcludesFile = ~/.gitignore_global\n", out var path);

        Assert.True(found);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitignore_global"),
            path);
    }

    /// <summary>
    /// Verifies that a <c>.git/info/exclude</c> file at the scan root is honored, in
    /// addition to <c>.gitignore</c> files.
    /// </summary>
    [Fact]
    public void Load_GitInfoExclude_IsHonored()
    {
        var root = Path.Combine(Path.GetTempPath(), "sloc-gitinfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "info"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".git", "info", "exclude"), "*.log\n");

            var rules = GitIgnoreRules.Load(root, new HashSet<string>());

            Assert.True(rules.IsIgnored("app.log"));
            Assert.False(rules.IsIgnored("app.cs"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
