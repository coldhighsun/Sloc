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

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
