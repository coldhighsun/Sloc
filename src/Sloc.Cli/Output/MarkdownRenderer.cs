using Sloc.Core.Models;
using System.Text;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as GitHub-Flavored Markdown tables, suitable for pasting
/// into READMEs, pull-request descriptions, and issues. A per-file table is emitted when
/// <c>byFile</c> or <c>detailed</c> is set; <c>detailed</c> emits both tables.
/// </summary>
public sealed class MarkdownRenderer : IResultRenderer
{
    private readonly DateTimeOffset _generatedAt;
    private readonly TextWriter _writer;

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownRenderer"/>.
    /// </summary>
    /// <param name="writer">
    /// The destination writer. Defaults to <see cref="Console.Out"/> when <see langword="null"/>.
    /// </param>
    /// <param name="generatedAt">
    /// The report generation time. Defaults to <see cref="DateTimeOffset.Now"/> when
    /// <see langword="null"/>; a fixed value is mainly useful for deterministic tests.
    /// </param>
    public MarkdownRenderer(TextWriter? writer = null, DateTimeOffset? generatedAt = null)
    {
        _writer = writer ?? Console.Out;
        _generatedAt = generatedAt ?? DateTimeOffset.Now;
    }

    /// <inheritdoc />
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new StringBuilder();

        sb.AppendLine("# Sloc Report");
        sb.AppendLine();

        var generated = _generatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var sourceMeta = string.IsNullOrEmpty(sourcePath) ? string.Empty : $" | **Source:** {sourcePath}";
        sb.AppendLine($"**Generated:** {generated} | **Files:** {summary.FileCount:N0} | **Total Lines:** {summary.Total:N0}{sourceMeta}");
        sb.AppendLine();

        if (detailed || !byFile)
        {
            if (detailed)
            {
                sb.AppendLine("## By Language");
                sb.AppendLine();
            }

            AppendLanguageTable(sb, summary, noHealth);
        }

        if (detailed || byFile)
        {
            if (detailed)
            {
                sb.AppendLine();
                sb.AppendLine("## By File");
                sb.AppendLine();
            }

            AppendFileTable(sb, summary, noHealth);
        }

        if (summary.Skipped.Count > 0)
        {
            AppendSkippedSection(sb, summary);
        }

        _writer.Write(sb.ToString());
    }

    private static void AppendFileTable(StringBuilder sb, AnalysisSummary summary, bool noHealth)
    {
        var header = new List<string> { "Path", "Language", "Code", "Comment", "Blank", "Total" };
        var aligns = new List<string> { ":---", ":---", "---:", "---:", "---:", "---:" };
        if (!noHealth)
        {
            header.Add("Health");
            aligns.Add(":---");
        }

        AppendRow(sb, header);
        AppendRow(sb, aligns);

        foreach (var file in summary.Files)
        {
            var row = new List<string>
            {
                Escape(file.Path),
                Escape(file.Language),
                file.Code.ToString("N0"),
                file.Comment.ToString("N0"),
                file.Blank.ToString("N0"),
                file.Total.ToString("N0")
            };
            if (!noHealth)
            {
                row.Add(HealthCell(file.Health));
            }

            AppendRow(sb, row);
        }

        var totalRow = new List<string>
        {
            "**Total**",
            string.Empty,
            summary.Code.ToString("N0"),
            summary.Comment.ToString("N0"),
            summary.Blank.ToString("N0"),
            summary.Total.ToString("N0")
        };
        if (!noHealth)
        {
            totalRow.Add(string.Empty);
        }

        AppendRow(sb, totalRow);
    }

    private static void AppendLanguageTable(StringBuilder sb, AnalysisSummary summary, bool noHealth)
    {
        // ':' alignment markers: language name left, numeric columns right.
        var header = new List<string> { "Language", "Files", "Code", "Comment", "Blank", "Total" };
        var aligns = new List<string> { ":---", "---:", "---:", "---:", "---:", "---:" };
        if (!noHealth)
        {
            header.Add("Health");
            aligns.Add(":---");
        }

        AppendRow(sb, header);
        AppendRow(sb, aligns);

        foreach (var language in summary.ByLanguage)
        {
            var row = new List<string>
            {
                Escape(language.Language),
                language.Files.ToString("N0"),
                language.Code.ToString("N0"),
                language.Comment.ToString("N0"),
                language.Blank.ToString("N0"),
                language.Total.ToString("N0")
            };
            if (!noHealth)
            {
                row.Add(HealthCell(language.Health));
            }

            AppendRow(sb, row);
        }

        var totalRow = new List<string>
        {
            "**Total**",
            summary.FileCount.ToString("N0"),
            summary.Code.ToString("N0"),
            summary.Comment.ToString("N0"),
            summary.Blank.ToString("N0"),
            summary.Total.ToString("N0")
        };
        if (!noHealth)
        {
            totalRow.Add(string.Empty);
        }

        AppendRow(sb, totalRow);
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        sb.Append("| ");
        sb.AppendJoin(" | ", cells);
        sb.AppendLine(" |");
    }

    private static void AppendSkippedSection(StringBuilder sb, AnalysisSummary summary)
    {
        sb.AppendLine();
        sb.AppendLine("## Skipped");
        sb.AppendLine();

        foreach (var entry in summary.Skipped)
        {
            sb.Append("- ");
            sb.Append(Escape(entry.Path));
            sb.Append(" — ");
            sb.AppendLine(Escape(entry.Reason));
        }
    }

    private static string Escape(string cell) =>
        cell.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string HealthCell(CommentHealthLevel health) =>
                health == CommentHealthLevel.NotApplicable ? string.Empty : health.ToString();
}