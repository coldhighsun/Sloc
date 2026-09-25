using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitIgnoreRules"/> pattern matching.
/// </summary>
public class GitIgnoreRulesTests
{
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
    /// Verifies that a leading <c>^</c> negates a character class exactly like <c>!</c>, as in
    /// git's wildmatch, while a <c>^</c> anywhere else in the class is a literal member.
    /// Expectations checked against <c>git check-ignore</c>.
    /// </summary>
    [Theory]
    [InlineData("file[^12].txt", "file^.txt", true)]
    [InlineData("file[^12].txt", "file1.txt", false)]
    [InlineData("file[^12].txt", "file3.txt", true)]
    [InlineData("[^a].c", "a.c", false)]
    [InlineData("[^a].c", "b.c", true)]
    [InlineData("file[1^].txt", "file^.txt", true)]
    [InlineData("file[1^].txt", "file2.txt", false)]
    public void IsIgnored_LeadingCaretInCharacterClass_NegatesClass(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a <c>]</c> right after the opening bracket (or negation marker) is a
    /// class member rather than the end of the class, and that a backslash inside a class
    /// makes the next character a literal member. Expectations checked against
    /// <c>git check-ignore</c>.
    /// </summary>
    [Theory]
    [InlineData("file[]a].txt", "file].txt", true)]
    [InlineData("file[]a].txt", "filea.txt", true)]
    [InlineData("file[]a].txt", "fileb.txt", false)]
    [InlineData("file[!]a].txt", "fileb.txt", true)]
    [InlineData("file[!]a].txt", "file].txt", false)]
    [InlineData(@"f[\]]", "f]", true)]
    [InlineData(@"f[\]]", @"f\", false)]
    [InlineData(@"f[a\-z]", "f-", true)]
    [InlineData(@"f[a\-z]", "fm", false)]
    public void IsIgnored_CharacterClassEdgeCases_MatchGit(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a backslash outside a character class makes the next character literal:
    /// an escaped wildcard matches only itself, and an escaped trailing space is kept.
    /// Expectations checked against <c>git check-ignore</c>.
    /// </summary>
    [Theory]
    [InlineData(@"a\?c", "a?c", true)]
    [InlineData(@"a\?c", "abc", false)]
    [InlineData(@"a\*c", "a*c", true)]
    [InlineData(@"a\*c", "abc", false)]
    [InlineData(@"a\[b]", "a[b]", true)]
    [InlineData(@"a\[b]", "ab", false)]
    [InlineData(@"tr\ ", "tr ", true)]
    [InlineData(@"tr\ ", "tr", false)]
    [InlineData(@"tr\ .c", "tr .c", true)]
    [InlineData(@"\#x", "#x", true)]
    [InlineData(@"\!x", "!x", true)]
    public void IsIgnored_BackslashEscape_MatchesLiteralCharacter(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a character class, negated or not, never matches the <c>/</c> between
    /// path segments, as in git's wildmatch. Expectations checked against <c>git check-ignore</c>.
    /// </summary>
    [Theory]
    [InlineData("foo[!x]bar", "foo/bar", false)]
    [InlineData("foo[^x]bar", "foo/bar", false)]
    [InlineData("foo[!x]bar", "fooybar", true)]
    [InlineData("foo[/x]bar", "foo/bar", false)]
    [InlineData("foo[/x]bar", "fooxbar", true)]
    public void IsIgnored_CharacterClass_NeverMatchesSlash(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a pattern with an unterminated character class matches nothing, as in
    /// git, rather than matching a literal <c>[</c>.
    /// </summary>
    /// <param name="pattern">A pattern whose <c>[</c> never closes.</param>
    /// <param name="path">The path the pattern would match if <c>[</c> were literal.</param>
    [Theory]
    [InlineData("build[", "build[")]
    [InlineData("a[]", "a[]")]
    public void IsIgnored_UnterminatedCharacterClass_MatchesNothing(string pattern, string path)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.False(rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that a pattern ending in an unescaped backslash matches nothing, as in git,
    /// rather than a literal backslash.
    /// </summary>
    [Fact]
    public void IsIgnored_TrailingBackslash_MatchesNothing()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [@"x\"]);

        Assert.False(rules.IsIgnored("x"));
        Assert.False(rules.IsIgnored(@"x\"));
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
    /// Verifies that a run of three or more stars between slashes (or at the start/end) acts
    /// like <c>**</c>, as in git's wildmatch, while a star run inside a segment still never
    /// crosses a <c>/</c>.
    /// </summary>
    [Theory]
    [InlineData("a/***/b.cs", "a/b.cs", true)]
    [InlineData("a/***/b.cs", "a/x/b.cs", true)]
    [InlineData("a/***/b.cs", "a/x/y/b.cs", true)]
    [InlineData("***/temp", "a/b/temp", true)]
    [InlineData("a/***", "a/x/y.cs", true)]
    [InlineData("a***b", "axxb", true)]
    [InlineData("a***b", "ax/xb", false)]
    public void IsIgnored_StarRun_ActsLikeDoubleStar(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies that only trailing spaces are trimmed from a pattern, as in git's
    /// <c>trim_trailing_spaces</c>: a trailing tab is part of the pattern, an escaped space is
    /// kept, and a space after an escaped backslash is still trimmed.
    /// </summary>
    [Theory]
    [InlineData("tab\t", "tab\t", true)]
    [InlineData("tab\t", "tab", false)]
    [InlineData("sp  ", "sp", true)]
    [InlineData(@"sp\  ", "sp ", true)]
    [InlineData(@"sp\  ", "sp", false)]
    public void IsIgnored_TrailingWhitespace_TrimsOnlyUnescapedSpaces(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern]);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    /// <summary>
    /// Verifies the trailing-space trimming rules directly, including cases a path can't
    /// express: backslashes pair up from the start of the line, so a space after an escaped
    /// backslash is trimmed, and a line ending in a lone backslash is left alone.
    /// </summary>
    [Theory]
    [InlineData("a  ", "a")]
    [InlineData("a\t", "a\t")]
    [InlineData("a \t ", "a \t")]
    [InlineData(@"a\ ", @"a\ ")]
    [InlineData(@"a\\ ", @"a\\")]
    [InlineData(@"a \", @"a \")]
    [InlineData("   ", "")]
    public void TrimTrailingSpaces_Line_MatchesGitTrimTrailingSpaces(string line, string expected)
    {
        var trimmed = GitIgnorePattern.TrimTrailingSpaces(line);

        Assert.Equal(expected, trimmed);
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
    /// Verifies that a negation cannot re-include a path whose parent directory is
    /// already excluded by a directory-only pattern, matching git's documented rule that
    /// an excluded directory is never scanned for re-inclusion patterns.
    /// </summary>
    [Fact]
    public void IsIgnored_Negation_CannotReincludePathUnderExcludedDirectory()
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, ["build/", "!build/keep.txt"]);

        Assert.True(rules.IsIgnored("build/keep.txt"));
        Assert.True(rules.IsIgnored("build/other.txt"));
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
    /// Verifies the precedence order documented on
    /// <see cref="GitIgnoreRules.Load(string, IReadOnlySet{string}, bool, Action{int, string}?, out IReadOnlyList{string}, bool)"/>: the
    /// repo-local <c>.git/info/exclude</c> is the lowest precedence among the tiers testable
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

    /// <summary>
    /// Verifies that a leading <c>~</c> in <c>core.excludesFile</c> expands to the user's
    /// home directory as git resolves it.
    /// </summary>
    [Fact]
    public void TryParseExcludesFile_ExpandsHomeDirectoryTilde()
    {
        var found = GitIgnoreRules.TryParseExcludesFile("[core]\nexcludesFile = ~/.gitignore_global\n", out var path);

        // Git's home directory: $HOME when set, otherwise the user profile.
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } envHome
            ? envHome
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(found);
        Assert.Equal(Path.Combine(home, ".gitignore_global"), path);
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
    [InlineData("[core]\nexcludesFile = \"/etc/git ignore\"\n", "/etc/git ignore")]
    [InlineData("[core]\nexcludesFile = /etc/gitignore # global ignores\n", "/etc/gitignore")]
    [InlineData("[core]\nexcludesFile = /etc/gitignore ; global ignores\n", "/etc/gitignore")]
    [InlineData("[core]\nexcludesFile = \"/etc/a#b\"\n", "/etc/a#b")]
    [InlineData("[core]\r\nexcludesFile = \"C:\\\\git\\\\ignore\"\r\n", "C:\\git\\ignore")]
    [InlineData("[core]\nexcludesFile = C:\\Users\\me\\ignore\n", "C:\\Users\\me\\ignore")]
    [InlineData("[core]\nexcludesFile = C:\\tools\\new\\build\\.gitignore\n", "C:\\tools\\new\\build\\.gitignore")]
    [InlineData("[core]\nexcludesFile = /a\n[core]\nexcludesFile = /b\n", "/b")]
    [InlineData("[core] excludesFile = /etc/gitignore\n", "/etc/gitignore")]
    [InlineData("[core \"sub\"]\nexcludesFile = /etc/gitignore\n", null)]
    public void TryParseExcludesFile_ExtractsCoreSectionSetting(string config, string? expected)
    {
        var found = GitIgnoreRules.TryParseExcludesFile(config, out var path);

        Assert.Equal(expected is not null, found);
        Assert.Equal(expected, path);
    }

    /// <summary>
    /// Verifies that with no <c>core.excludesFile</c> configured anywhere, the global
    /// ignore file falls back to git's default, <c>$XDG_CONFIG_HOME/git/ignore</c> (or
    /// <c>~/.config/git/ignore</c> when <c>XDG_CONFIG_HOME</c> is unset).
    /// </summary>
    [Fact]
    public void ResolveGlobalExcludesFilePath_NothingConfigured_UsesXdgDefault()
    {
        var home = CreateTempHome();
        try
        {
            Assert.Equal(
                Path.Combine(home, ".config", "git", "ignore"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdgConfigHome: null));

            var xdg = Path.Combine(home, "xdg");
            Assert.Equal(
                Path.Combine(xdg, "git", "ignore"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdg));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <c>core.excludesFile</c> is read from the XDG git config, and that
    /// <c>~/.gitconfig</c>, which git reads after it, overrides it.
    /// </summary>
    [Fact]
    public void ResolveGlobalExcludesFilePath_ReadsXdgConfigThenGitconfig()
    {
        var home = CreateTempHome();
        try
        {
            var xdgGit = Path.Combine(home, ".config", "git");
            Directory.CreateDirectory(xdgGit);
            File.WriteAllText(Path.Combine(xdgGit, "config"), "[core]\n\texcludesFile = ~/from-xdg\n");

            Assert.Equal(
                Path.Combine(home, "from-xdg"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdgConfigHome: null));

            File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n\texcludesFile = ~/from-gitconfig\n");

            Assert.Equal(
                Path.Combine(home, "from-gitconfig"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdgConfigHome: null));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <c>GIT_CONFIG_GLOBAL</c> replaces both user-level config files, and that
    /// the repository's own <c>.git/config</c>, which git reads last, overrides them all.
    /// </summary>
    [Fact]
    public void ResolveGlobalExcludesFilePath_HonorsGitConfigGlobalAndRepoConfig()
    {
        var home = CreateTempHome();
        try
        {
            File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n\texcludesFile = ~/from-gitconfig\n");
            var globalConfig = Path.Combine(home, "global-config");
            File.WriteAllText(globalConfig, "[core]\n\texcludesFile = ~/from-global-env\n");

            Assert.Equal(
                Path.Combine(home, "from-global-env"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdgConfigHome: null, gitConfigGlobal: globalConfig));

            var repoConfig = Path.Combine(home, "repo-config");
            File.WriteAllText(repoConfig, "[core]\n\texcludesFile = ~/from-repo\n");

            Assert.Equal(
                Path.Combine(home, "from-repo"),
                GitIgnoreRules.ResolveGlobalExcludesFilePath(home, xdgConfigHome: null, gitConfigGlobal: globalConfig, repoConfig));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="GitIgnoreRules.TryParseIgnoreCase"/> reads git's boolean
    /// syntax for <c>core.ignoreCase</c> (a bare key is true, the last setting wins) and skips
    /// settings outside the plain <c>[core]</c> section or with an invalid boolean.
    /// </summary>
    [Theory]
    [InlineData("[core]\n\tignorecase = false\n", true, false)]
    [InlineData("[core]\n\tignoreCase = true\n", true, true)]
    [InlineData("[core]\n\tignorecase\n", true, true)]
    [InlineData("[core]\n\tignorecase =\n", true, false)]
    [InlineData("[CORE]\n\tIgnoreCase = Off\n", true, false)]
    [InlineData("[core]\n\tignorecase = 2\n", true, true)]
    [InlineData("[core]\n\tignorecase = true\n[core]\n\tignorecase = no\n", true, false)]
    [InlineData("[core]\n\tignorecase = maybe\n", false, false)]
    [InlineData("[core \"sub\"]\n\tignorecase = false\n", false, false)]
    [InlineData("[user]\n\tignorecase = false\n", false, false)]
    public void TryParseIgnoreCase_Config_ReadsCoreBoolean(string config, bool expectedFound, bool expectedValue)
    {
        var found = GitIgnoreRules.TryParseIgnoreCase(config, out var ignoreCase);

        Assert.Equal(expectedFound, found);
        Assert.Equal(expectedValue, ignoreCase);
    }

    /// <summary>
    /// Verifies that <c>core.ignoreCase</c> falls back to the platform default when nothing
    /// sets it, is read from the global config, and is overridden by the repository config.
    /// </summary>
    [Fact]
    public void ResolveIgnoreCase_GlobalThenRepoConfig_LastSettingWins()
    {
        var home = CreateTempHome();
        try
        {
            var repoConfig = Path.Combine(home, "repo-config");
            var platformDefault = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

            var unset = GitIgnoreRules.ResolveIgnoreCase(home, xdgConfigHome: null, gitConfigGlobal: null, repoConfig);

            File.WriteAllText(Path.Combine(home, ".gitconfig"), $"[core]\n\tignorecase = {!platformDefault}\n");
            var global = GitIgnoreRules.ResolveIgnoreCase(home, xdgConfigHome: null, gitConfigGlobal: null, repoConfig);

            File.WriteAllText(repoConfig, $"[core]\n\tignorecase = {platformDefault}\n");
            var repo = GitIgnoreRules.ResolveIgnoreCase(home, xdgConfigHome: null, gitConfigGlobal: null, repoConfig);

            Assert.Equal(platformDefault, unset);
            Assert.Equal(!platformDefault, global);
            Assert.Equal(platformDefault, repo);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that with <c>core.ignoreCase</c> off, patterns match only paths of the same case.
    /// </summary>
    [Theory]
    [InlineData("*.LOG", "debug.LOG", true)]
    [InlineData("*.LOG", "debug.log", false)]
    [InlineData("Build/", "Build/out.txt", true)]
    [InlineData("Build/", "build/out.txt", false)]
    public void IsIgnored_CaseSensitiveRules_MatchExactCaseOnly(string pattern, string path, bool expected)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern], ignoreCase: false);

        Assert.Equal(expected, rules.IsIgnored(path));
    }

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "sloc-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>
    /// Verifies that a pattern whose character class can't be translated to a valid regex
    /// (an empty class, or a reversed range) is skipped rather than throwing, so one
    /// malformed line doesn't abort the whole scan, and the file's other patterns still apply.
    /// </summary>
    [Theory]
    [InlineData("foo[]")]
    [InlineData("foo[!]")]
    [InlineData("foo[z-a]")]
    public void FromLines_InvalidCharacterClass_SkipsPatternInsteadOfThrowing(string pattern)
    {
        var rules = GitIgnoreRules.FromLines(string.Empty, [pattern, "*.tmp"]);

        Assert.True(rules.IsIgnored("x.tmp"));
        Assert.False(rules.IsIgnored("foo.cs"));
    }
}