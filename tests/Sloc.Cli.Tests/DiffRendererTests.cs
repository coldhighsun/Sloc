using Sloc.Cli.Output;
using Sloc.Core.Models;
using Spectre.Console.Testing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="DiffRenderer"/>, covering the console table diff and
/// the round-trip through a saved JSON baseline.
/// </summary>
public class DiffRendererTests
{
    /// <summary>
    /// Verifies that a table diff against a saved baseline shows the signed per-language
    /// and total deltas.
    /// </summary>
    [Fact]
    public void RenderTable_AgainstSavedBaseline_ShowsSignedDeltas()
    {
        var baselinePath = WriteBaseline(code: 50, comment: 10, blank: 3);
        try
        {
            var baseline = DiffRenderer.Load(baselinePath);
            var current = BuildSummary(code: 80, comment: 20, blank: 5);

            var console = new TestConsole();
            DiffRenderer.RenderTable(current, baseline, console);

            var output = console.Output;
            Assert.Contains("Δ vs baseline", output);
            Assert.Contains("+30", output); // code delta 80 - 50
            Assert.Contains("+10", output); // comment delta 20 - 10
        }
        finally
        {
            File.Delete(baselinePath);
        }
    }

    /// <summary>
    /// Verifies that loading a baseline JSON with neither <c>byLanguage</c> nor <c>files</c>
    /// populated throws rather than silently succeeding, since diffing against it would
    /// otherwise report every current language as entirely new.
    /// </summary>
    [Fact]
    public void Load_BaselineWithNoBreakdown_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-empty-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"code":0,"comment":0,"blank":0,"total":0}""");
        try
        {
            Assert.Throws<InvalidOperationException>(() => DiffRenderer.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that loading a baseline file containing syntactically invalid JSON throws
    /// with a message identifying it as an unparseable Sloc report, rather than propagating
    /// the raw <see cref="System.Text.Json.JsonException"/>.
    /// </summary>
    [Fact]
    public void Load_MalformedJson_ThrowsWithFormatMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-malformed-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DiffRenderer.Load(path));
            Assert.Contains("is not a valid Sloc JSON report", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a baseline whose <c>byLanguage</c> array contains two entries differing
    /// only by case (e.g. hand-edited, or produced by another tool/version) does not crash
    /// the diff, and that the later entry wins.
    /// </summary>
    [Fact]
    public void RenderTable_BaselineWithCaseVariantDuplicateLanguages_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-dup-lang-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
                "generatedAt": "2024-01-01T00:00:00Z",
                "fileCount": 2,
                "code": 90, "comment": 10, "blank": 0, "total": 100,
                "byLanguage": [
                    { "language": "C#", "files": 1, "code": 50, "comment": 5, "blank": 0, "total": 55, "health": "Healthy" },
                    { "language": "c#", "files": 1, "code": 40, "comment": 5, "blank": 0, "total": 45, "health": "Healthy" }
                ]
            }
            """);

        try
        {
            var baseline = DiffRenderer.Load(path);
            var current = BuildSummary(code: 80, comment: 20, blank: 5);

            var console = new TestConsole();
            var exception = Record.Exception(() => DiffRenderer.RenderTable(current, baseline, console));

            Assert.Null(exception);
            Assert.Contains("+40", console.Output); // code delta 80 - 40 (later "c#" entry wins)
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a baseline saved with <c>--top</c> (its <c>byLanguage</c> truncated, no
    /// per-file breakdown) does not report current languages missing from it as entirely
    /// new: they are diffed as one group against the baseline's untruncated remainder.
    /// </summary>
    [Fact]
    public void RenderJson_TopTruncatedBaseline_DiffsMissingLanguagesAgainstRemainder()
    {
        var path = WriteBaseline(TwoLanguageSummary(pythonCode: 30, top: 1), detailed: false);
        try
        {
            var baseline = DiffRenderer.Load(path);
            var current = TwoLanguageSummary(pythonCode: 40);

            var diff = RenderJsonDiff(current, baseline);

            var languages = diff.GetProperty("byLanguage").EnumerateArray().ToList();
            Assert.DoesNotContain(languages, l => l.GetProperty("language").GetString() == "Python");
            var csharp = Assert.Single(languages, l => l.GetProperty("language").GetString() == "C#");
            Assert.Equal(0, csharp.GetProperty("code").GetInt32());
            var other = Assert.Single(languages, l => l.GetProperty("language").GetString() == DiffRenderer.OtherLanguagesLabel);
            Assert.Equal(10, other.GetProperty("code").GetInt32());
            Assert.Equal(10, diff.GetProperty("total").GetProperty("code").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a baseline saved with <c>--top</c> that also carries a per-file breakdown
    /// (<c>--detailed</c>) rebuilds its full per-language breakdown from the files, so every
    /// language still gets its own exact delta.
    /// </summary>
    [Fact]
    public void RenderJson_TopTruncatedBaselineWithFiles_RebuildsLanguagesFromFiles()
    {
        var path = WriteBaseline(TwoLanguageSummary(pythonCode: 30, top: 1), detailed: true);
        try
        {
            var baseline = DiffRenderer.Load(path);
            var current = TwoLanguageSummary(pythonCode: 40);

            var diff = RenderJsonDiff(current, baseline);

            var languages = diff.GetProperty("byLanguage").EnumerateArray().ToList();
            var python = Assert.Single(languages, l => l.GetProperty("language").GetString() == "Python");
            Assert.Equal(10, python.GetProperty("code").GetInt32());
            Assert.DoesNotContain(languages, l => l.GetProperty("language").GetString() == DiffRenderer.OtherLanguagesLabel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a baseline whose listed languages merely don't add up to its totals
    /// (hand-edited, or from another tool), without being marked as truncated, is taken at
    /// face value: new current languages are still listed individually.
    /// </summary>
    [Fact]
    public void RenderJson_UnmarkedBaselineWithUnreconciledTotals_ListsNewLanguages()
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-unreconciled-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
                "generatedAt": "2024-01-01T00:00:00Z",
                "fileCount": 2,
                "code": 80, "comment": 0, "blank": 0, "total": 80,
                "byLanguage": [
                    { "language": "C#", "files": 1, "code": 50, "comment": 0, "blank": 0, "total": 50 }
                ]
            }
            """);
        try
        {
            var baseline = DiffRenderer.Load(path);
            var current = TwoLanguageSummary(pythonCode: 40);

            var diff = RenderJsonDiff(current, baseline);

            var languages = diff.GetProperty("byLanguage").EnumerateArray().ToList();
            var python = Assert.Single(languages, l => l.GetProperty("language").GetString() == "Python");
            Assert.Equal(40, python.GetProperty("code").GetInt32());
            Assert.DoesNotContain(languages, l => l.GetProperty("language").GetString() == DiffRenderer.OtherLanguagesLabel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static System.Text.Json.JsonElement RenderJsonDiff(AnalysisSummary current, JsonReport baseline)
    {
        using var writer = new StringWriter();
        DiffRenderer.RenderJson(writer, current, baseline);
        return System.Text.Json.JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }

    private static AnalysisSummary TwoLanguageSummary(int pythonCode, int? top = null) =>
        new(
            [
                new FileAnalysis { Path = "a.cs", Language = "C#", Code = 50, Comment = 0, Blank = 0 },
                new FileAnalysis { Path = "b.py", Language = "Python", Code = pythonCode, Comment = 0, Blank = 0 }
            ],
            top: top);

    private static string WriteBaseline(AnalysisSummary summary, bool detailed)
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        using (var writer = new StreamWriter(path))
        {
            new JsonRenderer(writer).Render(summary, byFile: false, noHealth: false, detailed: detailed);
        }

        return path;
    }

    private static string WriteBaseline(int code, int comment, int blank)
    {
        var path = Path.Combine(Path.GetTempPath(), "sloc-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        using (var writer = new StreamWriter(path))
        {
            new JsonRenderer(writer).Render(BuildSummary(code, comment, blank), byFile: false, noHealth: false);
        }

        return path;
    }

    private static AnalysisSummary BuildSummary(int code, int comment, int blank)
    {
        var file = new FileAnalysis
        {
            Path = "a.cs",
            Language = "C#",
            Code = code,
            Comment = comment,
            Blank = blank
        };
        return new AnalysisSummary([file]);
    }
}
