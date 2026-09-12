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

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: false, noComplexity: true);
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

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Language,Files,Code,Comment,Blank,Total", lines[0]);
        Assert.DoesNotContain("Health", lines[0]);
    }

    /// <summary>
    /// Verifies that <c>noComplexity</c> drops the Complexity column from the header and rows.
    /// </summary>
    [Fact]
    public void Render_NoComplexity_OmitsComplexityColumn()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Language,Files,Code,Comment,Blank,Total", lines[0]);
        Assert.DoesNotContain("Complexity", lines[0]);
    }

    /// <summary>
    /// Verifies that the Complexity column is present by default, with C#'s per-file
    /// complexity value and an empty cell for a language that does not support it.
    /// </summary>
    [Fact]
    public void Render_Complexity_PopulatesForSupportedLanguageOnly()
    {
        var supported = new FileAnalysis { Path = "a.cs", Language = "C#", Code = 80, Comment = 20, Blank = 5, Complexity = 4 };
        var unsupported = new FileAnalysis { Path = "b.yml", Language = "YAML", Code = 10, Comment = 0, Blank = 0 };
        var summary = new AnalysisSummary([supported, unsupported]);
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: true, noHealth: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Path,Language,Code,Comment,Blank,Total,Complexity", lines[0]);
        Assert.Equal("a.cs,C#,80,20,5,105,4", lines[1]);
        Assert.Equal("b.yml,YAML,10,0,0,10,", lines[2]);
    }

    /// <summary>
    /// Verifies that a path containing a comma is quoted so the CSV stays well-formed.
    /// </summary>
    [Fact]
    public void Render_ByFile_QuotesPathsWithCommas()
    {
        var summary = BuildSummary("weird, name.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: true, noHealth: true, noComplexity: true);
        var text = writer.ToString();

        Assert.Contains("\"weird, name.cs\"", text);
    }

    /// <summary>
    /// Verifies that a path starting with a formula-trigger character (=, +, -, @, or tab)
    /// is prefixed with a quote so spreadsheet apps do not execute it as a formula.
    /// </summary>
    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1:A2)")]
    public void Render_ByFile_NeutralizesFormulaTriggerPaths(string maliciousPath)
    {
        var summary = BuildSummary(maliciousPath);
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: true, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("'" + maliciousPath[0], lines[1]);
    }

    /// <summary>
    /// Verifies that a skip-reason starting with a formula-trigger character is also
    /// neutralized, not just file paths.
    /// </summary>
    [Fact]
    public void Render_WithSkippedFiles_NeutralizesFormulaTriggerReason()
    {
        var summary = BuildSummary("a.cs", skipped: [new SkippedEntry("bad.cs", "=HYPERLINK(http://evil)")]);
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, noComplexity: true);
        var text = writer.ToString();

        Assert.Contains("bad.cs,'=HYPERLINK", text);
    }

    /// <summary>
    /// Verifies that the by-language CSV ends with a Total row summing all languages.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_AppendsTotalRow()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Total,1,80,20,5,105", lines[^1]);
    }

    /// <summary>
    /// Verifies that the by-file CSV ends with a Total row with an empty language column.
    /// </summary>
    [Fact]
    public void Render_ByFile_AppendsTotalRow()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: true, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Total,,80,20,5,105", lines[^1]);
    }

    /// <summary>
    /// Verifies that <c>--detailed</c> selects the language summary (not the per-file view),
    /// unlike the other formats which emit both.
    /// </summary>
    [Fact]
    public void Render_Detailed_EmitsLanguageSummaryOnly()
    {
        var summary = BuildSummary("a.cs");
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, detailed: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Language,Files,Code,Comment,Blank,Total", lines[0]);
        Assert.DoesNotContain("Path", lines[0]);
    }

    /// <summary>
    /// Verifies that skipped files are appended as a second table after a blank line.
    /// </summary>
    [Fact]
    public void Render_WithSkippedFiles_AppendsSkippedTable()
    {
        var summary = BuildSummary("a.cs", skipped: [new SkippedEntry("bad.cs", "binary file")]);
        using var writer = new StringWriter();

        new CsvRenderer(writer).Render(summary, byFile: false, noHealth: true, noComplexity: true);
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Path,Reason", lines[^2]);
        Assert.Equal("bad.cs,binary file", lines[^1]);
    }

    private static AnalysisSummary BuildSummary(string path, IReadOnlyList<SkippedEntry>? skipped = null)
    {
        var file = new FileAnalysis
        {
            Path = path,
            Language = "C#",
            Code = 80,
            Comment = 20,
            Blank = 5
        };
        return new AnalysisSummary([file], skipped);
    }
}
