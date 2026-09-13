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
