using Sloc.Core;

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
    /// Verifies that scanning a non-existent path throws <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    [Fact]
    public void Scan_MissingPath_Throws()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => _scanner.Scan(missing, new ScanOptions()));
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
    /// Verifies that a symlinked file is skipped by default and only included once
    /// <c>FollowSymlinks</c> is set, matching how symlinked directories are handled.
    /// </summary>
    [Fact]
    public void Scan_SymlinkedFile_IsSkippedUnlessFollowSymlinksIsSet()
    {
        Write("real.cs", "// real");
        var targetPath = Path.Combine(_root, "real.cs");
        var linkPath = Path.Combine(_root, "linked.cs");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var defaultResult = _scanner.Scan(_root, new ScanOptions());
        var defaultNames = defaultResult.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["real.cs"], defaultNames);

        var followedResult = _scanner.Scan(_root, new ScanOptions { FollowSymlinks = true });
        var followedNames = followedResult.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["linked.cs", "real.cs"], followedNames);
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

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
