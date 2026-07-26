using Sloc.Cli.Output;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="MarkdownRenderer"/>.
/// </summary>
public class MarkdownRendererTests
{
    /// <summary>
    /// Verifies that the by-language output has a title, a summary line, a header row,
    /// a GFM alignment separator, and a data row including the health column.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_WritesTitleHeaderSeparatorAndRow()
    {
        var summary = BuildSummary("a.cs");

        var text = Render(summary, byFile: false, noHealth: false);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Contains("# Sloc Report", lines);
        Assert.Contains(lines, line => line.StartsWith("**Generated:**") && line.Contains("**Files:** 1") && line.Contains("**Total Lines:** 105"));
        Assert.Contains("| Language | Files | Code | Comment | Blank | Total | Health |", lines);
        Assert.Contains("| :--- | ---: | ---: | ---: | ---: | ---: | :--- |", lines);
        Assert.Contains(lines, line => line.StartsWith("| C# | 1 | 80 | 20 | 5 | 105 |"));
    }

    /// <summary>
    /// Verifies that the by-language table ends with a bolded Total row summing all languages.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_AppendsTotalRow()
    {
        var summary = BuildSummary("a.cs");

        var text = Render(summary, byFile: false, noHealth: false);

        Assert.Contains("| **Total** | 1 | 80 | 20 | 5 | 105 |", text);
    }

    /// <summary>
    /// Verifies that the by-file table ends with a bolded Total row summing all files.
    /// </summary>
    [Fact]
    public void Render_ByFile_AppendsTotalRow()
    {
        var summary = BuildSummary("a.cs");

        var text = Render(summary, byFile: true, noHealth: false);

        Assert.Contains("| **Total** |  | 80 | 20 | 5 | 105 |", text);
    }

    /// <summary>
    /// Verifies that supplying a scan path and scan time renders both as header lines.
    /// </summary>
    [Fact]
    public void Render_WithScanPathAndTime_WritesHeaderLines()
    {
        var summary = BuildSummary("a.cs");
        var scanTime = new DateTimeOffset(2026, 7, 26, 10, 30, 0, TimeSpan.Zero);

        using var writer = new StringWriter();
        new MarkdownRenderer(writer, scanTime).Render(summary, byFile: false, noHealth: false);
        var text = writer.ToString();

        Assert.Contains("**Generated:** 2026-07-26T10:30:00Z", text);
    }

    /// <summary>
    /// Verifies that omitting the scan path leaves the Scan Path header line out entirely.
    /// </summary>
    [Fact]
    public void Render_WithoutScanPath_OmitsScanPathLine()
    {
        var summary = BuildSummary("a.cs");

        var text = Render(summary, byFile: false, noHealth: false);

        Assert.DoesNotContain("Scan Path", text);
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> drops the Health column from the header and separator.
    /// </summary>
    [Fact]
    public void Render_NoHealth_OmitsHealthColumn()
    {
        var summary = BuildSummary("a.cs");

        var text = Render(summary, byFile: false, noHealth: true);

        Assert.Contains("| Language | Files | Code | Comment | Blank | Total |", text);
        Assert.DoesNotContain("Health", text);
    }

    /// <summary>
    /// Verifies that a path containing a pipe is escaped so the table stays well-formed.
    /// </summary>
    [Fact]
    public void Render_ByFile_EscapesPipesInPaths()
    {
        var summary = BuildSummary("weird|name.cs");

        var text = Render(summary, byFile: true, noHealth: true);

        Assert.Contains("weird\\|name.cs", text);
    }

    private static string Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        using var writer = new StringWriter();
        new MarkdownRenderer(writer).Render(summary, byFile, noHealth);
        return writer.ToString();
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
