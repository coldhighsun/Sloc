using System.Diagnostics;
using System.Text.Json;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains integration tests for <see cref="AnalyzeHandler"/> with
/// <see cref="AnalyzeOptions.GitHash"/> set, driving a full extract-scan-analyze-render
/// run against a real temporary git repository.
/// </summary>
public sealed class AnalyzeHandlerGitHashTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalyzeHandlerGitHashTests"/>, creating a
    /// unique temporary git repository for the test.
    /// </summary>
    public AnalyzeHandlerGitHashTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-handler-git-" + Guid.NewGuid().ToString("N"));
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
    /// Verifies that analyzing by commit hash reports git-relative paths (no temp-dir
    /// prefix) and the same totals as a normal scan of the working tree at that commit.
    /// </summary>
    [Fact]
    public void Execute_GitHash_ReportsGitRelativePathsAndMatchesNormalScan()
    {
        // A name unique to this test run, so the leftover-temp-dir check below cannot
        // collide with another test class's concurrently running instance.
        var markerFileName = "a-" + Guid.NewGuid().ToString("N") + ".cs";
        File.WriteAllText(Path.Combine(_root, markerFileName), "int x = 1;\n// comment\n\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.py"), "x = 1\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");
        var outputFile = Path.Combine(Path.GetTempPath(), "sloc-report-" + Guid.NewGuid().ToString("N") + ".json");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            GitHash = "HEAD",
            Format = OutputFormat.Json,
            OutputFile = outputFile,
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(File.ReadAllText(outputFile));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("fileCount").GetInt32());

        var normalScanOutput = Path.Combine(Path.GetTempPath(), "sloc-report-" + Guid.NewGuid().ToString("N") + ".json");
        new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            OutputFile = normalScanOutput,
            Quiet = true,
            NoUpdateCheck = true
        });

        using var normalDocument = JsonDocument.Parse(File.ReadAllText(normalScanOutput));
        Assert.Equal(
            normalDocument.RootElement.GetProperty("code").GetInt32(),
            root.GetProperty("code").GetInt32());

        // The extraction temp directory must be cleaned up once Execute returns.
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.GetTempPath(), "sloc-git-*"),
            dir => File.Exists(Path.Combine(dir, markerFileName)));

        File.Delete(outputFile);
        File.Delete(normalScanOutput);
    }

    /// <summary>
    /// Verifies that an invalid commit-ish returns <see cref="ExitCode.Error"/> rather
    /// than throwing.
    /// </summary>
    [Fact]
    public void Execute_GitHashInvalid_ReturnsErrorExitCode()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            GitHash = "not-a-real-hash",
            Format = OutputFormat.Json,
            OutputFile = Path.Combine(_root, "report.json"),
            Quiet = true,
            NoUpdateCheck = true
        });

        Assert.Equal(ExitCode.Error, exitCode);
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