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

        try
        {
            return JsonSerializer.Deserialize(json, SlocJsonContext.Default.JsonReport)
                ?? throw new InvalidOperationException($"Baseline '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Baseline '{path}' is not a valid Sloc JSON report: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders the difference between <paramref name="current"/> and
    /// <paramref name="baseline"/> as a console table.
    /// </summary>
    /// <param name="current">The current analysis summary.</param>
    /// <param name="baseline">The baseline report.</param>
    public static void RenderTable(AnalysisSummary current, JsonReport baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        var deltas = Compute(current, baseline);

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

        AnsiConsole.Write(table);
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

        var deltas = Compute(current, baseline);
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

    private static Deltas Compute(AnalysisSummary current, JsonReport baseline)
    {
        var baseByLanguage = (baseline.ByLanguage ?? [])
            .ToDictionary(language => language.Language, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var languages = new List<LanguageDelta>();

        foreach (var language in current.ByLanguage)
        {
            baseByLanguage.TryGetValue(language.Language, out var previous);
            seen.Add(language.Language);
            languages.Add(new LanguageDelta(
                language.Language,
                language.Code - (previous?.Code ?? 0),
                language.Comment - (previous?.Comment ?? 0),
                language.Blank - (previous?.Blank ?? 0),
                language.Total - (previous?.Total ?? 0)));
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

        var total = new LanguageDelta(
            "Total",
            current.Code - baseline.Code,
            current.Comment - baseline.Comment,
            current.Blank - baseline.Blank,
            current.Total - baseline.Total);

        return new Deltas(languages, total);
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
