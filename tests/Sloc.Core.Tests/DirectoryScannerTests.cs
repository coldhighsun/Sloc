using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="DirectoryScanner"/>. Each test builds a small
/// on-disk directory tree in a temporary location and removes it on disposal.
/// </summary>
public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryScanner _scanner = new();

    /// <summary>
    /// Initializes a new instance of <see cref="DirectoryScannerTests"/>, creating a
    /// unique temporary root directory for the test.
    /// </summary>
    public DirectoryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Deletes the temporary directory tree created for the test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that built-in directory excludes (e.g. bin, obj, node_modules) are applied.
    /// </summary>
    [Fact]
    public void Scan_AppliesDefaultExcludes()
    {
        Write("keep.cs", "// keep");
        Write("bin/skip.cs", "// bin");
        Write("obj/skip.cs", "// obj");
        Write("node_modules/skip.js", "// nm");

        var result = _scanner.Scan(_root, new ScanOptions());

        Assert.Single(result.Files);
        Assert.EndsWith("keep.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that user-supplied exclude globs drop matching files.
    /// </summary>
    [Fact]
    public void Scan_ExcludeGlob_DropsMatches()
    {
        Write("keep.cs", "// keep");
        Write("tests/drop.cs", "// drop");

        var result = _scanner.Scan(_root, new ScanOptions { Excludes = ["**/tests/**"] });

        Assert.Single(result.Files);
        Assert.EndsWith("keep.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that <see cref="ScanOptions.ExcludeLangs"/> drops files resolved as one of
    /// the named languages, matched case-insensitively.
    /// </summary>
    [Fact]
    public void Scan_ExcludeLangs_DropsMatchingLanguage()
    {
        Write("a.cs", "// code");
        Write("b.py", "print(1)");

        var result = _scanner.Scan(_root, new ScanOptions { ExcludeLangs = ["Python"] });

        Assert.Single(result.Files);
        Assert.EndsWith("a.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that a longer cycle formed by two distinct symlinks (A/linkToB points at B,
    /// and B/linkToA points back at A) is detected and does not cause infinite recursion,
    /// even though neither symlink directly loops back to its own ancestor.
    /// </summary>
    [Fact]
    public async Task Scan_FollowSymlinksChainedLoop_StillSkipsLoop()
    {
        Write("a/keep-a.cs", "// keep a");
        Write("b/keep-b.cs", "// keep b");

        var linkToB = Path.Combine(_root, "a", "linkToB");
        var linkToA = Path.Combine(_root, "b", "linkToA");
        try
        {
            Directory.CreateSymbolicLink(linkToB, Path.Combine(_root, "b"));
            Directory.CreateSymbolicLink(linkToA, Path.Combine(_root, "a"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        ScanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true }))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Scan did not complete; likely stuck in a chained symlink loop.");
            return;
        }

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["keep-a.cs", "keep-b.cs"], names);
    }

    /// <summary>
    /// Verifies that even with <c>FollowSymlinks</c> set, a symlink that loops back to one
    /// of its own ancestors is still skipped rather than followed forever.
    /// </summary>
    [Fact]
    public async Task Scan_FollowSymlinksDirectLoop_StillSkipsLoop()
    {
        Write("real/keep.cs", "// keep");

        var linkPath = Path.Combine(_root, "real", "loop");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        ScanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true }))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Scan did not complete; likely stuck in a symlink loop.");
            return;
        }

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).ToArray();
        Assert.Equal(["keep.cs"], names);
    }

    /// <summary>
    /// Verifies that a symlink looping back to a non-root ancestor (rather than the scan
    /// root itself) is still detected as a direct loop and skipped, even with
    /// <c>FollowSymlinks</c> set.
    /// </summary>
    [Fact]
    public async Task Scan_FollowSymlinksLoopToNonRootAncestor_StillSkipsLoop()
    {
        Write("a/keep.cs", "// keep");

        var linkPath = Path.Combine(_root, "a", "b", "loop");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(_root, "a"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        ScanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true }))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Scan did not complete; likely stuck in a symlink loop.");
            return;
        }

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).ToArray();
        Assert.Equal(["keep.cs"], names);
    }

    /// <summary>
    /// Verifies that <c>FollowSymlinks</c> descends into a symlinked directory that does
    /// not loop back on itself, picking up files behind the link.
    /// </summary>
    [Fact]
    public void Scan_FollowSymlinksNonLooping_DescendsIntoLink()
    {
        Write("real/keep.cs", "// keep");
        var targetDir = Path.Combine(_root, "target");
        Write("target/linked.cs", "// linked");

        var linkPath = Path.Combine(_root, "real", "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var result = _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true });

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Contains("linked.cs", names);
    }

    /// <summary>
    /// Verifies that <c>.gitignore</c> exclusion, <c>.gitattributes</c> vendored exclusion,
    /// and a user-supplied <c>--exclude</c> glob all apply correctly together in a single
    /// scan, covering the combined discovery/exclusion pass.
    /// </summary>
    [Fact]
    public void Scan_GitignoreAndGitAttributesAndCustomExclude_AllApplyTogether()
    {
        Write(".gitignore", "*.log\n");
        Write(".gitattributes", "vendor/** linguist-vendored\n");
        Write("app.cs", "// kept");
        Write("debug.log", "noise");
        Write("vendor/lib.cs", "// vendored");
        Write("generated/skip.cs", "// custom excluded");

        var result = _scanner.Scan(_root, new ScanOptions
        {
            RespectGitignore = true,
            RespectGitAttributes = true,
            Excludes = ["**/generated/**"]
        });

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["app.cs"], names);
    }

    /// <summary>
    /// Verifies that include globs restrict discovery to matching files.
    /// </summary>
    [Fact]
    public void Scan_IncludeGlob_RestrictsToMatches()
    {
        Write("a.cs", "// code");
        Write("b.py", "print(1)");

        var result = _scanner.Scan(_root, new ScanOptions { Includes = ["**/*.cs"] });

        Assert.Single(result.Files);
        Assert.EndsWith("a.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that <see cref="ScanOptions.IncludeLangs"/> restricts discovery to files
    /// resolved as one of the named languages, matched case-insensitively.
    /// </summary>
    [Fact]
    public void Scan_IncludeLangs_RestrictsToMatchingLanguage()
    {
        Write("a.cs", "// code");
        Write("b.py", "print(1)");

        var result = _scanner.Scan(_root, new ScanOptions { IncludeLangs = ["python"] });

        Assert.Single(result.Files);
        Assert.EndsWith("b.py", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that combined include/exclude globs are matched independently per file
    /// across a larger candidate set, guarding against the batched <c>Matcher.Match</c>
    /// call (which matches all candidate paths in a single call for performance) mixing up
    /// results between files.
    /// </summary>
    [Fact]
    public void Scan_IncludeAndExcludeGlobs_DiscriminatePerFileAcrossManyCandidates()
    {
        Write("keep-a.cs", "// keep a");
        Write("keep-b.cs", "// keep b");
        Write("nested/keep-c.cs", "// keep c");
        Write("nested/drop.cs", "// dropped by exclude");
        Write("other.py", "print(1)");
        Write("nested/other.py", "print(2)");

        var result = _scanner.Scan(_root, new ScanOptions
        {
            Includes = ["**/*.cs"],
            Excludes = ["**/drop.cs"]
        });

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["keep-a.cs", "keep-b.cs", "keep-c.cs"], names);
    }

    /// <summary>
    /// Verifies that unknown extensions are dropped by default and included as the
    /// "Other" language when <see cref="ScanOptions.IncludeUnknown"/> is set.
    /// </summary>
    [Fact]
    public void Scan_IncludeUnknown_ControlsUnknownExtensions()
    {
        Write("a.cs", "// code");
        Write("data.unknownext", "raw");

        var withoutUnknown = _scanner.Scan(_root, new ScanOptions());
        var withUnknown = _scanner.Scan(_root, new ScanOptions { IncludeUnknown = true });

        Assert.Single(withoutUnknown.Files);
        Assert.Equal(2, withUnknown.Files.Count);
        Assert.Contains(withUnknown.Files, f => f.Language.Name == "Other");
    }

    /// <summary>
    /// Verifies that scanning a non-existent path throws <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    [Fact]
    public void Scan_MissingPath_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => _scanner.Scan(missing, new ScanOptions()));
    }

    /// <summary>
    /// Verifies that a non-recursive scan ignores files in subdirectories.
    /// </summary>
    [Fact]
    public void Scan_NonRecursive_IgnoresSubdirectories()
    {
        Write("a.cs", "// code");
        Write("sub/b.cs", "// nested");

        var result = _scanner.Scan(_root, new ScanOptions { Recursive = false });

        Assert.Single(result.Files);
        Assert.EndsWith("a.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that <see cref="ScanOptions.Recursive"/> is ignored when explicit
    /// <see cref="ScanOptions.Includes"/> are supplied, per its documented contract: the
    /// tree walk must still descend into subdirectories for a pattern like <c>**/*.cs</c>
    /// to find files below the root.
    /// </summary>
    [Fact]
    public void Scan_NonRecursiveWithIncludes_StillFindsNestedMatches()
    {
        Write("a.cs", "// code");
        Write("sub/b.cs", "// nested");

        var result = _scanner.Scan(_root, new ScanOptions { Recursive = false, Includes = ["**/*.cs"] });

        Assert.Equal(2, result.Files.Count);
    }

    /// <summary>
    /// Verifies that a recursive scan discovers known-language files in nested directories.
    /// </summary>
    [Fact]
    public void Scan_Recursive_FindsNestedKnownFiles()
    {
        Write("a.cs", "// code");
        Write("sub/b.py", "print(1)");

        var result = _scanner.Scan(_root, new ScanOptions());

        Assert.Equal(2, result.Files.Count);
        Assert.Empty(result.Skipped);
    }

    /// <summary>
    /// Verifies that files marked <c>linguist-vendored</c> in a <c>.gitattributes</c> file
    /// are excluded when <see cref="ScanOptions.RespectGitAttributes"/> is set, and included
    /// otherwise.
    /// </summary>
    [Fact]
    public void Scan_RespectGitAttributes_ExcludesVendoredFiles()
    {
        Write(".gitattributes", "vendor/** linguist-vendored\n");
        Write("app.cs", "// app");
        Write("vendor/lib.cs", "// vendored");

        var honored = _scanner.Scan(_root, new ScanOptions { RespectGitAttributes = true });
        var honoredNames = honored.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();

        Assert.Equal(["app.cs"], honoredNames);

        var toggleOff = _scanner.Scan(_root, new ScanOptions { RespectGitAttributes = false });
        Assert.Contains(toggleOff.Files, f => Path.GetFileName(f.Path) == "lib.cs");
    }

    /// <summary>
    /// Verifies that <c>.gitignore</c> files, including a nested one, are honored when
    /// <see cref="ScanOptions.RespectGitignore"/> is set, and ignored otherwise.
    /// </summary>
    [Fact]
    public void Scan_RespectGitignore_HonorsRootAndNestedIgnoreFiles()
    {
        Write(".gitignore", "*.log\n");
        Write("app/.gitignore", "generated.cs\n");
        Write("keep.cs", "// keep");
        Write("debug.log", "noise");
        Write("app/main.cs", "// main");
        Write("app/generated.cs", "// generated");

        var honored = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true });
        var honoredNames = honored.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();

        Assert.Equal(["keep.cs", "main.cs"], honoredNames);

        var ignoredToggleOff = _scanner.Scan(_root, new ScanOptions { RespectGitignore = false });
        Assert.Contains(ignoredToggleOff.Files, f => Path.GetFileName(f.Path) == "generated.cs");
    }

    /// <summary>
    /// Verifies that the repository's <c>core.ignoreCase</c> decides whether
    /// <c>.gitignore</c> and <c>.gitattributes</c> patterns match paths of a different case.
    /// </summary>
    /// <param name="ignoreCase">The <c>core.ignoreCase</c> value written to <c>.git/config</c>.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scan_RepoIgnoreCaseSetting_ControlsPatternCaseSensitivity(bool ignoreCase)
    {
        Write(".git/config", $"[core]\n\tignorecase = {ignoreCase}\n");
        Write(".gitignore", "*.CS\n");
        Write(".gitattributes", "Vendor/** linguist-vendored\n");
        Write("app.cs", "// app");
        Write("vendor/lib.py", "# vendored");
        Write("main.py", "# main");

        var result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true });
        var names = result.Files.Select(f => Relative(f.Path)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ignoreCase ? ["main.py"] : ["app.cs", "main.py", "vendor/lib.py"], names);
    }

    /// <summary>
    /// Verifies that a macro defined in the top-level <c>.gitattributes</c> marks files
    /// vendored wherever it is set, that a nested file can reset the attribute with
    /// <c>!attr</c>, and that a macro defined in a nested file is ignored.
    /// </summary>
    [Fact]
    public void Scan_GitattributesMacros_ApplyTopLevelDefinitionsOnly()
    {
        Write(".gitattributes", "[attr]thirdparty linguist-vendored\nextern/** thirdparty\n");
        Write("extern/lib.py", "# vendored");
        Write("extern/own/.gitattributes", "*.py !linguist-vendored\n");
        Write("extern/own/mine.py", "# ours");
        Write("src/.gitattributes", "[attr]local linguist-generated\n*.py local\n");
        Write("src/app.py", "# app");

        var result = _scanner.Scan(_root, new ScanOptions());
        var names = result.Files.Select(f => Relative(f.Path)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["extern/own/mine.py", "src/app.py"], names);
    }

    /// <summary>
    /// Verifies that a negation pattern re-includes an otherwise ignored file during a scan.
    /// </summary>
    [Fact]
    public void Scan_RespectGitignore_NegationReincludesFile()
    {
        Write(".gitignore", "*.log\n!keep.log\n");
        Write("drop.log", "noise");
        Write("keep.log", "wanted");

        var result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true, IncludeUnknown = true });
        var names = result.Files.Select(f => Path.GetFileName(f.Path)).ToArray();

        Assert.Contains("keep.log", names);
        Assert.DoesNotContain("drop.log", names);
    }

    /// <summary>
    /// Verifies that a non-recursive scan does not descend into subdirectories to search for
    /// nested <c>.gitignore</c> files, matching the shallow file discovery it performs.
    /// </summary>
    [Fact]
    public void Scan_RespectGitignoreNonRecursive_DoesNotDescendForNestedIgnoreFiles()
    {
        Write(".gitignore", "*.log\n");
        Write("app/.gitignore", "generated.cs\n");
        Write("keep.cs", "// keep");
        Write("debug.log", "noise");

        var result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true, Recursive = false });
        var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();

        Assert.Equal(["keep.cs"], names);
    }

    /// <summary>
    /// Verifies that a <c>.gitignore</c>/<c>.gitattributes</c> file that cannot be read is
    /// reported as skipped on its own, while the rest of its directory and subtree is still scanned.
    /// </summary>
    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    public void Scan_UnreadableRuleFile_SkipsOnlyThatFileAndKeepsItsSubtree(string ruleFileName)
    {
        Write("a.cs", "int a;");
        Write("sub/b.cs", "int b;");
        Write("sub/deep/c.cs", "int c;");
        Write($"sub/{ruleFileName}", "x.cs linguist-vendored");
        var ruleFilePath = Path.Combine(_root, "sub", ruleFileName);

        ScanResult result;
        using (new FileStream(ruleFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true, RespectGitAttributes = true });
        }

        Assert.Equal(
            ["a.cs", "b.cs", "c.cs"],
            result.Files.Select(f => Path.GetFileName(f.Path)).Order(StringComparer.Ordinal));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(ruleFilePath, skipped.Path);
    }

    /// <summary>
    /// Verifies that a file listed more than once, including under a different spelling of
    /// the same path, is resolved only once so it is not counted twice.
    /// </summary>
    [Fact]
    public void ScanFiles_SameFileListedTwice_ResolvesItOnce()
    {
        var path = Write("sub/a.cs", "int a;");
        var other = Write("b.cs", "int b;");
        var respelled = Path.Combine(_root, "sub", "..", "sub", "a.cs");

        var result = _scanner.ScanFiles([path, other, respelled, path], new ScanOptions());

        Assert.Equal([Path.GetFullPath(path), Path.GetFullPath(other)], result.Files.Select(f => f.Path));
        Assert.Empty(result.Skipped);
    }

    /// <summary>
    /// Verifies that scanning a single existing file returns exactly that file.
    /// </summary>
    [Fact]
    public void Scan_SingleFile_ReturnsThatFile()
    {
        var path = Write("solo.cs", "// solo");

        var result = _scanner.Scan(path, new ScanOptions());

        Assert.Single(result.Files);
        Assert.EndsWith("solo.cs", result.Files[0].Path);
    }

    /// <summary>
    /// Verifies that scanning an explicit single-file path still honors a <c>.gitignore</c>
    /// in its parent directory when <see cref="ScanOptions.RespectGitignore"/> is set, and
    /// includes the file otherwise.
    /// </summary>
    [Fact]
    public void Scan_SingleFilePath_HonorsGitignoreInParentDirectory()
    {
        Write(".gitignore", "*.log\n");
        Write("debug.log", "noise");

        var ignoredPath = Path.Combine(_root, "debug.log");

        var honored = _scanner.Scan(ignoredPath, new ScanOptions { RespectGitignore = true, IncludeUnknown = true });
        Assert.Empty(honored.Files);

        var ignoredToggleOff = _scanner.Scan(ignoredPath, new ScanOptions { RespectGitignore = false, IncludeUnknown = true });
        Assert.Single(ignoredToggleOff.Files);
    }

    /// <summary>
    /// Verifies that scanning an explicit single-file path applies
    /// <see cref="ScanOptions.Excludes"/> glob patterns, including patterns that encode a
    /// directory segment relative to the current working directory.
    /// </summary>
    [Fact]
    public void Scan_SingleFilePath_AppliesExcludeGlob()
    {
        var path = Write("sub/foo.cs", "// foo");

        var excluded = _scanner.Scan(path, new ScanOptions { Excludes = ["**/sub/*.cs"] });
        Assert.Empty(excluded.Files);

        var notExcluded = _scanner.Scan(path, new ScanOptions { Excludes = ["**/other/*.cs"] });
        Assert.Single(notExcluded.Files);
    }

    /// <summary>
    /// Verifies that scanning an explicit single-file path applies
    /// <see cref="ScanOptions.Includes"/> glob patterns, dropping the file when it does not
    /// match any include pattern.
    /// </summary>
    [Fact]
    public void Scan_SingleFilePath_AppliesIncludeGlob()
    {
        var path = Write("foo.cs", "// foo");

        var notMatched = _scanner.Scan(path, new ScanOptions { Includes = ["**/*.py"] });
        Assert.Empty(notMatched.Files);

        var matched = _scanner.Scan(path, new ScanOptions { Includes = ["**/*.cs"] });
        Assert.Single(matched.Files);
    }

    /// <summary>
    /// Verifies that scanning an explicit single-file path matches a bare file-name glob
    /// (e.g. <c>*.cs</c>) the same way scanning its parent directory would, for both
    /// <see cref="ScanOptions.Includes"/> and <see cref="ScanOptions.Excludes"/>.
    /// </summary>
    [Fact]
    public void Scan_SingleFilePath_MatchesFileNameGlob()
    {
        var path = Write("sub/foo.cs", "// foo");

        var included = _scanner.Scan(path, new ScanOptions { Includes = ["*.cs"] });
        Assert.Single(included.Files);

        var notIncluded = _scanner.Scan(path, new ScanOptions { Includes = ["*.py"] });
        Assert.Empty(notIncluded.Files);

        var excluded = _scanner.Scan(path, new ScanOptions { Excludes = ["*.cs"] });
        Assert.Empty(excluded.Files);

        var excludedByDirectory = _scanner.Scan(path, new ScanOptions { Includes = ["*.cs"], Excludes = ["**/sub/**"] });
        Assert.Empty(excludedByDirectory.Files);
    }

    /// <summary>
    /// Verifies that for a single-file path under the current directory, globs match its
    /// path relative to the current directory (as a scan of "." would): a directory
    /// segment written relative to it matches, and a directory above it does not.
    /// </summary>
    [Fact]
    public void Scan_SingleFilePathUnderCwd_MatchesRelativeToCwd()
    {
        var cwd = Directory.GetCurrentDirectory();
        var dirName = "sloc-cwd-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(cwd, dirName, "sub", "foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// foo");
        try
        {
            var included = _scanner.Scan(path, new ScanOptions { Includes = [$"{dirName}/sub/*.cs"] });
            Assert.Single(included.Files);

            // The current directory's own name only appears above it, so it must not exclude.
            var aboveCwd = _scanner.Scan(path, new ScanOptions { Excludes = [$"**/{Path.GetFileName(cwd)}/**"] });
            Assert.Single(aboveCwd.Files);
        }
        finally
        {
            Directory.Delete(Path.Combine(cwd, dirName), recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a self-referential directory symlink does not make the scan recurse
    /// forever, and that files behind the loop are not walked through the symlink.
    /// </summary>
    [Fact]
    public async Task Scan_SymlinkedDirectoryLoop_DoesNotRecurseForever()
    {
        Write("real/keep.cs", "// keep");

        var linkPath = Path.Combine(_root, "real", "loop");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory symlinks require a privilege this environment may not grant; the
            // behavior under test cannot be exercised without one.
            return;
        }

        ScanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Scan(_root, new ScanOptions()))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Scan did not complete; likely stuck in a symlink loop.");
            return;
        }

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).ToArray();
        Assert.Equal(["keep.cs"], names);
    }

    /// <summary>
    /// Verifies that a self-referential directory symlink does not make the scan recurse
    /// forever when <c>.gitignore</c> discovery is also walking the tree (the two share a
    /// single traversal in this case).
    /// </summary>
    [Fact]
    public async Task Scan_SymlinkedDirectoryLoopWithGitignore_DoesNotRecurseForever()
    {
        Write("real/keep.cs", "// keep");

        var linkPath = Path.Combine(_root, "real", "loop");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory symlinks require a privilege this environment may not grant; the
            // behavior under test cannot be exercised without one.
            return;
        }

        ScanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Scan(_root, new ScanOptions { RespectGitignore = true }))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Scan did not complete; likely stuck in a symlink loop.");
            return;
        }

        var names = result.Files.Select(f => Path.GetFileName(f.Path)).ToArray();
        Assert.Equal(["keep.cs"], names);
    }

    /// <summary>
    /// Verifies that a symlinked file is skipped by default and only included once
    /// <c>FollowSymlinks</c> is set, matching how symlinked directories are handled.
    /// </summary>
    [Fact]
    public void Scan_SymlinkedFile_IsSkippedUnlessFollowSymlinksIsSet()
    {
        var external = Directory.CreateTempSubdirectory("sloc-ext-");
        try
        {
            Write("repo/real.cs", "// real");
            var targetPath = Path.Combine(external.FullName, "shared.cs");
            File.WriteAllText(targetPath, "// shared");
            if (!TryCreateFileSymlink(Path.Combine(_root, "repo", "linked.cs"), targetPath))
            {
                return;
            }

            var repo = Path.Combine(_root, "repo");
            var defaultResult = _scanner.Scan(repo, new ScanOptions());
            var defaultNames = defaultResult.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
            Assert.Equal(["real.cs"], defaultNames);

            var followedResult = _scanner.Scan(repo, new ScanOptions { FollowSymlinks = true });
            var followedNames = followedResult.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
            Assert.Equal(["linked.cs", "real.cs"], followedNames);
        }
        finally
        {
            external.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that with <c>FollowSymlinks</c> set, a file symlink whose target is inside
    /// the scan root is not counted a second time alongside its target.
    /// </summary>
    [Fact]
    public void Scan_FollowSymlinksFileLinkIntoRoot_IsNotDoubleCounted()
    {
        var targetPath = Write("real.cs", "// real");
        if (!TryCreateFileSymlink(Path.Combine(_root, "linked.cs"), targetPath))
        {
            return;
        }

        var result = _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true });

        Assert.Equal(["real.cs"], result.Files.Select(f => Relative(f.Path)).ToArray());
    }

    /// <summary>
    /// Verifies that with <c>FollowSymlinks</c> set, an external file reached through several
    /// file symlinks and a followed directory symlink is counted only once.
    /// </summary>
    [Fact]
    public void Scan_FollowSymlinksSameExternalFileViaSeveralLinks_IsCountedOnce()
    {
        var external = Directory.CreateTempSubdirectory("sloc-ext-");
        try
        {
            var targetPath = Path.Combine(external.FullName, "shared.cs");
            File.WriteAllText(targetPath, "// shared");
            if (!TryCreateFileSymlink(Path.Combine(_root, "one.cs"), targetPath)
                || !TryCreateFileSymlink(Path.Combine(_root, "two.cs"), targetPath)
                || !TryCreateDirectorySymlink(Path.Combine(_root, "lib"), external.FullName))
            {
                return;
            }

            var result = _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true });

            Assert.Single(result.Files);
        }
        finally
        {
            external.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that with <c>FollowSymlinks</c> set, a directory symlink pointing at an
    /// ancestor of the scan root is skipped, rather than walking the scan root again below it.
    /// </summary>
    [Fact]
    public void Scan_FollowSymlinksLinkToRootAncestor_IsSkipped()
    {
        Write("repo/a.cs", "// a");
        Write("other/b.cs", "// b");
        var repo = Path.Combine(_root, "repo");
        if (!TryCreateDirectorySymlink(Path.Combine(repo, "up"), _root))
        {
            return;
        }

        var result = _scanner.Scan(repo, new ScanOptions { FollowSymlinks = true });

        Assert.Equal(["repo/a.cs"], result.Files.Select(f => Relative(f.Path)).ToArray());
    }

    /// <summary>
    /// Verifies that with <c>FollowSymlinks</c> set and the scan root itself given through a
    /// symlink, a link to a directory inside the root's real location is still recognized as
    /// inside the scan root and not followed, so its files are not double-counted.
    /// </summary>
    [Fact]
    public void Scan_FollowSymlinksRootGivenThroughLink_SkipsLinkIntoRealRoot()
    {
        Write("real/a.cs", "// a");
        Write("real/sub/b.cs", "// b");
        var alias = Path.Combine(_root, "alias");
        if (!TryCreateDirectorySymlink(alias, Path.Combine(_root, "real"))
            || !TryCreateDirectorySymlink(Path.Combine(_root, "real", "link"), Path.Combine(_root, "real", "sub")))
        {
            return;
        }

        var result = _scanner.Scan(alias, new ScanOptions { FollowSymlinks = true });

        var paths = result.Files.Select(f => Path.GetRelativePath(alias, f.Path).Replace('\\', '/')).ToArray();
        Assert.Equal(["a.cs", "sub/b.cs"], paths);
    }

    /// <summary>
    /// Verifies that with <c>FollowSymlinks</c> set, two directory symlinks whose external
    /// targets overlap (one inside the other) do not count the shared files twice, whichever
    /// link the walk reaches first.
    /// </summary>
    /// <param name="outerLink">The name of the link to the outer directory.</param>
    /// <param name="innerLink">The name of the link to the directory nested inside it.</param>
    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a")]
    public void Scan_FollowSymlinksOverlappingTargets_CountsSharedFilesOnce(string outerLink, string innerLink)
    {
        var external = Directory.CreateTempSubdirectory("sloc-ext-");
        try
        {
            File.WriteAllText(Path.Combine(external.FullName, "top.cs"), "// top");
            var inner = external.CreateSubdirectory("inner");
            File.WriteAllText(Path.Combine(inner.FullName, "deep.cs"), "// deep");
            if (!TryCreateDirectorySymlink(Path.Combine(_root, outerLink), external.FullName)
                || !TryCreateDirectorySymlink(Path.Combine(_root, innerLink), inner.FullName))
            {
                return;
            }

            var result = _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true });

            var names = result.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(["deep.cs", "top.cs"], names);
        }
        finally
        {
            external.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="DirectoryScanner.ScanSnapshot"/> given every file of a tree
    /// (with its root-relative path) keeps exactly the files <see cref="DirectoryScanner.Scan"/>
    /// keeps for that same tree on disk: root and nested <c>.gitignore</c>/<c>.gitattributes</c>,
    /// rule files inside a built-in excluded directory, globs, and recursion all agree.
    /// </summary>
    /// <param name="variant">Which scan options to compare the two under.</param>
    [Theory]
    [InlineData("default")]
    [InlineData("no-recursive")]
    [InlineData("include")]
    [InlineData("include-no-recursive")]
    [InlineData("exclude")]
    public void ScanSnapshot_SameTree_KeepsSameFilesAsScan(string variant)
    {
        Write("a.cs", "// a");
        Write("root-ignored.cs", "// ignored by the root .gitignore");
        Write(".gitignore", "root-ignored.cs\n");
        Write("sub/b.cs", "// b");
        Write("sub/nested-ignored.cs", "// ignored by sub/.gitignore");
        Write("sub/.gitignore", "nested-ignored.cs\n");
        Write("sub/gen/g.cs", "// generated");
        Write("sub/.gitattributes", "gen/** linguist-generated\n");
        Write("bin/c.cs", "// bin");
        Write("bin/.gitignore", "*\n");
        Write("vendor/v.cs", "// vendored");
        Write(".gitattributes", "vendor/** linguist-vendored\n");

        var options = new ScanOptions
        {
            RespectGitignore = true,
            Recursive = variant is not ("no-recursive" or "include-no-recursive"),
            Includes = variant is "include" or "include-no-recursive" ? ["sub/**"] : [],
            Excludes = variant == "exclude" ? ["**/gen/**"] : []
        };

        var scanned = _scanner.Scan(_root, options).Files.Select(f => Relative(f.Path)).Order(StringComparer.Ordinal);

        SnapshotEntry[] entries =
        [
            .. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Select(path => new SnapshotEntry(path, Relative(path)))
        ];
        var snapshot = _scanner.ScanSnapshot(_root, entries, options).Files.Select(f => Relative(f.Path));

        Assert.Equal(scanned, snapshot);
    }

    /// <summary>
    /// Verifies that scanning a subdirectory of a repository applies the rule files of the
    /// directories above it and the repository's <c>info/exclude</c>, with their patterns
    /// relative to their own directories as git evaluates them.
    /// </summary>
    [Fact]
    public void Scan_SubdirectoryOfRepository_AppliesEnclosingRules()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        Write(".git/info/exclude", "*.tmp.cs\n");
        Write(".gitignore", "*.gen.cs\n/sub/anchored.cs\nsub/skip/\n/a.cs\n");
        Write(".gitattributes", "*.vendor.cs linguist-vendored\n");
        Write("sub/a.cs", "// a");
        Write("sub/b.gen.cs", "// generated");
        Write("sub/anchored.cs", "// anchored");
        Write("sub/skip/c.cs", "// skipped directory");
        Write("sub/d.vendor.cs", "// vendored");
        Write("sub/e.tmp.cs", "// excluded");

        var result = _scanner.Scan(Path.Combine(_root, "sub"), new ScanOptions { RespectGitignore = true });

        Assert.Equal(["sub/a.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that a scan root the enclosing repository's rules would ignore is still
    /// scanned, while the rules keep applying to the files inside it.
    /// </summary>
    [Fact]
    public void Scan_ScanRootIgnoredByEnclosingRepository_IsStillScanned()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "sub/\n/sub/*\n*.gen.cs\n");
        Write("sub/inner/a.cs", "// a");
        Write("sub/inner/b.gen.cs", "// generated");

        var result = _scanner.Scan(Path.Combine(_root, "sub", "inner"), new ScanOptions { RespectGitignore = true });

        Assert.Equal(["sub/inner/a.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that attribute macros defined in the repository root's <c>.gitattributes</c>
    /// apply when scanning a subdirectory.
    /// </summary>
    [Fact]
    public void Scan_SubdirectoryOfRepository_TakesMacrosFromRepositoryRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitattributes", "[attr]thirdparty linguist-vendored\n");
        Write("sub/.gitattributes", "lib/** thirdparty\n");
        Write("sub/a.cs", "// a");
        Write("sub/lib/b.cs", "// vendored");

        var result = _scanner.Scan(Path.Combine(_root, "sub"), new ScanOptions());

        Assert.Equal(["sub/a.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that in a linked worktree (whose <c>.git</c> is a file pointing into the main
    /// repository), <c>info/exclude</c> is read from the main repository's common directory.
    /// </summary>
    [Fact]
    public void Scan_LinkedWorktree_UsesCommonDirectoryExclude()
    {
        Write("main/.git/info/exclude", "*.tmp.cs\n");
        Write("main/.git/worktrees/wt/commondir", "../..\n");
        Write("wt/.git", "gitdir: ../main/.git/worktrees/wt\n");
        Write("wt/a.cs", "// a");
        Write("wt/b.tmp.cs", "// excluded");

        var result = _scanner.Scan(Path.Combine(_root, "wt"), new ScanOptions { RespectGitignore = true });

        Assert.Equal(["wt/a.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that <see cref="DirectoryScanner.ScanSnapshot"/> of a repository subdirectory
    /// without a <see cref="SnapshotRepository"/> applies the enclosing rule files on disk,
    /// as <see cref="DirectoryScanner.Scan"/> does.
    /// </summary>
    [Fact]
    public void ScanSnapshot_SubdirectoryOfRepository_AppliesEnclosingRulesFromDisk()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "*.gen.cs\n");
        SnapshotEntry[] entries =
        [
            new(Write("sub/a.cs", "// a"), "a.cs"),
            new(Write("sub/b.gen.cs", "// generated"), "b.gen.cs")
        ];

        var result = _scanner.ScanSnapshot(Path.Combine(_root, "sub"), entries, new ScanOptions { RespectGitignore = true });

        Assert.Equal(["sub/a.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that <see cref="DirectoryScanner.ScanSnapshot"/> given a
    /// <see cref="SnapshotRepository"/> takes the rule files above the scan root from the
    /// repository's files instead of the disk.
    /// </summary>
    [Fact]
    public void ScanSnapshot_WithRepository_UsesSuppliedAncestorRules()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write(".gitignore", "b.cs\n");
        var a = Write("store/sub/a.cs", "// a");
        var b = Write("store/sub/b.cs", "// b");
        var c = Write("store/sub/c.gen.cs", "// generated");
        var gitignore = Write("store/.gitignore", "*.gen.cs\n");
        var gitattributes = Write("store/.gitattributes", "a.cs linguist-generated\n");
        var repository = new SnapshotRepository(
            "sub",
            [
                new(gitignore, ".gitignore"),
                new(gitattributes, ".gitattributes"),
                new(a, "sub/a.cs"),
                new(b, "sub/b.cs"),
                new(c, "sub/c.gen.cs")
            ]);
        SnapshotEntry[] entries = [new(a, "a.cs"), new(b, "b.cs"), new(c, "c.gen.cs")];

        var result = _scanner.ScanSnapshot(
            Path.Combine(_root, "sub"), entries, new ScanOptions { RespectGitignore = true }, repository);

        Assert.Equal(["store/sub/b.cs"], result.Files.Select(f => Relative(f.Path)));
    }

    /// <summary>
    /// Verifies that inside a repository, a nested repository (a <c>.git</c> directory or
    /// file, as a submodule has) is not descended into and is reported as skipped.
    /// </summary>
    /// <param name="gitFile">Whether the nested repository's <c>.git</c> is a file rather than a directory.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_NestedRepositoryInsideRepository_IsSkipped(bool gitFile)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write("a.cs", "// a");
        Write("sub/b.cs", "// submodule");
        if (gitFile)
        {
            Write("sub/.git", "gitdir: ../.git/modules/sub\n");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(_root, "sub", ".git"));
        }

        var result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = true });

        Assert.Equal(["a.cs"], result.Files.Select(f => Relative(f.Path)));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("sub", Relative(skipped.Path));
    }

    /// <summary>
    /// Verifies that nested repositories are scanned when the scan root is not inside a
    /// repository (e.g. a folder of clones) or when <c>.gitignore</c> is not honored.
    /// </summary>
    /// <param name="insideRepository">Whether the scan root is inside a repository.</param>
    /// <param name="respectGitignore">Whether <c>.gitignore</c> is honored.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Scan_NestedRepositoryOutsideRepositoryOrWithoutGitignore_IsScanned(bool insideRepository, bool respectGitignore)
    {
        if (insideRepository)
        {
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
        }

        Directory.CreateDirectory(Path.Combine(_root, "clone", ".git"));
        Write("clone/b.cs", "// clone");

        var result = _scanner.Scan(_root, new ScanOptions { RespectGitignore = respectGitignore });

        Assert.Equal(["clone/b.cs"], result.Files.Select(f => Relative(f.Path)));
        Assert.Empty(result.Skipped);
    }

    private string Relative(string path) =>
        Path.GetRelativePath(_root, path).Replace('\\', '/');

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// Creates a directory symlink, returning <see langword="false"/> instead of throwing when
    /// the environment does not grant the privilege required.
    /// </summary>
    /// <param name="linkPath">The path of the link to create.</param>
    /// <param name="targetPath">The directory the link points at.</param>
    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a file symlink, returning <see langword="false"/> instead of throwing when the
    /// environment does not grant the privilege required.
    /// </summary>
    /// <param name="linkPath">The path of the link to create.</param>
    /// <param name="targetPath">The file the link points at.</param>
    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}