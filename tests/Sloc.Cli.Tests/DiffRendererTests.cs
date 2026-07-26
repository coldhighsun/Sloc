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
