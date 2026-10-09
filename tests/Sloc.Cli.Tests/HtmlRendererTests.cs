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

    /// <summary>
    /// Verifies that the by-file tree keeps folders differing only by case (possible in a
    /// <c>--git-hash</c> tree from a case-sensitive filesystem) as separate folders.
    /// </summary>
    [Fact]
    public void Render_ByFileCaseVariantFolders_KeepsThemSeparate()
    {
        var summary = new AnalysisSummary(
        [
            new FileAnalysis { Path = Path.Combine("Src", "upper.js"), Language = "JavaScript", Code = 1, Comment = 0, Blank = 0 },
            new FileAnalysis { Path = Path.Combine("src", "lower.js"), Language = "JavaScript", Code = 1, Comment = 0, Blank = 0 }
        ]);
        using var writer = new StringWriter();

        new HtmlRenderer(writer).Render(summary, byFile: true, noHealth: true);
        var html = writer.ToString();

        Assert.Contains("<span>Src</span>", html);
        Assert.Contains("<span>src</span>", html);
    }

    /// <summary>
    /// Verifies that a <c>top</c>-truncated language table is followed by a note that the
    /// totals cover all languages, and that an untruncated one has no note.
    /// </summary>
    [Fact]
    public void Render_TruncatedLanguages_WritesNote()
    {
        var files = TestData.TwoLanguageFiles();
        using var truncated = new StringWriter();
        using var complete = new StringWriter();

        new HtmlRenderer(truncated).Render(new AnalysisSummary(files, top: 1), byFile: false, noHealth: true);
        new HtmlRenderer(complete).Render(new AnalysisSummary(files), byFile: false, noHealth: true);

        Assert.Contains("Showing top 1 of 2 languages; totals cover all languages.", truncated.ToString());
        Assert.DoesNotContain("Showing top", complete.ToString());
    }

    /// <summary>
    /// Verifies that in detailed output the truncation note is a <c>table-note</c> paragraph
    /// placed after the language table and before the By File section.
    /// </summary>
    [Fact]
    public void Render_DetailedTruncatedLanguages_PlacesNoteBetweenTables()
    {
        using var writer = new StringWriter();

        new HtmlRenderer(writer).Render(
            new AnalysisSummary(TestData.TwoLanguageFiles(), top: 1), byFile: false, noHealth: true, detailed: true);
        var html = writer.ToString();

        var languageTable = html.IndexOf("<h2>By Language</h2>", StringComparison.Ordinal);
        var note = html.IndexOf("<p class=\"table-note\">Showing top 1 of 2 languages", StringComparison.Ordinal);
        var fileSection = html.IndexOf("<h2>By File</h2>", StringComparison.Ordinal);
        Assert.True(languageTable >= 0 && languageTable < note, "note should follow the language table");
        Assert.True(note < fileSection, "note should precede the By File section");
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
