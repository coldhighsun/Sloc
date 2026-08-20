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
    /// Verifies that <c>detailed</c> emits both the by-language summary and the per-file
    /// array in the same document.
    /// </summary>
    [Fact]
    public void Render_Detailed_EmitsLanguagesAndFilesTogether()
    {
        var summary = BuildSummary();

        using var writer = new StringWriter();
        new JsonRenderer(writer).Render(summary, byFile: false, noHealth: false, detailed: true);
        var root = JsonSerializer.Deserialize<JsonElement>(writer.ToString());

        Assert.Equal(1, root.GetProperty("byLanguage").GetArrayLength());
        Assert.Equal(1, root.GetProperty("files").GetArrayLength());
    }

    /// <summary>
    /// Verifies that <c>generatedAt</c> is stamped with the constructor-supplied time,
    /// formatted as UTC.
    /// </summary>
    [Fact]
    public void Render_GeneratedAt_UsesSuppliedTime()
    {
        var summary = BuildSummary();
        var generatedAt = new DateTimeOffset(2026, 7, 26, 10, 30, 0, TimeSpan.Zero);

        using var writer = new StringWriter();
        new JsonRenderer(writer, generatedAt).Render(summary, byFile: false, noHealth: false);
        var root = JsonSerializer.Deserialize<JsonElement>(writer.ToString());

        Assert.Equal("2026-07-26T10:30:00Z", root.GetProperty("generatedAt").GetString());
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> suppresses only the health field, while percentages
    /// remain in the payload.
    /// </summary>
    [Fact]
    public void Render_NoHealth_KeepsPercentagesButOmitsHealth()
    {
        var summary = BuildSummary();

        var root = Render(summary, byFile: false, noHealth: true);

        Assert.True(root.TryGetProperty("codePct", out _));
        Assert.True(root.TryGetProperty("commentPct", out _));
        var language = root.GetProperty("byLanguage")[0];
        Assert.True(language.TryGetProperty("codePct", out _));
        Assert.False(language.TryGetProperty("health", out _));
    }

    /// <summary>
    /// Verifies that <c>sourcePath</c> is emitted when supplied and omitted when not.
    /// </summary>
    [Fact]
    public void Render_SourcePath_EmittedOnlyWhenSupplied()
    {
        var summary = BuildSummary();

        using var withSource = new StringWriter();
        new JsonRenderer(withSource).Render(summary, byFile: false, noHealth: false, sourcePath: "src");
        var rootWithSource = JsonSerializer.Deserialize<JsonElement>(withSource.ToString());
        Assert.Equal("src", rootWithSource.GetProperty("sourcePath").GetString());

        using var withoutSource = new StringWriter();
        new JsonRenderer(withoutSource).Render(summary, byFile: false, noHealth: false);
        var rootWithoutSource = JsonSerializer.Deserialize<JsonElement>(withoutSource.ToString());
        Assert.False(rootWithoutSource.TryGetProperty("sourcePath", out _));
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

    private static JsonElement Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        using var writer = new StringWriter();
        new JsonRenderer(writer).Render(summary, byFile, noHealth);
        return JsonSerializer.Deserialize<JsonElement>(writer.ToString());
    }
}