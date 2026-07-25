using Sloc.Cli.Output;
using Sloc.Core.Models;
using System.Text.Json;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="JsonRenderer"/>, asserting on the emitted JSON shape.
/// </summary>
public class JsonRendererTests
{
    /// <summary>
    /// Verifies that the by-language payload includes totals, percentages, and health,
    /// and omits the per-file array.
    /// </summary>
    [Fact]
    public void Render_ByLanguage_EmitsTotalsPercentagesAndHealth()
    {
        var summary = BuildSummary();

        var root = Render(summary, byFile: false, noHealth: false);

        Assert.Equal(1, root.GetProperty("fileCount").GetInt32());
        Assert.Equal(6, root.GetProperty("total").GetInt32());
        Assert.True(root.TryGetProperty("codePct", out _));

        var languages = root.GetProperty("byLanguage");
        Assert.Equal(1, languages.GetArrayLength());
        var language = languages[0];
        Assert.Equal("C#", language.GetProperty("language").GetString());
        Assert.True(language.TryGetProperty("health", out _));
        Assert.False(root.TryGetProperty("files", out _));
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> suppresses both the percentage and health fields.
    /// </summary>
    [Fact]
    public void Render_NoHealth_OmitsPercentagesAndHealth()
    {
        var summary = BuildSummary();

        var root = Render(summary, byFile: false, noHealth: true);

        Assert.False(root.TryGetProperty("codePct", out _));
        Assert.False(root.TryGetProperty("commentPct", out _));
        var language = root.GetProperty("byLanguage")[0];
        Assert.False(language.TryGetProperty("codePct", out _));
        Assert.False(language.TryGetProperty("health", out _));
    }

    /// <summary>
    /// Verifies that the by-file payload emits the per-file array and omits by-language.
    /// </summary>
    [Fact]
    public void Render_ByFile_EmitsFilesAndOmitsByLanguage()
    {
        var summary = BuildSummary();

        var root = Render(summary, byFile: true, noHealth: false);

        Assert.False(root.TryGetProperty("byLanguage", out _));
        var files = root.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("a.cs", files[0].GetProperty("path").GetString());
    }

    private static JsonElement Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        using var writer = new StringWriter();
        new JsonRenderer(writer).Render(summary, byFile, noHealth);
        return JsonSerializer.Deserialize<JsonElement>(writer.ToString());
    }

    private static AnalysisSummary BuildSummary()
    {
        var file = new FileAnalysis
        {
            Path = "a.cs",
            Language = "C#",
            Code = 3,
            Comment = 2,
            Blank = 1
        };
        return new AnalysisSummary([file]);
    }
}
