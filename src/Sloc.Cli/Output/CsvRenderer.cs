using Sloc.Core.Models;
using System.Text;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as RFC 4180 comma-separated values, suitable for
/// spreadsheets and further data processing. A per-file breakdown is emitted when
/// <c>byFile</c> or <c>detailed</c> is set; otherwise a per-language summary is emitted.
/// </summary>
/// <remarks>
/// Unlike <see cref="JsonRenderer"/>, <see cref="HtmlRenderer"/>, and
/// <see cref="MarkdownRenderer"/>, this renderer intentionally has no report-generation-time
/// field: CSV consumers (spreadsheets, <c>pandas.read_csv</c>, etc.) parse a fixed number of
/// columns starting from the first row, so an extra metadata line would either break parsing
/// or be misread as a malformed data row.
/// </remarks>
public sealed class CsvRenderer : IResultRenderer
{
    private readonly TextWriter _writer;

    /// <summary>
    /// Initializes a new instance of <see cref="CsvRenderer"/>.
    /// </summary>
    /// <param name="writer">
    /// The destination writer. Defaults to <see cref="Console.Out"/> when <see langword="null"/>.
    /// </param>
    public CsvRenderer(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    /// <inheritdoc />
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // Unlike the other formats, CSV's --detailed emits only the language summary (a
        // single well-formed table), so --by-file alone selects the per-file view.
        if (byFile && !detailed)
        {
            RenderByFile(summary, noHealth);
        }
        else
        {
            RenderByLanguage(summary, noHealth);
        }

        if (summary.Skipped.Count > 0)
        {
            RenderSkipped(summary);
        }
    }

    private void RenderSkipped(AnalysisSummary summary)
    {
        _writer.Write("\r\n");
        WriteRow(["Path", "Reason"]);

        foreach (var entry in summary.Skipped)
        {
            WriteRow([entry.Path, entry.Reason]);
        }
    }

    private void RenderByLanguage(AnalysisSummary summary, bool noHealth)
    {
        var header = new List<string> { "Language", "Files", "Code", "Comment", "Blank", "Total" };
        if (!noHealth)
        {
            header.Add("Health");
        }

        WriteRow(header);

        foreach (var language in summary.ByLanguage)
        {
            var row = new List<string>
            {
                language.Language,
                language.Files.ToString(),
                language.Code.ToString(),
                language.Comment.ToString(),
                language.Blank.ToString(),
                language.Total.ToString()
            };
            if (!noHealth)
            {
                row.Add(HealthCell(language.Health));
            }

            WriteRow(row);
        }

        WriteTotalRow(summary, noHealth, summary.FileCount.ToString());
    }

    private void RenderByFile(AnalysisSummary summary, bool noHealth)
    {
        var header = new List<string> { "Path", "Language", "Code", "Comment", "Blank", "Total" };
        if (!noHealth)
        {
            header.Add("Health");
        }

        WriteRow(header);

        foreach (var file in summary.Files)
        {
            var row = new List<string>
            {
                file.Path,
                file.Language,
                file.Code.ToString(),
                file.Comment.ToString(),
                file.Blank.ToString(),
                file.Total.ToString()
            };
            if (!noHealth)
            {
                row.Add(HealthCell(file.Health));
            }

            WriteRow(row);
        }

        WriteTotalRow(summary, noHealth, string.Empty);
    }

    // The second column is the file/language count for the by-language table and blank for
    // the by-file table; every numeric column carries the run-wide total.
    private void WriteTotalRow(AnalysisSummary summary, bool noHealth, string secondColumn)
    {
        var row = new List<string>
        {
            "Total",
            secondColumn,
            summary.Code.ToString(),
            summary.Comment.ToString(),
            summary.Blank.ToString(),
            summary.Total.ToString()
        };
        if (!noHealth)
        {
            row.Add(string.Empty);
        }

        WriteRow(row);
    }

    private static string HealthCell(CommentHealthLevel health) =>
        health == CommentHealthLevel.NotApplicable ? string.Empty : health.ToString();

    private void WriteRow(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Escape(fields[i]));
        }

        // RFC 4180 uses CRLF as the record separator.
        _writer.Write(sb.ToString());
        _writer.Write("\r\n");
    }

    private static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
