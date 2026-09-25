using Sloc.Cli.Analysis;
using System.Diagnostics;
using System.Text.Json;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains integration tests for <see cref="AnalyzeHandler"/> with
/// <see cref="AnalyzeOptions.CompareTo"/> set, driving a full diff-against-a-commit run
/// against a real temporary git repository.
/// </summary>
public sealed class AnalyzeHandlerCompareToTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalyzeHandlerCompareToTests"/>, creating a
    /// unique temporary git repository for the test.
    /// </summary>
    public AnalyzeHandlerCompareToTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-handler-compareto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit("init", "-q");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
    }

    /// <summary>
    /// Deletes the temporary git repository created for the test.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> reports the exact line-count delta between the
    /// working tree and a previous commit, without requiring a saved baseline file.
    /// </summary>
    [Fact]
    public void Execute_CompareTo_ReportsDeltaAgainstPreviousCommit()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        // Adds a code line and a comment line relative to the first commit.
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\nint y = 2;\n// comment\n");
        var outputFile = Path.Combine(Path.GetTempPath(), "sloc-diff-" + Guid.NewGuid().ToString("N") + ".json");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputFile));
        var total = document.RootElement.GetProperty("total");
        Assert.Equal(1, total.GetProperty("code").GetInt32());
        Assert.Equal(1, total.GetProperty("comment").GetInt32());

        File.Delete(outputFile);
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> scopes the baseline to the same subdirectory as
    /// the analyzed path, rather than diffing against the whole repository — otherwise
    /// unrelated files elsewhere in the repo would pollute the diff.
    /// </summary>
    [Fact]
    public void Execute_CompareTo_ScopesBaselineToAnalyzedSubdirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "a.cs"), "int x = 1;\n");
        File.WriteAllText(Path.Combine(_root, "outside.cs"), "int y = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        // Grows only the file outside the analyzed subdirectory.
        File.WriteAllText(Path.Combine(_root, "outside.cs"), "int y = 1;\nint z = 2;\nint w = 3;\n");
        var outputFile = Path.Combine(Path.GetTempPath(), "sloc-diff-" + Guid.NewGuid().ToString("N") + ".json");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = Path.Combine(_root, "sub"),
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputFile));
        var total = document.RootElement.GetProperty("total");
        Assert.Equal(0, total.GetProperty("code").GetInt32());
        Assert.Equal(0, total.GetProperty("total").GetInt32());

        File.Delete(outputFile);
    }

    /// <summary>
    /// Verifies that an invalid commit-ish returns <see cref="ExitCode.Error"/> rather
    /// than throwing.
    /// </summary>
    [Fact]
    public void Execute_CompareToInvalid_ReturnsErrorExitCode()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            CompareTo = "not-a-real-hash",
            Format = OutputFormat.Json,
            OutputFile = Path.Combine(_root, "report.json"),
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Error, exitCode);
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> combined with <c>--git-hash</c> diffs two commits
    /// directly, without touching the working tree.
    /// </summary>
    [Fact]
    public void Execute_CompareToWithGitHash_DiffsTwoCommitsDirectly()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\nint y = 2;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "second");

        var outputFile = Path.Combine(Path.GetTempPath(), "sloc-diff-" + Guid.NewGuid().ToString("N") + ".json");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            GitHash = "HEAD",
            CompareTo = "HEAD~1",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputFile));
        Assert.Equal(1, document.RootElement.GetProperty("total").GetProperty("code").GetInt32());

        File.Delete(outputFile);
    }

    /// <summary>
    /// Verifies that combining <c>--compare-to</c> with a diff-unsupported format and an
    /// explicit <c>-o</c> file returns the error exit code and does not write the file,
    /// matching <c>--baseline</c>'s existing behavior for the same combination.
    /// </summary>
    [Fact]
    public void Execute_CompareToWithUnsupportedFormatAndOutputFile_ReturnsErrorAndDoesNotWriteFile()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        var reportPath = Path.Combine(_root, "report.html");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            CompareTo = "HEAD",
            Format = OutputFormat.Html,
            OutputFile = reportPath,
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Error, exitCode);
        Assert.False(File.Exists(reportPath));
    }

    /// <summary>
    /// Verifies that the baseline side of a <c>--compare-to</c> diff applies the same file
    /// filters as the current (working tree) side, so an unchanged tree diffs to zero:
    /// <c>--exclude</c>, <c>--include</c>, <c>--no-recursive</c>, the built-in excluded
    /// directories, and <c>.gitignore</c>/<c>.gitattributes</c> rules (at the root and in a
    /// subdirectory) that exclude a tracked file.
    /// </summary>
    /// <param name="filter">Which filter the unchanged tree is analyzed with.</param>
    [Theory]
    [InlineData("exclude")]
    [InlineData("include")]
    [InlineData("no-recursive")]
    [InlineData("include-no-recursive")]
    [InlineData("default-excluded-dir")]
    [InlineData("gitignore")]
    [InlineData("gitattributes")]
    public void Execute_CompareToUnchangedTree_AppliesSameFiltersToBaseline(string filter)
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int a = 1;\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.cs"), "int b = 1;\nint c = 2;\n");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "out.cs"), "int d = 1;\n");
        Directory.CreateDirectory(Path.Combine(_root, "vendor"));
        File.WriteAllText(Path.Combine(_root, "vendor", "lib.cs"), "int e = 1;\n");
        File.WriteAllText(Path.Combine(_root, "generated.cs"), "int f = 1;\n");
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "vendor/** linguist-vendored\n");
        // Nested rule files: relative to their own directory, not the scan root.
        File.WriteAllText(Path.Combine(_root, "sub", "nested-ignored.cs"), "int g = 1;\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub", "gen"));
        File.WriteAllText(Path.Combine(_root, "sub", "gen", "g.cs"), "int h = 1;\n");
        File.WriteAllText(Path.Combine(_root, "sub", ".gitattributes"), "gen/** linguist-generated\n");
        // A rule file inside a built-in excluded directory is never read by the directory
        // walk, so it must not affect the baseline either.
        File.WriteAllText(Path.Combine(_root, "bin", ".gitignore"), "!out.cs\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        // Ignored only after being committed, so the files are tracked but gitignored.
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "generated.cs\n");
        File.WriteAllText(Path.Combine(_root, "sub", ".gitignore"), "nested-ignored.cs\n");
        RunGit("add", ".gitignore", "sub/.gitignore");
        RunGit("commit", "-q", "-m", "ignore generated files");

        var total = RunCompareToJson(outputFile => new AnalyzeOptions
        {
            Path = _root,
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true,
            Excludes = filter == "exclude" ? ["**/sub/**"] : [],
            Includes = filter is "include" or "include-no-recursive" ? ["sub/**"] : [],
            NoRecursive = filter is "no-recursive" or "include-no-recursive"
        }).GetProperty("total");

        Assert.Equal(0, total.GetProperty("code").GetInt32());
        Assert.Equal(0, total.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> accepts a path to a single file, diffing just that
    /// file against its content at the given commit.
    /// </summary>
    [Fact]
    public void Execute_CompareToWithFilePath_DiffsJustThatFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "a.cs"), "int x = 1;\n");
        File.WriteAllText(Path.Combine(_root, "other.cs"), "int y = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        File.WriteAllText(Path.Combine(_root, "sub", "a.cs"), "int x = 1;\nint z = 2;\n");
        File.WriteAllText(Path.Combine(_root, "other.cs"), "int y = 1;\nint w = 2;\nint v = 3;\n");

        var total = RunCompareToJson(outputFile => new AnalyzeOptions
        {
            Path = Path.Combine(_root, "sub", "a.cs"),
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        }).GetProperty("total");

        Assert.Equal(1, total.GetProperty("code").GetInt32());
        Assert.Equal(1, total.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> on a directory that was a file of the same name at
    /// the given commit treats that old file as out of scope, reporting every current line
    /// as added instead of crashing.
    /// </summary>
    [Fact]
    public void Execute_CompareToDirectoryThatWasFile_TreatsOldFileAsOutOfScope()
    {
        File.WriteAllText(Path.Combine(_root, "lib.c"), "int a;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        File.Delete(Path.Combine(_root, "lib.c"));
        Directory.CreateDirectory(Path.Combine(_root, "lib.c"));
        File.WriteAllText(Path.Combine(_root, "lib.c", "x.c"), "int a;\nint b;\nint c;\n");

        var total = RunCompareToJson(outputFile => new AnalyzeOptions
        {
            Path = Path.Combine(_root, "lib.c"),
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        }).GetProperty("total");

        Assert.Equal(3, total.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Verifies that <c>--compare-to</c> on a file that was a directory of the same name at
    /// the given commit does not diff against a file from inside that old directory.
    /// </summary>
    [Fact]
    public void Execute_CompareToFileThatWasDirectory_DoesNotDiffAgainstFileInsideOldDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "lib.c"));
        File.WriteAllText(Path.Combine(_root, "lib.c", "x.c"), "int a;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        Directory.Delete(Path.Combine(_root, "lib.c"), recursive: true);
        File.WriteAllText(Path.Combine(_root, "lib.c"), "int a;\nint b;\nint c;\n");

        var total = RunCompareToJson(outputFile => new AnalyzeOptions
        {
            Path = Path.Combine(_root, "lib.c"),
            CompareTo = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        }).GetProperty("total");

        Assert.Equal(3, total.GetProperty("code").GetInt32());
    }

    private static JsonElement RunCompareToJson(Func<string, AnalyzeOptions> buildOptions)
    {
        var outputFile = Path.Combine(Path.GetTempPath(), "sloc-diff-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var exitCode = new AnalyzeHandler().Execute(buildOptions(outputFile));

            Assert.Equal(ExitCode.Success, exitCode);
            using var document = JsonDocument.Parse(File.ReadAllText(outputFile));
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(outputFile);
        }
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
    }
}
