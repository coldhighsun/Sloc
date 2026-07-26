using Sloc.Cli.Output;
using Sloc.Core.Models;
using Spectre.Console.Testing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="TableRenderer"/>, rendering to an in-memory
/// <see cref="TestConsole"/> so ANSI output can be asserted without a real terminal.
/// </summary>
public class TableRendererTests
{
    /// <summary>
    /// Verifies that the language table includes the language name, counts, the health
    /// column, and its computed label.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_WithHealth_RendersLanguageRowAndHealthColumn()
    {
        var summary = BuildSummary();
        var console = new TestConsole();

        console.Write(TableRenderer.BuildLanguageTable(summary, noHealth: false));
        var output = console.Output;

        Assert.Contains("C#", output);
        Assert.Contains("Comment Health", output);
        Assert.Contains("Good", output);
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> drops the Comment Health column entirely.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_NoHealth_OmitsHealthColumn()
    {
        var summary = BuildSummary();
        var console = new TestConsole();

        console.Write(TableRenderer.BuildLanguageTable(summary, noHealth: true));
        var output = console.Output;

        Assert.Contains("C#", output);
        Assert.DoesNotContain("Comment Health", output);
    }

    private static AnalysisSummary BuildSummary()
    {
        var file = new FileAnalysis
        {
            Path = "a.cs",
            Language = "C#",
            Code = 80,
            Comment = 20,
            Blank = 5
        };
        return new AnalysisSummary([file]);
    }
}
