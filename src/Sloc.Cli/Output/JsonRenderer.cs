using Sloc.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as indented JSON.
/// </summary>
public sealed class JsonRenderer : IResultRenderer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var payload = new
        {
            summary.FileCount,
            summary.Code,
            CodePct = Pct(summary.Code, summary.Total),
            summary.Comment,
            CommentPct = Pct(summary.Comment, summary.Total),
            summary.Blank,
            BlankPct = Pct(summary.Blank, summary.Total),
            summary.Total,
            ByLanguage = byFile ? null : summary.ByLanguage.Select(language => new
            {
                language.Language,
                language.Files,
                language.Code,
                CodePct = Pct(language.Code, language.Total),
                language.Comment,
                CommentPct = Pct(language.Comment, language.Total),
                language.Blank,
                BlankPct = Pct(language.Blank, language.Total),
                language.Total
            }),
            Files = byFile
                ? summary.Files.Select(file => new
                {
                    file.Path,
                    file.Language,
                    file.Code,
                    CodePct = Pct(file.Code, file.Total),
                    file.Comment,
                    CommentPct = Pct(file.Comment, file.Total),
                    file.Blank,
                    BlankPct = Pct(file.Blank, file.Total),
                    file.Total
                })
                : null,
            Skipped = summary.Skipped.Select(entry => new
            {
                entry.Path,
                entry.Reason
            })
        };

        _writer.WriteLine(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    private static double Pct(int count, int total) =>
        total == 0 ? 0.0 : Math.Round((double)count / total * 100, 1);
}