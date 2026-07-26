using Sloc.Cli;
using System.Text.Json;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains integration tests for <see cref="AnalyzeHandler"/>, driving a full
/// scan-analyze-render run against a temporary directory tree.
/// </summary>
public sealed class AnalyzeHandlerTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalyzeHandlerTests"/>, creating a
    /// unique temporary root directory for the test.
    /// </summary>
    public AnalyzeHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-handler-" + Guid.NewGuid().ToString("N"));
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
    /// Verifies that a JSON run writes a report file, returns exit code 0, and the
    /// report contains the expected aggregated totals.
    /// </summary>
    [Fact]
    public void Execute_JsonFormat_WritesReportAndReturnsZero()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n// comment\n\n");
        var outputFile = Path.Combine(_root, "report.json");

        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            OutputFile = outputFile
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputFile));

        var root = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(outputFile));
        Assert.Equal(1, root.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, root.GetProperty("code").GetInt32());
        Assert.Equal(1, root.GetProperty("comment").GetInt32());
        Assert.Equal(1, root.GetProperty("blank").GetInt32());
    }

    /// <summary>
    /// Verifies that <c>--list-file</c> analyzes exactly the listed files, ignoring
    /// <see cref="AnalyzeOptions.Path"/>, and reports a missing entry as skipped.
    /// </summary>
    [Fact]
    public void Execute_ListFile_AnalyzesListedFilesAndSkipsMissing()
    {
        var keep = Path.Combine(_root, "a.cs");
        File.WriteAllText(keep, "int x = 1;\n");
        var missing = Path.Combine(_root, "missing.cs");
        var listFile = Path.Combine(_root, "list.txt");
        File.WriteAllText(listFile, $"{keep}\n{missing}\n");

        var stdout = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = Path.Combine(_root, "does-not-exist"),
            ListFile = listFile,
            Format = OutputFormat.Json,
            Quiet = true
        }));

        var root = JsonSerializer.Deserialize<JsonElement>(stdout);
        Assert.Equal(1, root.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, root.GetProperty("skipped").GetArrayLength());
    }

    /// <summary>
    /// Verifies that <c>--unique</c> counts byte-identical files only once and reports
    /// the later duplicate as skipped instead of double-counting its lines.
    /// </summary>
    [Fact]
    public void Execute_Unique_CountsDuplicateContentOnce()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "int x = 1;\n");

        var stdout = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            Quiet = true,
            Unique = true
        }));

        var root = JsonSerializer.Deserialize<JsonElement>(stdout);
        Assert.Equal(1, root.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, root.GetProperty("code").GetInt32());
        Assert.Equal(1, root.GetProperty("skipped").GetArrayLength());
    }

    /// <summary>
    /// Verifies that a missing path returns exit code 1 rather than throwing.
    /// </summary>
    [Fact]
    public void Execute_MissingPath_ReturnsOne()
    {
        var exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = Path.Combine(_root, "does-not-exist"),
            Format = OutputFormat.Json,
            OutputFile = Path.Combine(_root, "report.json")
        });

        Assert.Equal(ExitCode.Error, exitCode);
    }

    /// <summary>
    /// Verifies that JSON is written to stdout (without a BOM) when no output file is
    /// given, so it can be piped.
    /// </summary>
    [Fact]
    public void Execute_JsonWithoutOutputFile_WritesToStdout()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");

        var stdout = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            Quiet = true
        }));

        Assert.False(stdout.StartsWith('﻿'), "stdout should not begin with a BOM");
        var root = JsonSerializer.Deserialize<JsonElement>(stdout);
        Assert.Equal(1, root.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Verifies that Html is written to stdout when no output file is given, matching
    /// the default stdout behavior of the other non-table formats.
    /// </summary>
    [Fact]
    public void Execute_HtmlWithoutOutputFile_WritesToStdout()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");

        var stdout = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Html,
            Quiet = true
        }));

        Assert.Contains("<html", stdout);
    }

    /// <summary>
    /// Verifies that the comment-percentage threshold gate returns the threshold exit
    /// code when the comment percentage is below the requested minimum.
    /// </summary>
    [Fact]
    public void Execute_BelowCommentThreshold_ReturnsThresholdCode()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\nint y = 2;\n");

        int exitCode = 0;
        CaptureStdout(() => exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            Quiet = true,
            MinCommentPct = 50
        }));

        Assert.Equal(ExitCode.ThresholdNotMet, exitCode);
    }

    /// <summary>
    /// Verifies that a satisfied comment-percentage threshold returns success.
    /// </summary>
    [Fact]
    public void Execute_MeetsCommentThreshold_ReturnsSuccess()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "// comment\n// comment\nint x = 1;\n");

        int exitCode = 1;
        CaptureStdout(() => exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            Quiet = true,
            MinCommentPct = 50
        }));

        Assert.Equal(ExitCode.Success, exitCode);
    }

    /// <summary>
    /// Verifies that sequential (<c>--jobs 1</c>) and parallel analysis produce
    /// byte-identical JSON output, confirming the merge order is deterministic.
    /// </summary>
    [Fact]
    public void Execute_ParallelAndSequential_ProduceIdenticalOutput()
    {
        for (var i = 0; i < 50; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"file{i:D3}.cs"), $"// file {i}\nint x = {i};\n\n");
        }

        string Run(int jobs) => CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            ByFile = true,
            Quiet = true,
            Jobs = jobs
        }));

        var sequential = Run(1);
        var parallel = Run(8);

        Assert.Equal(sequential, parallel);
    }

    /// <summary>
    /// Verifies that a JSON baseline diff reports the per-language and total line-count
    /// deltas between a saved report and the current run.
    /// </summary>
    [Fact]
    public void Execute_JsonBaselineDiff_ReportsDeltas()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        var baselinePath = Path.Combine(_root, "baseline.json");
        var firstExit = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Includes = ["**/*.cs"],
            Format = OutputFormat.Json,
            OutputFile = baselinePath,
            Quiet = true
        });
        Assert.Equal(ExitCode.Success, firstExit);

        // Add another code line, then diff against the baseline.
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\nint y = 2;\n");

        var diff = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Includes = ["**/*.cs"],
            Format = OutputFormat.Json,
            BaselinePath = baselinePath,
            Quiet = true
        }));

        var root = JsonSerializer.Deserialize<JsonElement>(diff);
        Assert.Equal(1, root.GetProperty("total").GetProperty("code").GetInt32());
        var csharp = root.GetProperty("byLanguage").EnumerateArray().Single(e => e.GetProperty("language").GetString() == "C#");
        Assert.Equal(1, csharp.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Verifies that a baseline saved with <c>--by-file</c> (no by-language section) still
    /// yields correct per-language deltas, rather than treating every language as newly added.
    /// </summary>
    [Fact]
    public void Execute_ByFileBaselineDiff_ReportsIncrementalDeltas()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");
        var baselinePath = Path.Combine(_root, "baseline.json");
        var firstExit = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Includes = ["**/*.cs"],
            Format = OutputFormat.Json,
            OutputFile = baselinePath,
            ByFile = true,
            Quiet = true
        });
        Assert.Equal(ExitCode.Success, firstExit);

        // Add another code line, then diff against the by-file baseline.
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\nint y = 2;\n");

        var diff = CaptureStdout(() => new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Includes = ["**/*.cs"],
            Format = OutputFormat.Json,
            BaselinePath = baselinePath,
            Quiet = true
        }));

        var root = JsonSerializer.Deserialize<JsonElement>(diff);
        var csharp = root.GetProperty("byLanguage").EnumerateArray().Single(e => e.GetProperty("language").GetString() == "C#");
        // The delta must be +1 line, not the full +2 that a missing baseline breakdown would produce.
        Assert.Equal(1, csharp.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Verifies that a missing baseline file returns the error exit code instead of throwing.
    /// </summary>
    [Fact]
    public void Execute_MissingBaseline_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "int x = 1;\n");

        int exitCode = 0;
        CaptureStdout(() => exitCode = new AnalyzeHandler().Execute(new AnalyzeOptions
        {
            Path = _root,
            Format = OutputFormat.Json,
            BaselinePath = Path.Combine(_root, "no-such-baseline.json"),
            Quiet = true
        }));

        Assert.Equal(ExitCode.Error, exitCode);
    }

    private static string CaptureStdout(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
