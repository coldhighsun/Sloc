using Sloc.Cli.Output;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="CsvRenderer"/>.
/// </summary>
public class CsvRendererTests
{
    /// <summary>
    /// Verifies that the by-language CSV has a header row and one data row per language,
    /// including the health column.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_WritesHeaderAndRows()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: false);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Language,Files,Code,Comment,Blank,Total,Health", lines[0]);
        Assert.StartsWith("C#,1,80,20,5,105,", lines[1]);
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> drops the Health column from the header and rows.
    /// </summary>
    [Fact]
    public void Render_NoHealth_OmitsHealthColumn()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Language,Files,Code,Comment,Blank,Total", lines[0]);
        Assert.DoesNotContain("Health", lines[0]);
    }

    /// <summary>
    /// Verifies that a path containing a comma is quoted so the CSV stays well-formed.
    /// </summary>
    [Fact]
    public void Render_ByFile_QuotesPathsWithCommas()
    {
        var summary = BuildSummary("weird, name.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: true, noHealth: true);
        var text = writer.ToString();

        Assert.Contains("\"weird, name.cs\"", text);
    }

    private static AnalysisSummary BuildSummary(string path)
    {
        var file = new FileAnalysis
        {
            Path = path,
            Language = "C#",
            Code = 80,
            Comment = 20,
            Blank = 5
        };
        return new AnalysisSummary([file]);
    }
}
