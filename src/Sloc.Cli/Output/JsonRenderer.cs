using Sloc.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as indented JSON.
/// </summary>
public sealed class JsonRenderer : IResultRenderer
{
    private readonly TextWriter _writer;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonRenderer"/>.
    /// </summary>
    /// <param name="writer">
    /// The destination writer. Defaults to <see cref="Console.Out"/> when <see langword="null"/>.
    /// </param>
    public JsonRenderer(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    /// <inheritdoc />
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var includeLanguages = detailed || !byFile;
        var includeFiles = detailed || byFile;

        var report = new JsonReport
        {
            FileCount = summary.FileCount,
            Code = summary.Code,
            CodePct = Pct(summary.Code, summary.Total),
            Comment = summary.Comment,
            CommentPct = Pct(summary.Comment, summary.Total),
            Blank = summary.Blank,
            BlankPct = Pct(summary.Blank, summary.Total),
            Total = summary.Total,
            ByLanguage = !includeLanguages ? null : summary.ByLanguage.Select(language => new JsonLanguage
            {
                Language = language.Language,
                Files = language.Files,
                Code = language.Code,
                CodePct = Pct(language.Code, language.Total),
                Comment = language.Comment,
                CommentPct = Pct(language.Comment, language.Total),
                Blank = language.Blank,
                BlankPct = Pct(language.Blank, language.Total),
                Total = language.Total,
                Health = Health(language.Health, noHealth)
            }).ToList(),
            Files = includeFiles
                ? summary.Files.Select(file => new JsonFile
                {
                    Path = file.Path,
                    Language = file.Language,
                    Code = file.Code,
                    CodePct = Pct(file.Code, file.Total),
                    Comment = file.Comment,
                    CommentPct = Pct(file.Comment, file.Total),
                    Blank = file.Blank,
                    BlankPct = Pct(file.Blank, file.Total),
                    Total = file.Total,
                    Health = Health(file.Health, noHealth)
                }).ToList()
                : null,
            Skipped = summary.Skipped.Select(entry => new JsonSkipped
            {
                Path = entry.Path,
                Reason = entry.Reason
            }).ToList()
        };

        _writer.WriteLine(JsonSerializer.Serialize(report, SlocJsonContext.Default.JsonReport));
    }

    private static double Pct(int count, int total) =>
        total == 0 ? 0.0 : Math.Round((double)count / total * 100, 1);

    private static string? Health(CommentHealthLevel health, bool noHealth) =>
        noHealth || health == CommentHealthLevel.NotApplicable ? null : health.ToString();
}

/// <summary>
/// The top-level JSON report payload.
/// </summary>
internal sealed class JsonReport
{
    public int FileCount { get; init; }

    public int Code { get; init; }

    public double? CodePct { get; init; }

    public int Comment { get; init; }

    public double? CommentPct { get; init; }

    public int Blank { get; init; }

    public double? BlankPct { get; init; }

    public int Total { get; init; }

    public IReadOnlyList<JsonLanguage>? ByLanguage { get; init; }

    public IReadOnlyList<JsonFile>? Files { get; init; }

    public IReadOnlyList<JsonSkipped> Skipped { get; init; } = [];
}

/// <summary>
/// Per-language statistics in the JSON report.
/// </summary>
internal sealed class JsonLanguage
{
    public required string Language { get; init; }

    public int Files { get; init; }

    public int Code { get; init; }

    public double? CodePct { get; init; }

    public int Comment { get; init; }

    public double? CommentPct { get; init; }

    public int Blank { get; init; }

    public double? BlankPct { get; init; }

    public int Total { get; init; }

    public string? Health { get; init; }
}

/// <summary>
/// Per-file statistics in the JSON report.
/// </summary>
internal sealed class JsonFile
{
    public required string Path { get; init; }

    public required string Language { get; init; }

    public int Code { get; init; }

    public double? CodePct { get; init; }

    public int Comment { get; init; }

    public double? CommentPct { get; init; }

    public int Blank { get; init; }

    public double? BlankPct { get; init; }

    public int Total { get; init; }

    public string? Health { get; init; }
}

/// <summary>
/// A skipped path in the JSON report.
/// </summary>
internal sealed class JsonSkipped
{
    public required string Path { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Source-generated serialization context for the JSON report, so serialization is
/// trimming- and single-file-safe (no runtime reflection).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonReport))]
internal sealed partial class SlocJsonContext : JsonSerializerContext
{
}
