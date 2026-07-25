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
