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

        Assert.Equal(1, exitCode);
    }
}
