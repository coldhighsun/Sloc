using Sloc.Cli.Output;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="HtmlRenderer"/>.
/// </summary>
public class HtmlRendererTests
{
    /// <summary>
    /// Verifies that the rendered document is a self-contained HTML page that contains
    /// the language name and a health label.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_ProducesSelfContainedDocumentWithHealth()
    {
        var summary = BuildSummary();
        using var writer = new StringWriter();

        new HtmlRenderer(writer).Render(summary, byFile: false, noHealth: false);
        var html = writer.ToString();

        Assert.Contains("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C#", html);
        Assert.Contains("Good", html);
    }

    /// <summary>
    /// Verifies that the injected scan time is stamped in the report header rather than the
    /// render-time clock, so it matches other formats produced from the same run.
    /// </summary>
    [Fact]
    public void Render_WithScanTime_StampsInjectedTimeInHeader()
    {
        var summary = BuildSummary();
        var scanTime = new DateTimeOffset(2026, 7, 26, 10, 30, 0, TimeSpan.Zero);
        using var writer = new StringWriter();

        new HtmlRenderer(writer, scanTime).Render(summary, byFile: false, noHealth: false);
        var html = writer.ToString();

        Assert.Contains("2026-07-26T10:30:00Z", html);
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> suppresses the health label in the output.
    /// </summary>
    [Fact]
    public void Render_NoHealth_OmitsHealthLabel()
    {
        var summary = BuildSummary();
        using var writer = new StringWriter();

        new HtmlRenderer(writer).Render(summary, byFile: false, noHealth: true);
        var html = writer.ToString();

        Assert.DoesNotContain(">Good<", html);
    }

    /// <summary>
    /// Verifies that the document carries a dark-theme stylesheet and a reproducible
    /// UTC timestamp (ISO-8601, <c>Z</c> suffix) rather than a local-time string.
    /// </summary>
    [Fact]
    public void Render_EmitsDarkThemeAndUtcTimestamp()
    {
        var summary = BuildSummary();
        using var writer = new StringWriter();

        new HtmlRenderer(writer).Render(summary, byFile: false, noHealth: false);
        var html = writer.ToString();

        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Matches(@"Generated:\s*\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", html);
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
