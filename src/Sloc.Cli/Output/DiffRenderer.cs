using Sloc.Core.Models;
using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sloc.Cli.Output;

/// <summary>
/// Compares the current analysis against a previously saved JSON report and renders the
/// difference, either as a console table or as machine-readable JSON.
/// </summary>
internal static class DiffRenderer
{
    /// <summary>
    /// The row label for languages diffed as one group against a <c>--top</c>-truncated
    /// baseline's unlisted remainder. Parenthesized so it can't collide with a real language
    /// name (including <c>Other</c>, the <c>--all</c> group).
    /// </summary>
    internal const string OtherLanguagesLabel = "(other languages)";

    /// <summary>
    /// Loads a baseline report previously produced by <see cref="JsonRenderer"/>.
    /// </summary>
    /// <param name="path">The path to the baseline JSON file.</param>
    /// <returns>The parsed baseline report.</returns>
    /// <exception cref="InvalidOperationException">
    /// The file could not be read or parsed as a Sloc JSON report.
    /// </exception>
    public static JsonReport Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read baseline '{path}': {ex.Message}");
        }

        JsonReport report;
        try
        {
            report = JsonSerializer.Deserialize(json, SlocJsonContext.Default.JsonReport)
                ?? throw new InvalidOperationException($"Baseline '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Baseline '{path}' is not a valid Sloc JSON report: {ex.Message}");
        }

        // An empty breakdown is still a breakdown: a report of a directory with no source
        // files (e.g. a new project) has "byLanguage": [] and is a valid baseline.
        if (report.ByLanguage is null && report.Files is null)
        {
            throw new InvalidOperationException(
                $"Baseline '{path}' has no per-language or per-file breakdown to diff against.");
        }

        return report;
    }

    /// <summary>
    /// Renders the difference between <paramref name="current"/> and
    /// <paramref name="baseline"/> as a console table.
    /// </summary>
    /// <param name="current">The current analysis summary.</param>
    /// <param name="baseline">The baseline report.</param>
    /// <param name="console">
    /// The console to write to. Defaults to <see cref="AnsiConsole.Console"/> when
    /// <see langword="null"/>; supply a test console to capture the output.
    /// </param>
    public static void RenderTable(AnalysisSummary current, JsonReport baseline, IAnsiConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        RenderTable(Compute(current, baseline), console);
    }

    /// <summary>
    /// Renders the difference between <paramref name="current"/> and <paramref name="baseline"/>
    /// as a console table, where <paramref name="baseline"/> is itself a freshly analyzed
    /// summary (e.g. a git commit analyzed via <c>--compare-to</c>) rather than a saved report.
    /// </summary>
    /// <param name="current">The current analysis summary.</param>
    /// <param name="baseline">The baseline analysis summary.</param>
    /// <param name="console">
    /// The console to write to. Defaults to <see cref="AnsiConsole.Console"/> when
    /// <see langword="null"/>; supply a test console to capture the output.
    /// </param>
    public static void RenderTable(AnalysisSummary current, AnalysisSummary baseline, IAnsiConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        RenderTable(Compute(current, baseline), console);
    }

    private static void RenderTable(Deltas deltas, IAnsiConsole? console)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.Caption(new TableTitle("[grey]Δ vs baseline[/]"));
        table.AddColumn("Language");
        table.AddColumn(new TableColumn("Code Δ").RightAligned());
        table.AddColumn(new TableColumn("Comment Δ").RightAligned());
        table.AddColumn(new TableColumn("Blank Δ").RightAligned());
        table.AddColumn(new TableColumn("Total Δ").RightAligned());

        foreach (var delta in deltas.Languages)
        {
            table.AddRow(
                Markup.Escape(delta.Language),
                Signed(delta.Code),
                Signed(delta.Comment),
                Signed(delta.Blank),
                Signed(delta.Total));
        }

        table.AddEmptyRow();
        table.AddRow(
            "[bold]Total[/]",
            SignedBold(deltas.Total.Code),
            SignedBold(deltas.Total.Comment),
            SignedBold(deltas.Total.Blank),
            SignedBold(deltas.Total.Total));

        (console ?? AnsiConsole.Console).Write(table);
    }

    /// <summary>
    /// Renders the difference between <paramref name="current"/> and
    /// <paramref name="baseline"/> as JSON to the supplied writer.
    /// </summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="current">The current analysis summary.</param>
    /// <param name="baseline">The baseline report.</param>
    public static void RenderJson(TextWriter writer, AnalysisSummary current, JsonReport baseline)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        RenderJson(writer, Compute(current, baseline));
    }

    /// <summary>
    /// Renders the difference between <paramref name="current"/> and <paramref name="baseline"/>
    /// as JSON, where <paramref name="baseline"/> is itself a freshly analyzed summary (e.g. a
    /// git commit analyzed via <c>--compare-to</c>) rather than a saved report.
    /// </summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="current">The current analysis summary.</param>
    /// <param name="baseline">The baseline analysis summary.</param>
    public static void RenderJson(TextWriter writer, AnalysisSummary current, AnalysisSummary baseline)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        RenderJson(writer, Compute(current, baseline));
    }

    private static void RenderJson(TextWriter writer, Deltas deltas)
    {
        var payload = new JsonDiff
        {
            Total = new JsonDiffTotals
            {
                Code = deltas.Total.Code,
                Comment = deltas.Total.Comment,
                Blank = deltas.Total.Blank,
                Total = deltas.Total.Total
            },
            ByLanguage = deltas.Languages.Select(delta => new JsonLanguageDiff
            {
                Language = delta.Language,
                Code = delta.Code,
                Comment = delta.Comment,
                Blank = delta.Blank,
                Total = delta.Total
            }).ToList()
        };

        writer.WriteLine(JsonSerializer.Serialize(payload, SlocDiffJsonContext.Default.JsonDiff));
    }

    private static Deltas Compute(AnalysisSummary current, JsonReport baseline) =>
        Compute(
            current,
            ResolveBaselineLanguages(baseline, out var remainder).Select(language => (language.Language, language.Code, language.Comment, language.Blank, language.Total)),
            baseline.Code, baseline.Comment, baseline.Blank, baseline.Total,
            remainder);

    private static Deltas Compute(AnalysisSummary current, AnalysisSummary baseline) =>
        Compute(
            current,
            baseline.ByLanguage.Select(language => (language.Language, language.Code, language.Comment, language.Blank, language.Total)),
            baseline.Code, baseline.Comment, baseline.Blank, baseline.Total,
            baselineRemainder: null);

    // baselineRemainder: the baseline's lines not covered by baselineLanguages (a report saved
    // with --top), or null when that breakdown is complete. When set, current languages
    // missing from the baseline can't be told apart from languages that were merely cut off,
    // so they are diffed together against the remainder in a single OtherLanguagesLabel row
    // instead of being reported as entirely new.
    private static Deltas Compute(
        AnalysisSummary current,
        IEnumerable<(string Language, int Code, int Comment, int Blank, int Total)> baselineLanguages,
        int baselineCode, int baselineComment, int baselineBlank, int baselineTotal,
        LanguageDelta? baselineRemainder)
    {
        // Built manually (last entry wins) instead of ToDictionary: a baseline JSON file may
        // be hand-edited or produced by another tool/version and contain two language
        // entries differing only by case, which ToDictionary's case-insensitive comparer
        // would otherwise throw on.
        var baseByLanguage = new Dictionary<string, (string Language, int Code, int Comment, int Blank, int Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in baselineLanguages)
        {
            baseByLanguage[language.Language] = language;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var languages = new List<LanguageDelta>();
        int otherCode = 0, otherComment = 0, otherBlank = 0, otherTotal = 0;

        foreach (var language in current.ByLanguage)
        {
            var hasPrevious = baseByLanguage.TryGetValue(language.Language, out var previous);
            seen.Add(language.Language);
            if (!hasPrevious && baselineRemainder is not null)
            {
                otherCode += language.Code;
                otherComment += language.Comment;
                otherBlank += language.Blank;
                otherTotal += language.Total;
                continue;
            }

            languages.Add(new LanguageDelta(
                language.Language,
                language.Code - (hasPrevious ? previous.Code : 0),
                language.Comment - (hasPrevious ? previous.Comment : 0),
                language.Blank - (hasPrevious ? previous.Blank : 0),
                language.Total - (hasPrevious ? previous.Total : 0)));
        }

        // Languages present only in the baseline (fully removed).
        foreach (var previous in baseByLanguage.Values)
        {
            if (seen.Contains(previous.Language))
            {
                continue;
            }

            languages.Add(new LanguageDelta(
                previous.Language,
                -previous.Code,
                -previous.Comment,
                -previous.Blank,
                -previous.Total));
        }

        if (baselineRemainder is not null)
        {
            languages.Add(new LanguageDelta(
                OtherLanguagesLabel,
                otherCode - baselineRemainder.Code,
                otherComment - baselineRemainder.Comment,
                otherBlank - baselineRemainder.Blank,
                otherTotal - baselineRemainder.Total));
        }

        var total = new LanguageDelta(
            "Total",
            current.Code - baselineCode,
            current.Comment - baselineComment,
            current.Blank - baselineBlank,
            current.Total - baselineTotal);

        return new Deltas(languages, total);
    }

    /// <summary>
    /// Resolves the per-language breakdown of a baseline report. Reports saved with
    /// <c>--by-file</c> have no <see cref="JsonReport.ByLanguage"/>, and reports saved with
    /// <c>--top</c> list only some languages; in either case the breakdown is reconstructed
    /// by aggregating the per-file entries when the report has them, so language deltas stay
    /// correct.
    /// </summary>
    /// <param name="baseline">The baseline report.</param>
    /// <param name="remainder">
    /// When the report is marked <see cref="JsonReport.ByLanguageTruncated"/> and has no
    /// per-file entries to rebuild its languages from, receives the lines of the unlisted
    /// languages; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>The per-language statistics of the baseline.</returns>
    private static IReadOnlyList<JsonLanguage> ResolveBaselineLanguages(JsonReport baseline, out LanguageDelta? remainder)
    {
        remainder = null;

        if (baseline.ByLanguage is { Count: > 0 } byLanguage)
        {
            // Only a report that says it was cut short by --top is treated as partial; a
            // report whose totals merely don't reconcile (hand-edited, another tool) is taken
            // at face value rather than guessed at.
            if (baseline.ByLanguageTruncated != true)
            {
                return byLanguage;
            }

            if (baseline.Files is not { Count: > 0 })
            {
                remainder = new LanguageDelta(
                    OtherLanguagesLabel,
                    baseline.Code - byLanguage.Sum(language => language.Code),
                    baseline.Comment - byLanguage.Sum(language => language.Comment),
                    baseline.Blank - byLanguage.Sum(language => language.Blank),
                    baseline.Total - byLanguage.Sum(language => language.Total));
                return byLanguage;
            }
        }

        if (baseline.Files is not { Count: > 0 })
        {
            return [];
        }

        return baseline.Files
            .GroupBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .Select(group => new JsonLanguage
            {
                Language = group.Key,
                Files = group.Count(),
                Code = group.Sum(file => file.Code),
                Comment = group.Sum(file => file.Comment),
                Blank = group.Sum(file => file.Blank),
                Total = group.Sum(file => file.Total)
            })
            .ToList();
    }

    private static string Signed(int value) => value switch
    {
        > 0 => $"[green]+{value:N0}[/]",
        < 0 => $"[red]{value:N0}[/]",
        _ => "[grey]0[/]"
    };

    private static string SignedBold(int value) => value switch
    {
        > 0 => $"[bold green]+{value:N0}[/]",
        < 0 => $"[bold red]{value:N0}[/]",
        _ => "[bold grey]0[/]"
    };

    private sealed record LanguageDelta(string Language, int Code, int Comment, int Blank, int Total);

    private sealed record Deltas(IReadOnlyList<LanguageDelta> Languages, LanguageDelta Total);
}

/// <summary>
/// The JSON diff payload: overall and per-language line-count deltas.
/// </summary>
internal sealed class JsonDiff
{
    public required JsonDiffTotals Total { get; init; }

    public required IReadOnlyList<JsonLanguageDiff> ByLanguage { get; init; }
}

/// <summary>
/// Overall line-count deltas in the JSON diff payload.
/// </summary>
internal sealed class JsonDiffTotals
{
    public int Code { get; init; }

    public int Comment { get; init; }

    public int Blank { get; init; }

    public int Total { get; init; }
}

/// <summary>
/// Per-language line-count deltas in the JSON diff payload.
/// </summary>
internal sealed class JsonLanguageDiff
{
    public required string Language { get; init; }

    public int Code { get; init; }

    public int Comment { get; init; }

    public int Blank { get; init; }

    public int Total { get; init; }
}

/// <summary>
/// Source-generated serialization context for the JSON diff payload.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonDiff))]
internal sealed partial class SlocDiffJsonContext : JsonSerializerContext
{
}
