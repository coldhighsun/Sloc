using Sloc.Core.Models;
using System.Buffers;
using System.Text;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as GitHub-Flavored Markdown tables, suitable for pasting
/// into READMEs, pull-request descriptions, and issues. A per-file table is emitted when
/// <c>byFile</c> or <c>detailed</c> is set; <c>detailed</c> emits both tables.
/// </summary>
public sealed class MarkdownRenderer : IResultRenderer
{
    /// <summary>
    /// The characters with inline Markdown meaning that <see cref="Escape"/> backslash-escapes:
    /// emphasis (<c>__init__.py</c> would otherwise render as bold), code spans, links, HTML
    /// tags and entities, strikethrough, table cell separators, and the backslash itself
    /// (so a Windows path separator can't escape the character after it).
    /// </summary>
    private static readonly SearchValues<char> MarkdownSpecialChars = SearchValues.Create("\\`*_[]<>&~|");

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
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false, string? sourcePath = null, bool noComplexity = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new StringBuilder();

        sb.AppendLine("# Sloc Report");
        sb.AppendLine();

        var generated = _generatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var sourceMeta = string.IsNullOrEmpty(sourcePath) ? string.Empty : $" | **Source:** {Escape(sourcePath)}";
        sb.AppendLine($"**Generated:** {generated} | **Files:** {summary.FileCount:N0} | **Total Lines:** {summary.Total:N0}{sourceMeta}");
        sb.AppendLine();

        if (detailed || !byFile)
        {
            if (detailed)
            {
                sb.AppendLine("## By Language");
                sb.AppendLine();
            }

            AppendLanguageTable(sb, summary, noHealth, noComplexity);
        }

        if (detailed || byFile)
        {
            if (detailed)
            {
                sb.AppendLine();
                sb.AppendLine("## By File");
                sb.AppendLine();
            }

            AppendFileTable(sb, summary, noHealth, noComplexity);
        }

        if (summary.Skipped.Count > 0)
        {
            AppendSkippedSection(sb, summary);
        }

        _writer.Write(sb.ToString());
    }

    private static void AppendFileTable(StringBuilder sb, AnalysisSummary summary, bool noHealth, bool noComplexity)
    {
        var header = ColumnLayout.FileHeader(noHealth, noComplexity);
        var aligns = new List<string> { ":---", ":---", "---:", "---:", "---:", "---:" };
        ColumnLayout.AddOptional(aligns, noHealth, noComplexity, ":---", "---:");

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
            ColumnLayout.AddOptional(row, noHealth, noComplexity, ColumnLayout.HealthCell(file.Health), ComplexityCell(file.Complexity));

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
        ColumnLayout.AddOptional(totalRow, noHealth, noComplexity, string.Empty, ComplexityCell(summary.ComplexityTotal));

        AppendRow(sb, totalRow);
    }

    private static void AppendLanguageTable(StringBuilder sb, AnalysisSummary summary, bool noHealth, bool noComplexity)
    {
        // ':' alignment markers: language name left, numeric columns right.
        var header = ColumnLayout.LanguageHeader(noHealth, noComplexity);
        var aligns = new List<string> { ":---", "---:", "---:", "---:", "---:", "---:" };
        ColumnLayout.AddOptional(aligns, noHealth, noComplexity, ":---", "---:");

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
            ColumnLayout.AddOptional(row, noHealth, noComplexity, ColumnLayout.HealthCell(language.Health), ComplexityCell(language.ComplexityTotal));

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
        ColumnLayout.AddOptional(totalRow, noHealth, noComplexity, string.Empty, ComplexityCell(summary.ComplexityTotal));

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

    private static string ComplexityCell(int? complexity) =>
        complexity?.ToString("N0") ?? string.Empty;

    /// <summary>
    /// Escapes <paramref name="cell"/> for use as literal text in a Markdown table cell or
    /// list item, backslash-escaping <see cref="MarkdownSpecialChars"/> and replacing line
    /// breaks with spaces.
    /// </summary>
    /// <param name="cell">The raw text, e.g. a file path.</param>
    /// <returns>The escaped text.</returns>
    private static string Escape(string cell)
    {
        var sb = new StringBuilder(cell.Length + 8);
        foreach (var c in cell)
        {
            if (c is '\r' or '\n')
            {
                sb.Append(' ');
                continue;
            }

            if (MarkdownSpecialChars.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}