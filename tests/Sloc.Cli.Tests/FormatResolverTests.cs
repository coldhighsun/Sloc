using Sloc.Cli;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="FormatResolver"/>.
/// </summary>
public class FormatResolverTests
{
    /// <summary>
    /// Verifies that an explicit format always wins over any inference from the output path.
    /// </summary>
    [Fact]
    public void Resolve_ExplicitFormat_WinsOverExtension()
    {
        var format = FormatResolver.Resolve(OutputFormat.Csv, "report.json");

        Assert.Equal(OutputFormat.Csv, format);
    }

    /// <summary>
    /// Verifies that the format is inferred from the output file's extension when no
    /// explicit format is given.
    /// </summary>
    [Theory]
    [InlineData("report.json", OutputFormat.Json)]
    [InlineData("report.html", OutputFormat.Html)]
    [InlineData("report.htm", OutputFormat.Html)]
    [InlineData("report.csv", OutputFormat.Csv)]
    [InlineData("report.md", OutputFormat.Markdown)]
    [InlineData("report.markdown", OutputFormat.Markdown)]
    [InlineData("report.JSON", OutputFormat.Json)]
    public void Resolve_NoExplicitFormat_InfersFromExtension(string outputFile, OutputFormat expected)
    {
        var format = FormatResolver.Resolve(null, outputFile);

        Assert.Equal(expected, format);
    }

    /// <summary>
    /// Verifies that an unknown extension and a missing output path both fall back to Table.
    /// </summary>
    [Theory]
    [InlineData("report.txt")]
    [InlineData(null)]
    public void Resolve_UnknownOrMissing_FallsBackToTable(string? outputFile)
    {
        var format = FormatResolver.Resolve(null, outputFile);

        Assert.Equal(OutputFormat.Table, format);
    }

    /// <summary>
    /// Verifies that a real output path with a Table format is flagged as ignored.
    /// </summary>
    [Fact]
    public void OutputIgnoredForTable_TableWithRealPath_ReturnsTrue()
    {
        var ignored = FormatResolver.OutputIgnoredForTable(OutputFormat.Table, "report.txt");

        Assert.True(ignored);
    }

    /// <summary>
    /// Verifies that the stdout token, a null path, and non-Table formats are not flagged.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Table, "-")]
    [InlineData(OutputFormat.Table, null)]
    [InlineData(OutputFormat.Json, "report.json")]
    public void OutputIgnoredForTable_NotApplicable_ReturnsFalse(OutputFormat format, string? outputFile)
    {
        var ignored = FormatResolver.OutputIgnoredForTable(format, outputFile);

        Assert.False(ignored);
    }
}
